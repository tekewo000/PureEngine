using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PureEngine.Core;

namespace PureEngine.Editor;

public partial class MainWindow : Window
{
    private readonly Scene _scene = new();

    public MainWindow()
    {
        InitializeComponent();
        SceneObjects.ItemsSource = _scene.Objects;
        SceneObjects.SelectionChanged += OnObjectSelected;
        ObjectName.TextChanged += OnObjectNameChanged;
        SceneSurface.AddHandler(PointerPressedEvent, OnScenePointerPressed, RoutingStrategies.Tunnel);
    }

    private void OnScenePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(SceneSurface).Properties.IsRightButtonPressed) return;
        var row = (e.Source as Avalonia.Visual)?.GetSelfAndVisualAncestors()
            .OfType<ListBoxItem>().FirstOrDefault();
        SceneObjects.SelectedItem = row?.DataContext as SceneObject;
        SceneObjects.Focus();
    }

    private void OnAddObject(object? sender, RoutedEventArgs e)
    {
        var item = _scene.AddEmpty();
        SceneObjects.SelectedItem = item;
        SceneObjects.ScrollIntoView(item);
        SceneObjects.Focus();
    }

    private void OnObjectSelected(object? sender, SelectionChangedEventArgs e)
    {
        var item = SceneObjects.SelectedItem as SceneObject;
        DeleteObjectMenuItem.IsEnabled = item is not null;
        ObjectInspector.IsVisible = item is not null;
        ObjectName.Text = item?.Name ?? "";
        ObjectId.Text = item?.Id.ToString() ?? "";
        NameError.IsVisible = false;
    }

    private void OnObjectNameChanged(object? sender, TextChangedEventArgs e)
    {
        if (SceneObjects.SelectedItem is not SceneObject item) return;
        NameError.IsVisible = string.IsNullOrWhiteSpace(ObjectName.Text);
        if (!NameError.IsVisible) item.Rename(ObjectName.Text!);
    }

    private void OnDeleteObject(object? sender, RoutedEventArgs e) => DeleteSelectedObject();

    private void OnSceneKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        DeleteSelectedObject();
        e.Handled = true;
    }

    private void DeleteSelectedObject()
    {
        if (SceneObjects.SelectedItem is not SceneObject item) return;
        var index = SceneObjects.SelectedIndex;
        _scene.Remove(item);
        SceneObjects.SelectedIndex = Math.Min(index, _scene.Objects.Count - 1);
        SceneObjects.Focus();
    }

    private void OnPanePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is TabControl pane && !pane.IsKeyboardFocusWithin)
            pane.Focus();
    }
}
