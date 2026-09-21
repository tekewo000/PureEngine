using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PureEngine.Core;

namespace PureEngine.Editor;

public partial class MainWindow : Window
{
    private readonly Scene _scene = new();
    private static readonly DataFormat<Type> ComponentFormat =
        DataFormat.CreateInProcessFormat<Type>("PureEngine.ComponentType");
    private PointerPressedEventArgs? _assetPress;
    private Point _assetPressPosition;
    private Type? _dragType;

    public MainWindow()
    {
        InitializeComponent();
        ClassAssets.ItemsSource = ComponentAssets.Types;
        SceneObjects.ItemsSource = _scene.Objects;
        SceneObjects.SelectionChanged += OnObjectSelected;
        ObjectName.TextChanged += OnObjectNameChanged;
        SceneSurface.AddHandler(PointerPressedEvent, OnScenePointerPressed, RoutingStrategies.Tunnel);
        ClassAssets.AddHandler(PointerPressedEvent, OnAssetPressed, RoutingStrategies.Tunnel);
        ClassAssets.AddHandler(PointerMovedEvent, OnAssetMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        ClassAssets.AddHandler(PointerReleasedEvent, (_, _) => _assetPress = null,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        foreach (var surface in new Control[] { SceneSurface, InspectorPane })
        {
            surface.AddHandler(DragDrop.DragOverEvent, OnComponentDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
            surface.AddHandler(DragDrop.DropEvent, OnComponentDrop, RoutingStrategies.Bubble, handledEventsToo: true);
        }
    }

    private void OnAssetPressed(object? sender, PointerPressedEventArgs e)
    {
        _assetPress = null;
        if (!e.GetCurrentPoint(ClassAssets).Properties.IsLeftButtonPressed) return;
        _dragType = (e.Source as Visual)?.GetSelfAndVisualAncestors()
            .OfType<ListBoxItem>().FirstOrDefault()?.DataContext as Type;
        if (_dragType is null) return;
        _assetPressPosition = e.GetPosition(ClassAssets);
        _assetPress = e;
    }

    private async void OnAssetMoved(object? sender, PointerEventArgs e)
    {
        if (_assetPress is null) return;
        if (!e.GetCurrentPoint(ClassAssets).Properties.IsLeftButtonPressed)
        {
            _assetPress = null;
            return;
        }
        var delta = e.GetPosition(ClassAssets) - _assetPressPosition;
        if (Math.Abs(delta.X) < 4 && Math.Abs(delta.Y) < 4) return;
        var press = _assetPress;
        var type = _dragType!;
        _assetPress = null;
        using var data = new DataTransfer();
        data.Add(DataTransferItem.Create(ComponentFormat, type));
        await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Copy);
    }

    private SceneObject? DropTarget(object? sender, DragEventArgs e) =>
        ReferenceEquals(sender, InspectorPane)
            ? SceneObjects.SelectedItem as SceneObject
            : (e.Source as Visual)?.GetSelfAndVisualAncestors()
                .OfType<ListBoxItem>().FirstOrDefault()?.DataContext as SceneObject;

    private void OnComponentDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = ComponentAssets.CanAttach(DropTarget(sender, e), e.DataTransfer.TryGetValue(ComponentFormat))
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnComponentDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        e.DragEffects = DragDropEffects.None;
        var target = DropTarget(sender, e);
        var type = e.DataTransfer.TryGetValue(ComponentFormat);
        if (!ComponentAssets.CanAttach(target, type)) return;
        SceneObjects.SelectedItem = target;
        try
        {
            if (!ComponentAssets.TryAttach(target, type)) return;
            RefreshComponents();
            e.DragEffects = DragDropEffects.Copy;
        }
        catch (Exception error)
        {
            AttachError.Text = $"{type!.Name} を追加できませんでした: {error.GetBaseException().Message}";
            AttachError.IsVisible = true;
        }
    }

    private void RefreshComponents()
    {
        var types = (SceneObjects.SelectedItem as SceneObject)?.Components.Select(c => c.GetType()).ToArray();
        ComponentNames.ItemsSource = types;
        AttachedClasses.IsVisible = types is { Length: > 0 };
        AttachError.IsVisible = false;
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
        RefreshComponents();
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
