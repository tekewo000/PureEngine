using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PureEngine.Core;

namespace PureEngine.Editor;

public partial class MainWindow
{
    internal static readonly DataFormat<string> SceneObjectIdFormat =
        DataFormat.CreateInProcessFormat<string>("PureEngine.SceneObjectId");

    private static Guid? GetDraggedId(DragEventArgs e) =>
        e.DataTransfer.TryGetValue(SceneObjectIdFormat) is { } text
        && Guid.TryParse(text, out var id) ? id : null;

    private ObservableCollection<HierarchyNode> _hierarchyRoots = [];
    private PointerPressedEventArgs? _hierarchyPress;
    private Point _hierarchyPressPosition;
    private Guid? _hierarchyDragId;
    private TreeViewItem? _hierarchyDropTarget;
    private HierarchyDropPosition _hierarchyDropPosition;
    private readonly DispatcherTimer _hierarchyExpandTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _hierarchyRefreshing;

    /// <summary>Starts driving the Stuffs pane tree. Called once from the constructor.</summary>
    private void InitHierarchy()
    {
        _hierarchyExpandTimer.Tick += OnHierarchyExpandTick;
        RefreshHierarchy();
        SceneObjects.AddHandler(PointerPressedEvent, OnHierarchyPointerPressed, RoutingStrategies.Tunnel);
        SceneObjects.AddHandler(PointerMovedEvent, OnHierarchyPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        SceneObjects.AddHandler(PointerReleasedEvent, OnHierarchyPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        SceneSurface.AddHandler(DragDrop.DragEnterEvent, OnHierarchyDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        SceneSurface.AddHandler(DragDrop.DragOverEvent, OnHierarchyDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        SceneSurface.AddHandler(DragDrop.DropEvent, OnHierarchyDrop, RoutingStrategies.Bubble, handledEventsToo: true);
        SceneSurface.AddHandler(DragDrop.DragLeaveEvent, OnHierarchyDragLeave, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>For tests: reflects objects added directly to the scene in the tree. Preserves the selection.</summary>
    internal void SyncHierarchyForTest() => RefreshHierarchy(GetSelectedSceneObject()?.Id);

    /// <summary>The selected SceneObject. Reads the TreeView selection as a HierarchyNode Ref.</summary>
    internal SceneObject? GetSelectedSceneObject() => (SceneObjects.SelectedItem as HierarchyNode)?.Ref;

    /// <summary>For tests: the selected node. Lets post-TreeView tests compare by instance instead of ID.</summary>
    internal HierarchyNode? SelectedHierarchyNodeForTest() => SceneObjects.SelectedItem as HierarchyNode;

    /// <summary>For tests: selects the node for a SceneObject. Goes through the tree display selection path.</summary>
    internal void SelectSceneObjectForTest(SceneObject? item) => SelectSceneObject(item, focus: false);

    /// <summary>Enumerates HierarchyNodes. Used to stash state before a rebuild and restore the selection.</summary>
    private IEnumerable<HierarchyNode> EnumerateHierarchyNodes(IEnumerable<HierarchyNode>? roots = null)
    {
        var stack = new Stack<HierarchyNode>(roots ?? _hierarchyRoots);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            foreach (var child in node.Children)
                stack.Push(child);
        }
    }

    /// <summary>Rebuilds the Stuffs tree from scene parent-child links. Preserves expansion and selection by ID.</summary>
    internal void RefreshHierarchy(Guid? keepSelectedId = null, Guid? expandId = null)
    {
        ClearHierarchyDropIndicator();
        var selectedNode = SceneObjects.SelectedItem as HierarchyNode;
        _hierarchyRefreshing = true;
        try
        {
            var expanded = new HashSet<Guid>();
            foreach (var node in EnumerateHierarchyNodes())
                if (node.IsExpanded) expanded.Add(node.Ref.Id);
            _hierarchyRoots = StuffsHierarchy.Build(_editScene.Current);
            foreach (var node in EnumerateHierarchyNodes())
                if (expanded.Contains(node.Ref.Id)) node.IsExpanded = true;
            SceneObjects.ItemsSource = _hierarchyRoots;
        }
        finally
        {
            _hierarchyRefreshing = false;
        }
        var selectedId = keepSelectedId ?? selectedNode?.Ref.Id;
        if (expandId is { } parentId)
            foreach (var node in EnumerateHierarchyNodes())
                if (node.Ref.Id == parentId)
                {
                    node.IsExpanded = true;
                    break;
                }
        if (selectedId is { } id)
            SelectSceneObject(FindObject(id), focus: false);
        else
            RefreshObjectInspector();
    }

    private SceneObject? FindObject(Guid id)
    {
        foreach (var item in _editScene.Current.Objects)
            if (item.Id == id) return item;
        return null;
    }

    /// <summary>Selects the Stuffs tree for the given SceneObject. Expands parent nodes to make it visible.</summary>
    internal void SelectSceneObject(SceneObject? item, bool focus)
    {
        if (item is null)
        {
            SceneObjects.SelectedItem = null;
            if (focus) SceneObjects.Focus();
            return;
        }
        ExpandAncestors(item);
        foreach (var node in EnumerateHierarchyNodes())
            if (ReferenceEquals(node.Ref, item))
            {
                SceneObjects.SelectedItem = node;
                SceneObjects.ScrollIntoView(node);
                break;
            }
        if (focus) SceneObjects.Focus();
    }

    private void ExpandAncestors(SceneObject item)
    {
        var ancestors = new Stack<SceneObject>();
        for (var current = item.Parent; current is not null; current = current.Parent)
            ancestors.Push(current);
        while (ancestors.Count > 0)
        {
            var ancestor = ancestors.Pop();
            foreach (var node in EnumerateHierarchyNodes())
                if (ReferenceEquals(node.Ref, ancestor))
                {
                    node.IsExpanded = true;
                    break;
                }
        }
    }

    private void OnHierarchyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _hierarchyPress = null;
        _hierarchyDragId = null;
        var point = e.GetCurrentPoint(SceneObjects);
        if (!point.Properties.IsLeftButtonPressed) return;
        if (IsPlaying) return;
        var source = e.Source as Visual;
        if (source?.GetSelfAndVisualAncestors().OfType<ToggleButton>().Any() == true) return;
        var node = FindHierarchyNode(source);
        if (node is null) return;
        // Suppress the TreeView press selection so a drag keeps the Inspector on its current target.
        // A press without a drag completes the click on release.
        e.Handled = true;
        _hierarchyPress = e;
        _hierarchyPressPosition = e.GetPosition(SceneObjects);
        _hierarchyDragId = node.Ref.Id;
    }

    private async void OnHierarchyPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_hierarchyPress is null || _hierarchyDragId is null) return;
        if (!e.GetCurrentPoint(SceneObjects).Properties.IsLeftButtonPressed)
        {
            _hierarchyPress = null;
            _hierarchyDragId = null;
            return;
        }
        var delta = e.GetPosition(SceneObjects) - _hierarchyPressPosition;
        if (Math.Abs(delta.X) < 4 && Math.Abs(delta.Y) < 4) return;
        var press = _hierarchyPress;
        var draggedId = _hierarchyDragId.Value;
        _hierarchyPress = null;
        _hierarchyDragId = null;
        using var data = new DataTransfer();
        data.Add(DataTransferItem.Create(SceneObjectIdFormat, draggedId.ToString("D")));
        // Hierarchy drops move objects; Inspector reference drops copy their reference.
        try { await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Move | DragDropEffects.Copy); }
        finally { ClearHierarchyDropIndicator(); }
    }

    private void OnHierarchyPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton is not MouseButton.Left) return;
        var dragId = _hierarchyDragId;
        _hierarchyPress = null;
        _hierarchyDragId = null;
        // A drag clears the press state when it starts, so a remaining ID means a click.
        if (dragId is not { } id) return;
        if (FindObject(id) is not { } item) return;
        SelectSceneObject(item, focus: true);
    }

    private static HierarchyNode? FindHierarchyNode(Visual? source) =>
        source?.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault()?.DataContext as HierarchyNode;

    private static (TreeViewItem? Container, HierarchyDropPosition Position) HierarchyDropTarget(DragEventArgs e, Visual? source)
    {
        var container = source?.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault();
        // The container includes expanded children; only its header is a drop row.
        var header = container?.GetTemplateDescendants().OfType<Border>().FirstOrDefault(item => item.Name == "PART_LayoutRoot");
        if (header is null || header.Bounds.Height <= 0) return (null, HierarchyDropPosition.AsChild);
        var point = e.GetPosition(header);
        if (!new Rect(header.Bounds.Size).Contains(point)) return (null, HierarchyDropPosition.AsChild);
        return (container, point.Y < header.Bounds.Height * 0.25 ? HierarchyDropPosition.Before
            : point.Y > header.Bounds.Height * 0.75 ? HierarchyDropPosition.After : HierarchyDropPosition.AsChild);
    }

    private void OnHierarchyDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(PrefabPathFormat) && !e.DataTransfer.Contains(SceneObjectIdFormat))
        {
            if (IsPlaying)
            {
                ClearHierarchyDropIndicator();
                e.DragEffects = DragDropEffects.None;
            }
            else
            {
                ShowPrefabDropIndicator(e);
                e.DragEffects = DragDropEffects.Copy;
            }
            e.Handled = true;
            return;
        }
        if (!e.DataTransfer.Contains(SceneObjectIdFormat)) return;
        if (IsPlaying || GetDraggedId(e) is not { } draggedId)
        {
            ClearHierarchyDropIndicator();
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        var (container, position) = HierarchyDropTarget(e, e.Source as Visual);
        var node = container?.DataContext as HierarchyNode;
        var targetId = node?.Ref.Id;
        if (!HierarchyDrop.CanDrop(_editScene.Current, draggedId, targetId))
        {
            ClearHierarchyDropIndicator();
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        if (!ReferenceEquals(_hierarchyDropTarget, container) || _hierarchyDropPosition != position)
        {
            ClearHierarchyDropIndicator();
            _hierarchyDropTarget = container;
            _hierarchyDropPosition = position;
            container?.Classes.Add(position switch
            {
                HierarchyDropPosition.Before => "drop-before",
                HierarchyDropPosition.After => "drop-after",
                _ => "drop-as-child",
            });
            if (position == HierarchyDropPosition.AsChild && node is { IsExpanded: false, Children.Count: > 0 })
                _hierarchyExpandTimer.Start();
        }
        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnHierarchyDragLeave(object? sender, DragEventArgs e)
    {
        // Moving between the text and chevron of the same row must not restart the hover delay.
        var hit = SceneSurface.InputHitTest(e.GetPosition(SceneSurface)) as Visual;
        var (container, position) = HierarchyDropTarget(e, hit);
        // Prefab hovers always target as-child regardless of the row geometry.
        if (e.DataTransfer.Contains(PrefabPathFormat) && !e.DataTransfer.Contains(SceneObjectIdFormat))
            position = HierarchyDropPosition.AsChild;
        if (ReferenceEquals(container, _hierarchyDropTarget) && position == _hierarchyDropPosition) return;
        ClearHierarchyDropIndicator();
    }

    /// <summary>Highlights the Stuffs row a dragged prefab would land under. Empty areas clear the highlight.</summary>
    private void ShowPrefabDropIndicator(DragEventArgs e)
    {
        var (container, _) = HierarchyDropTarget(e, e.Source as Visual);
        if (container?.DataContext is not HierarchyNode)
        {
            ClearHierarchyDropIndicator();
            return;
        }
        if (!ReferenceEquals(_hierarchyDropTarget, container))
        {
            ClearHierarchyDropIndicator();
            _hierarchyDropTarget = container;
            container?.Classes.Add("drop-as-child");
        }
        _hierarchyDropPosition = HierarchyDropPosition.AsChild;
    }

    private void OnHierarchyExpandTick(object? sender, EventArgs e)
    {
        _hierarchyExpandTimer.Stop();
        if (!IsPlaying && _hierarchyDropTarget?.DataContext is HierarchyNode node)
            node.IsExpanded = true;
    }

    private void ClearHierarchyDropIndicator()
    {
        _hierarchyExpandTimer.Stop();
        if (_hierarchyDropTarget is not null)
        {
            _hierarchyDropTarget.Classes.Remove("drop-before");
            _hierarchyDropTarget.Classes.Remove("drop-after");
            _hierarchyDropTarget.Classes.Remove("drop-as-child");
            _hierarchyDropTarget = null;
        }
    }

    private void OnHierarchyDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(PrefabPathFormat) && !e.DataTransfer.Contains(SceneObjectIdFormat))
        {
            e.Handled = true;
            e.DragEffects = DragDropEffects.None;
            var (prefabContainer, _) = HierarchyDropTarget(e, e.Source as Visual);
            ClearHierarchyDropIndicator();
            if (RejectWhenPlaying("Place")) return;
            if (e.DataTransfer.TryGetValue(PrefabPathFormat) is not { } path) return;
            var prefabNode = prefabContainer?.DataContext as HierarchyNode;
            try
            {
                PlacePrefabAt(path, prefabNode?.Ref);
            }
            catch
            {
                // PlacePrefabAt already reported the reason in the file status.
                return;
            }
            e.DragEffects = DragDropEffects.Copy;
            return;
        }
        if (!e.DataTransfer.Contains(SceneObjectIdFormat)) return;
        e.Handled = true;
        e.DragEffects = DragDropEffects.None;
        var (container, position) = HierarchyDropTarget(e, e.Source as Visual);
        ClearHierarchyDropIndicator();
        if (RejectWhenPlaying("Reparent")) return;
        if (GetDraggedId(e) is not { } draggedId) return;
        var node = container?.DataContext as HierarchyNode;
        var targetId = node?.Ref.Id;
        try
        {
            HierarchyDrop.Execute(_editScene.Current, draggedId, targetId, position);
        }
        catch (Exception error)
        {
            SetFileStatus($"Cannot reparent: {error.GetBaseException().Message}", true);
            return;
        }
        MarkSceneChanged();
        e.DragEffects = DragDropEffects.Move;
        var dragged = FindObject(draggedId);
        if (targetId is { } parentId && position == HierarchyDropPosition.AsChild)
            RefreshHierarchy(draggedId, expandId: parentId);
        else if (dragged?.Parent?.Id is { } ancestorId)
            RefreshHierarchy(draggedId, expandId: ancestorId);
        else
            RefreshHierarchy(draggedId);
    }

    internal bool IsRefreshingHierarchyForTest() => _hierarchyRefreshing;
}
