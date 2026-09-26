using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using PureEngine.Core;

namespace PureEngine.Editor;

public partial class MainWindow
{
    /// <summary>Entry point for adding to the selected object. Searches registered types and uses the existing attach logic and factory.</summary>
    internal void OpenAddComponentDialog()
    {
        if (RejectWhenPlaying("Add")) return;
        if (GetSelectedSceneObject() is not SceneObject target)
        {
            AttachError.Text = "Select an object first.";
            AttachError.IsVisible = true;
            return;
        }
        var dialog = new Window
        {
            Title = "Add Component",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = FindEditorBrush("EditorBackgroundBrush"),
            Foreground = FindEditorBrush("EditorTextBrush"),
            FontFamily = "Segoe UI",
            FontSize = 12,
        };
        var caption = new TextBlock
        {
            Text = "ADD COMPONENT",
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = FindEditorBrush("EditorTextTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var heading = new TextBlock
        {
            Text = target.Name,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = FindEditorBrush("EditorTextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(heading, target.Name);
        var header = new StackPanel { Spacing = 2 };
        header.Children.Add(caption);
        header.Children.Add(heading);
        var searchIcon = new PathIcon
        {
            Data = FindEditorIcon("Icon.Search"),
            Width = 14,
            Height = 14,
            Foreground = FindEditorBrush("EditorTextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var search = new TextBox
        {
            PlaceholderText = "Search components…",
            MinHeight = 26,
            Padding = new Thickness(6, 3),
            FontSize = 12,
            CornerRadius = new CornerRadius(6),
            BorderBrush = FindEditorBrush("EditorBorderStrongBrush"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
        };
        search.SetValue(AutomationProperties.NameProperty, "AddComponentSearch");
        var searchRow = new Grid { ColumnDefinitions = [with("Auto,*")], ColumnSpacing = 8 };
        searchRow.Children.Add(searchIcon);
        Grid.SetColumn(search, 1);
        searchRow.Children.Add(search);
        // The framework may rebuild cleared containers with a null item when the candidate list is replaced; null renders empty.
        var list = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            MinHeight = 144,
            MaxHeight = 320,
            SelectionMode = SelectionMode.Single,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<ComponentAddRow>((row, _) => BuildComponentAddRow(row)),
        };
        list.SetValue(AutomationProperties.NameProperty, "AddComponentList");
        var listCard = new Border
        {
            Background = FindEditorBrush("EditorFloatBrush"),
            BorderBrush = FindEditorBrush("EditorBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6),
            Child = list,
        };
        var status = new TextBlock
        {
            FontSize = 11,
            Foreground = FindEditorBrush("EditorTextTertiaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var add = new Avalonia.Controls.Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new PathIcon
                    {
                        Data = FindEditorIcon("Icon.AddSquare"),
                        Width = 14,
                        Height = 14,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    new TextBlock { Text = "Add", VerticalAlignment = VerticalAlignment.Center },
                },
            },
            IsDefault = true,
        };
        add.Classes.Add("accent");
        add.SetValue(AutomationProperties.NameProperty, "AddComponentAdd");
        var close = new Avalonia.Controls.Button { Content = "Close", IsCancel = true };
        close.SetValue(AutomationProperties.NameProperty, "AddComponentClose");
        void updateAddState() => add.IsEnabled = list.SelectedItem is ComponentAddRow row && !row.Attached && !IsPlaying;
        void refresh()
        {
            var selectedType = (list.SelectedItem as ComponentAddRow)?.Type;
            var candidates = ComponentAssets.SearchCandidates(Components.Registry, search.Text);
            var rows = new List<ComponentAddRow>();
            foreach (var (type, typeId) in candidates)
            {
                var attached = target.Components.Any(component => component.GetType() == type);
                rows.Add(new ComponentAddRow(type, typeId, attached));
            }
            list.ItemsSource = rows;
            list.SelectedItem = rows.FirstOrDefault(row => row.Type == selectedType) ?? rows.FirstOrDefault();
            status.Text = rows.Count == 0 ? "No matching components." : $"{rows.Count} match(es). Attached types are disabled.";
            updateAddState();
        }
        search.TextChanged += (_, _) => refresh();
        list.SelectionChanged += (_, _) => updateAddState();
        list.DoubleTapped += (_, _) => add.Focus();
        add.Click += (_, _) =>
        {
            if (IsPlaying) return;
            if (list.SelectedItem is not ComponentAddRow row || row.Attached) return;
            try
            {
                if (!Components.TryAttach(target, row.Type, EditSession.Factory)) return;
            }
            catch (Exception error)
            {
                AttachError.Text = $"Could not add {row.Type.Name}: {error.GetBaseException().Message}";
                AttachError.IsVisible = true;
                return;
            }
            MarkSceneChanged();
            RefreshComponents();
            refresh();
        };
        close.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                header,
                searchRow,
                listCard,
                status,
                new Separator
                {
                    Background = FindEditorBrush("EditorBorderBrush"),
                    Margin = new Thickness(0, 2),
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { add, close },
                },
            },
        };
        dialog.Opened += (_, _) => { search.Focus(); refresh(); };
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) dialog.Close();
        };
        refresh();
        _ = dialog.ShowDialog(this);
    }

    internal sealed record ComponentAddRow(Type Type, string TypeId, bool Attached)
    {
        public string Display => Attached ? $"{Type.Name} ({TypeId}) — Added" : $"{Type.Name} ({TypeId})";
        public override string ToString() => Display;
    }

    /// <summary>Editor palette lookup shared by code-built dialog visuals. Falls back to the theme default when a resource is missing.</summary>
    private static IBrush? FindEditorBrush(string key) => Application.Current?.FindResource(key) as IBrush;

    private static StreamGeometry? FindEditorIcon(string key) => Application.Current?.FindResource(key) as StreamGeometry;

    /// <summary>Two-line candidate row: semibold name, muted type id, and an Added badge for attached types. Attached rows render dimmed. A null row renders empty for container clearing.</summary>
    private static Grid BuildComponentAddRow(ComponentAddRow? row)
    {
        var content = new Grid { ColumnDefinitions = [with("*,Auto")], ColumnSpacing = 8 };
        if (row is null) return content;
        var tertiary = FindEditorBrush("EditorTextTertiaryBrush");
        var name = new TextBlock
        {
            Text = row.Type.Name,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = row.Attached ? tertiary : FindEditorBrush("EditorTextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(name, row.Type.FullName);
        var typeId = new TextBlock
        {
            Text = row.TypeId,
            FontSize = 11,
            Foreground = row.Attached ? tertiary : FindEditorBrush("EditorTextSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var labels = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(name);
        labels.Children.Add(typeId);
        content.Children.Add(labels);
        if (row.Attached)
        {
            var badge = new Border
            {
                Background = FindEditorBrush("EditorFieldBrush"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "Added",
                    FontSize = 10,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = tertiary,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            Grid.SetColumn(badge, 1);
            content.Children.Add(badge);
        }
        content.SetValue(AutomationProperties.NameProperty, row.Display);
        return content;
    }

    private void OnAddComponent(object? sender, RoutedEventArgs e) => OpenAddComponentDialog();
}
