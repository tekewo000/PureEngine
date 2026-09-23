using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using PureEngine.Core;

namespace PureEngine.Editor;

public partial class MainWindow
{
    private StackPanel BuildSpriteEditor(object component, MemberInfo member, string automationName)
    {
        var root = new StackPanel { Spacing = 4 };
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        combo.Classes.Add("inspectorCombo");
        combo.SetValue(AutomationProperties.NameProperty, automationName);
        var info = new TextBlock { Classes = { "memberType" }, TextWrapping = TextWrapping.Wrap };
        info.SetValue(AutomationProperties.NameProperty, $"{automationName}.Info");
        root.Children.Add(combo);
        root.Children.Add(info);
        void refreshOptions()
        {
            var current = (Sprite?)GetMemberValue(component, member);
            List<SpriteOption> options = [new SpriteOption(null, "None")];
            foreach (var entry in AssetImageEntries())
                options.Add(new SpriteOption(entry.Id, entry.RelativePath));
            SpriteOption selected;
            if (current is null)
            {
                selected = options[0];
                info.Text = "No sprite.";
                ToolTip.SetTip(combo, $"{member.Name} : Sprite — Select an image or None");
            }
            else
            {
                var match = options.FirstOrDefault(option => option.Id == current.ImageId);
                if (match is null)
                {
                    match = new SpriteOption(current.ImageId, $"Missing: {current.ImageId:D}");
                    options.Add(match);
                    info.Text = current.SourceRect is { } rect
                        ? $"Missing image {current.ImageId:D} (crop {rect.X},{rect.Y},{rect.Width}x{rect.Height} kept)."
                        : $"Missing image {current.ImageId:D}. ID is kept.";
                }
                else if (current.SourceRect is { } rect)
                {
                    info.Text = $"Crop {rect.X},{rect.Y},{rect.Width}x{rect.Height} from {match.Display}. Picking another image uses the whole image.";
                }
                else
                {
                    info.Text = match.Display;
                }
                selected = match;
                ToolTip.SetTip(combo, $"{member.Name} : Sprite — {info.Text}");
            }
            combo.ItemsSource = options;
            combo.SelectedItem = selected;
        }
        combo.SelectionChanged += (_, _) =>
        {
            if (IsPlaying) return;
            if (combo.SelectedItem is not SpriteOption option) return;
            var current = (Sprite?)GetMemberValue(component, member);
            if (option.Id is null)
            {
                if (current is null) return;
                SetMemberValue(component, member, null);
            }
            else
            {
                if (current?.ImageId == option.Id && current.SourceRect is null) return;
                if (current?.ImageId == option.Id) return;
                SetMemberValue(component, member, new Sprite(option.Id.Value));
            }
            refreshOptions();
            if (FindOwner(component) is { } owner) RefreshUiWarnings(owner);
        };
        refreshOptions();
        return root;
    }

    internal sealed record SpriteOption(Guid? Id, string Display)
    {
        public override string ToString() => Display;
    }

    private SceneObject? FindOwner(object component) =>
        _editScene.Current.Objects.FirstOrDefault(item =>
            item.Components.Any(candidate => ReferenceEquals(candidate, component)));

    private TextBlock BuildUiWarning(SceneObject item, object component)
    {
        var warning = new TextBlock
        {
            Classes = { "hint" },
            Foreground = new SolidColorBrush(Color.Parse("#E0B45A")),
            TextWrapping = TextWrapping.Wrap,
        };
        warning.SetValue(AutomationProperties.NameProperty, $"{component.GetType().Name}.Requirements");
        UpdateUiWarning(warning, item, component);
        return warning;
    }

    private void UpdateUiWarning(TextBlock warning, SceneObject item, object component)
    {
        var missing = UiComponentRequirements.GetMissing(item);
        var relevant = component is Core.Image or Core.Button
            ? missing
            : component is PureEngine.Core.UiElement
                ? missing.Where(name => name == "Transform").ToList()
                : [];
        if (relevant.Count > 0)
        {
            warning.Text = $"Requires: {string.Join(", ", relevant)}";
            warning.IsVisible = true;
            return;
        }
        if (component is Core.Image image && image.Sprite is { } sprite && IsAssetMissing(sprite.ImageId))
        {
            warning.Text = $"Missing image {sprite.ImageId:D}. ID is kept.";
            warning.IsVisible = true;
            return;
        }
        warning.Text = "";
        warning.IsVisible = false;
    }

    private void RefreshUiWarnings(SceneObject item)
    {
        foreach (var card in ComponentEditors.Children.OfType<Border>())
        {
            if (card.Tag is not object component) continue;
            if (!item.Components.Any(candidate => ReferenceEquals(candidate, component))) continue;
            foreach (var warning in card.GetVisualDescendants().OfType<TextBlock>()
                .Where(block => Equals(block.GetValue(AutomationProperties.NameProperty) as string,
                    $"{component.GetType().Name}.Requirements")))
                UpdateUiWarning(warning, item, component);
            foreach (var combo in card.GetVisualDescendants().OfType<ComboBox>()
                .Where(box => Equals(box.GetValue(AutomationProperties.NameProperty) as string,
                    $"{component.GetType().Name}.Sprite")))
            {
                // Asset list may have changed; rebuild selection without losing the kept ID.
                var member = ComponentSchema.GetInspectorMembers(component.GetType())
                    .FirstOrDefault(m => m.Name == "Sprite");
                if (member is not null) RefreshSpriteCombo(combo, component, member);
            }
        }
    }

    private void RefreshSpriteCombo(ComboBox combo, object component, MemberInfo member)
    {
        var current = (Sprite?)GetMemberValue(component, member);
        List<SpriteOption> options = [new SpriteOption(null, "None")];
        foreach (var entry in AssetImageEntries())
            options.Add(new SpriteOption(entry.Id, entry.RelativePath));
        SpriteOption selected;
        if (current is null) selected = options[0];
        else
        {
            selected = options.FirstOrDefault(option => option.Id == current.ImageId)
                ?? new SpriteOption(current.ImageId, $"Missing: {current.ImageId:D}");
            if (!options.Contains(selected)) options.Add(selected);
        }
        combo.ItemsSource = options;
        combo.SelectedItem = selected;
    }
}
