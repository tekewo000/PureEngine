using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using PureEngine.Core;

namespace PureEngine.Editor;

public partial class MainWindow
{
    /// <summary>選択中オブジェクトへの追加入口。登録型を検索し、既存のアタッチ処理・factoryを使う。</summary>
    internal void OpenAddComponentDialog()
    {
        if (RejectWhenPlaying("Add")) return;
        if (SceneObjects.SelectedItem is not SceneObject target)
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
        };
        var search = new TextBox { PlaceholderText = "Search components…" };
        search.SetValue(AutomationProperties.NameProperty, "AddComponentSearch");
        var list = new ListBox { MaxHeight = 320, SelectionMode = SelectionMode.Single };
        list.SetValue(AutomationProperties.NameProperty, "AddComponentList");
        var status = new TextBlock { Classes = { "hint" }, TextWrapping = TextWrapping.Wrap };
        var add = new Button { Content = "Add", IsDefault = true };
        add.SetValue(AutomationProperties.NameProperty, "AddComponentAdd");
        var close = new Button { Content = "Close", IsCancel = true };
        close.SetValue(AutomationProperties.NameProperty, "AddComponentClose");
        void updateAddState() => add.IsEnabled = list.SelectedItem is ComponentAddRow row && !row.Attached && !IsPlaying;
        void refresh()
        {
            var selectedType = (list.SelectedItem as ComponentAddRow)?.Type;
            var candidates = ComponentAssets.SearchCandidates(_components.Registry, search.Text);
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
                if (!_components.TryAttach(target, row.Type, EditSession.Factory)) return;
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
                search,
                list,
                status,
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

    private void OnAddComponent(object? sender, RoutedEventArgs e) => OpenAddComponentDialog();
}
