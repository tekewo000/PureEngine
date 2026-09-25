using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using PureEngine.Core;
using PureEngine.Rendering;

namespace PureEngine.Editor;

public partial class MainWindow
{
    private Vector2 _scenePan;
    private float _sceneZoom = 1f;
    private bool _scenePanning;
    private Vector2 _panStartPan;
    private Vector2 _panStartView;
    private SceneViewMath.GizmoKind _sceneMoveKind = SceneViewMath.GizmoKind.None;
    private Scene? _dragScene;
    private SceneObject? _dragTarget;
    private Transform? _dragTransform;
    private SceneObject? _dragParent;
    private Vector3 _dragStartLocal;
    private Matrix4x4 _dragParentWorld = Matrix4x4.Identity;
    private Vector2 _dragStartScene;
    private bool _sceneViewSyncing;
    private IPointer? _scenePointer;
    private UiElement? _dragElement;
    private Vector2 _dragParentSize, _dragViewportSize;
    private (Vector2 Min, Vector2 Max, Vector2 Pivot, Vector2 Size, Quaternion Rotation, Vector3 Scale) _dragGeometry;
    private readonly HashSet<Guid> _sceneDrawFailures = [];

    internal Vector2 ScenePan => _scenePan;

    internal float SceneZoom => _sceneZoom;

    internal bool IsSceneDragging => _scenePanning || _sceneMoveKind is not SceneViewMath.GizmoKind.None;

    private void InitSceneView()
    {
        SceneViewport.Focusable = true;
        // Composition surfaces do not provide an Avalonia hit-test background.
        SceneViewport.Background = Avalonia.Media.Brushes.Transparent;
        SceneViewport.AddHandler(PointerPressedEvent, OnSceneViewPointerPressed, Avalonia.Interactivity.RoutingStrategies.Bubble);
        SceneViewport.AddHandler(PointerMovedEvent, OnSceneViewPointerMoved, Avalonia.Interactivity.RoutingStrategies.Bubble);
        SceneViewport.AddHandler(PointerReleasedEvent, OnSceneViewPointerReleased, Avalonia.Interactivity.RoutingStrategies.Bubble);
        SceneViewport.AddHandler(PointerWheelChangedEvent, OnSceneViewWheel, Avalonia.Interactivity.RoutingStrategies.Bubble);
        SceneViewport.AddHandler(KeyDownEvent, OnSceneViewKeyDown, Avalonia.Interactivity.RoutingStrategies.Bubble);
        SceneViewport.PointerCaptureLost += OnSceneViewCaptureLost;
        AddHandler(KeyDownEvent, OnSceneViewGlobalKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Deactivated += (_, _) => CancelSceneViewDrag();
        SceneViewport.LostFocus += (_, _) => CancelSceneViewDrag();
        SceneViewport.DetachedFromVisualTree += (_, _) => CancelSceneViewDrag();
    }

    private Vector2 SceneViewportSize()
    {
        var size = SceneViewport.Bounds.Size;
        return new Vector2((float)size.Width, (float)size.Height);
    }

    private bool IsDrawableSceneViewport(out Vector2 viewportSize)
    {
        viewportSize = SceneViewportSize();
        return SceneViewMath.IsValidViewport(viewportSize)
            && SceneViewMath.IsValidView(_scenePan, _sceneZoom)
            && ViewportTabs.SelectedIndex is SceneViewportIndex or PrefabViewportIndex
            && SceneViewport.IsEffectivelyVisible;
    }

    private IReadOnlyList<SceneViewMath.LayoutEntry> SceneLayouts(Vector2 viewportSize) =>
        SceneViewMath.EnumerateLayouts(_editScene.Current, viewportSize);

    private bool IsDrawableImage(SceneObject item)
    {
        if (item.GetComponent<Core.Image>() is not { Sprite: { } sprite })
            return false;
        return _previewImages.ContainsKey(sprite.ImageId) && !_sceneDrawFailures.Contains(item.Id);
    }

    private bool IsDrawableUi(SceneObject item) => IsDrawableImage(item) || IsDrawableText(item);

    private bool IsDrawableText(SceneObject item) =>
        item.GetComponent<Core.Text>() is { Content.Length: > 0 } && !_sceneDrawFailures.Contains(item.Id);

    private static SceneViewMath.LayoutEntry? FindLayout(IReadOnlyList<SceneViewMath.LayoutEntry> entries, SceneObject target)
    {
        foreach (var entry in entries)
        {
            if (ReferenceEquals(entry.Object, target))
                return entry;
        }
        return null;
    }

    private bool TrySceneFrame(
        SceneObject target, Vector2 viewportSize,
        out Vector2[] cornersView, out Vector2 pivotView,
        out Vector2 xAxis, out Vector2 yAxis, out bool gizmoValid)
    {
        cornersView = [];
        pivotView = Vector2.Zero;
        xAxis = Vector2.UnitX;
        yAxis = Vector2.UnitY;
        gizmoValid = false;
        var entry = FindLayout(SceneLayouts(viewportSize), target);
        if (entry is not null
            && SceneViewMath.TryGetSelectionFrame(entry, _scenePan, _sceneZoom, out cornersView, out pivotView))
        {
            gizmoValid = SceneViewMath.TryGetParentAxes(entry.ParentWorld, out xAxis, out yAxis);
            return true;
        }
        // Transform-only group parent: no rectangle, but shows Pivot and Gizmo as layout origins.
        // Keeps the legacy behavior for collapsed layouts with UiElement: shows neither frame nor gizmo.
        if (target.GetComponent<UiElement>() is not null)
            return false;
        if (!SceneViewMath.TryGetTransformFrame(_editScene.Current, target, viewportSize, out _, out var parentWorld, out var world))
            return false;
        if (!SceneViewMath.TryGetParentAxes(parentWorld, out xAxis, out yAxis))
            return false;
        var scenePivot = new Vector2(world.M41, world.M42);
        if (!float.IsFinite(scenePivot.X) || !float.IsFinite(scenePivot.Y))
            return false;
        pivotView = SceneViewMath.SceneToView(scenePivot, _scenePan, _sceneZoom);
        if (!float.IsFinite(pivotView.X) || !float.IsFinite(pivotView.Y))
            return false;
        gizmoValid = true;
        return true;
    }

    private void OnSceneViewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsSceneDragging || !IsDrawableSceneViewport(out var viewportSize)) return;
        var point = e.GetPosition(SceneViewport);
        var viewPoint = new Vector2((float)point.X, (float)point.Y);
        if (!float.IsFinite(viewPoint.X) || !float.IsFinite(viewPoint.Y)) return;
        var properties = e.GetCurrentPoint(SceneViewport).Properties;
        if (properties.IsMiddleButtonPressed)
        {
            SceneViewport.Focus();
            _scenePanning = true;
            _panStartPan = _scenePan;
            _panStartView = viewPoint;
            _scenePointer = e.Pointer;
            e.Pointer.Capture(SceneViewport);
            e.Handled = true;
            return;
        }
        if (!properties.IsLeftButtonPressed) return;
        SceneViewport.Focus();
        if (IsPlaying) { RejectWhenPlaying("Select"); return; }
        var entries = SceneLayouts(viewportSize);
        if (GetSelectedSceneObject() is SceneObject selected
            && TrySceneFrame(selected, viewportSize, out _, out var pivot, out var xAxis, out var yAxis, out var gizmoValid)
            && gizmoValid
            && BeginSceneMove(selected, SceneViewMath.HitGizmo(pivot, xAxis, yAxis, viewPoint), viewPoint))
        {
            _scenePointer = e.Pointer;
            e.Pointer.Capture(SceneViewport);
            e.Handled = true;
            return;
        }
        SelectSceneObject(SceneViewMath.HitTest(entries, viewportSize, _scenePan, _sceneZoom, viewPoint, IsDrawableUi), focus: false);
        e.Handled = true;
    }

    private bool BeginSceneMove(SceneObject target, SceneViewMath.GizmoKind kind, Vector2 viewPoint)
    {
        if (IsPlaying || IsSceneDragging || kind is not (SceneViewMath.GizmoKind.X or SceneViewMath.GizmoKind.Y or SceneViewMath.GizmoKind.XY)
            || !IsDrawableSceneViewport(out var viewportSize)
            || !float.IsFinite(viewPoint.X) || !float.IsFinite(viewPoint.Y)
            || !_editScene.Current.Objects.Contains(target)) return false;
        if (target.GetComponent<Transform>() is not { } transform) return false;
        var element = target.GetComponent<UiElement>();
        if (element is not null)
        {
            var entry = FindLayout(SceneLayouts(viewportSize), target);
            if (entry is null || !SceneViewMath.TryGetSelectionFrame(entry, _scenePan, _sceneZoom, out _, out _)
                || !SceneViewMath.TryGetParentAxes(entry.ParentWorld, out _, out _)) return false;
            _dragParentWorld = entry.ParentWorld;
            _dragParentSize = entry.ParentSize;
            _dragGeometry = (element.AnchorMin, element.AnchorMax, element.Pivot, element.SizeDelta, transform.LocalRotation, transform.LocalScale);
        }
        else
        {
            // Transform-only group parents can also move as origins. Relaxes the rectangle requirement and watches only the parent chain plus local rotation and scale.
            if (!SceneViewMath.TryGetTransformFrame(_editScene.Current, target, viewportSize, out var parentSize, out var parentWorld, out _))
                return false;
            if (!SceneViewMath.TryGetParentAxes(parentWorld, out _, out _)) return false;
            _dragParentWorld = parentWorld;
            _dragParentSize = parentSize;
            _dragGeometry = (Vector2.Zero, Vector2.Zero, Vector2.Zero, Vector2.Zero, transform.LocalRotation, transform.LocalScale);
        }
        _sceneMoveKind = kind;
        _dragScene = _editScene.Current;
        _dragTarget = target;
        _dragTransform = transform;
        _dragElement = element;
        _dragParent = target.Parent;
        _dragStartLocal = transform.LocalPosition;
        _dragViewportSize = viewportSize;
        _dragStartScene = SceneViewMath.ViewToScene(viewPoint, _scenePan, _sceneZoom);
        return true;
    }

    private bool ValidateSceneMove()
    {
        if (_sceneMoveKind is SceneViewMath.GizmoKind.None) return false;
        if (_dragScene is null || _dragTarget is null || _dragTransform is null
            || !ReferenceEquals(_dragScene, _editScene.Current) || !_editScene.Current.Objects.Contains(_dragTarget)
            || !ReferenceEquals(_dragTarget.GetComponent<Transform>(), _dragTransform))
        {
            AbortSceneDrag();
            return false;
        }
        var element = _dragTarget.GetComponent<UiElement>();
        var viewportSize = SceneViewportSize();
        if (IsPlaying || !IsDrawableSceneViewport(out _) || viewportSize != _dragViewportSize
            || !ReferenceEquals(_dragTarget.Parent, _dragParent) || !ReferenceEquals(element, _dragElement))
        {
            CancelSceneViewDrag();
            return false;
        }
        if (_dragElement is not null)
        {
            var entry = FindLayout(SceneLayouts(viewportSize), _dragTarget);
            if (element is null
                || (element.AnchorMin, element.AnchorMax, element.Pivot, element.SizeDelta, _dragTransform.LocalRotation, _dragTransform.LocalScale) != _dragGeometry
                || entry is null || entry.ParentSize != _dragParentSize || entry.ParentWorld != _dragParentWorld)
            {
                CancelSceneViewDrag();
                return false;
            }
        }
        else
        {
            if ((_dragTransform.LocalRotation, _dragTransform.LocalScale) != (_dragGeometry.Rotation, _dragGeometry.Scale))
            {
                CancelSceneViewDrag();
                return false;
            }
            if (!SceneViewMath.TryGetTransformFrame(_editScene.Current, _dragTarget, viewportSize, out var parentSize, out var parentWorld, out _)
                || parentSize != _dragParentSize || parentWorld != _dragParentWorld)
            {
                CancelSceneViewDrag();
                return false;
            }
        }
        return true;
    }

    private void OnSceneViewPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!IsSceneDragging || !ReferenceEquals(e.Pointer, _scenePointer))
            return;
        var point = e.GetPosition(SceneViewport);
        var viewPoint = new Vector2((float)point.X, (float)point.Y);
        if (!float.IsFinite(viewPoint.X) || !float.IsFinite(viewPoint.Y))
            return;
        if (_scenePanning)
        {
            var next = _panStartPan + (viewPoint - _panStartView);
            if (!float.IsFinite(next.X) || !float.IsFinite(next.Y))
                return;
            _scenePan = next;
            e.Handled = true;
            return;
        }
        UpdateSceneMove(viewPoint);
        e.Handled = true;
    }

    private bool UpdateSceneMove(Vector2 viewPoint)
    {
        if (!ValidateSceneMove()) return false;
        var delta = SceneViewMath.ViewToScene(viewPoint, _scenePan, _sceneZoom) - _dragStartScene;
        if (!SceneViewMath.TrySceneDeltaToLocal(delta, _dragParentWorld, out var localDelta)
            || !SceneViewMath.TryApplyMove(_dragStartLocal, localDelta, _sceneMoveKind, out var next))
        {
            CancelSceneViewDrag();
            return false;
        }
        _dragTransform!.LocalPosition = next;
        SyncInspectorToDrag();
        return true;
    }

    private bool ConfirmSceneMove(Vector2 viewPoint)
    {
        if (!UpdateSceneMove(viewPoint)) return false;
        var target = _dragTarget!;
        var transform = _dragTransform!;
        var changed = transform.LocalPosition != _dragStartLocal;
        AbortSceneDrag();
        if (changed) MarkSceneChanged();
        SyncInspectorToDragTarget(target, transform);
        return changed;
    }

    private void OnSceneViewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!IsSceneDragging || !ReferenceEquals(e.Pointer, _scenePointer)) return;
        var properties = e.GetCurrentPoint(SceneViewport).Properties;
        if (_scenePanning ? properties.IsMiddleButtonPressed : properties.IsLeftButtonPressed) return;
        var point = e.GetPosition(SceneViewport);
        if (_scenePanning) AbortSceneDrag();
        else ConfirmSceneMove(new Vector2((float)point.X, (float)point.Y));
        e.Handled = true;
    }

    private void OnSceneViewWheel(object? sender, PointerWheelEventArgs e)
    {
        if (IsSceneDragging)
            return;
        var viewportSize = SceneViewportSize();
        if (!SceneViewMath.IsValidViewport(viewportSize) || !SceneViewMath.IsValidView(_scenePan, _sceneZoom))
            return;
        var point = e.GetPosition(SceneViewport);
        var viewPoint = new Vector2((float)point.X, (float)point.Y);
        var factor = MathF.Pow(1.15f, (float)e.Delta.Y);
        if (!SceneViewMath.TryZoomAt(viewPoint, viewportSize, _scenePan, _sceneZoom, factor, out var pan, out var zoom))
            return;
        _scenePan = pan;
        _sceneZoom = zoom;
        UpdateStatusBarSegments();
        e.Handled = true;
    }

    private void OnSceneViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (IsSceneDragging || !SceneViewport.IsKeyboardFocusWithin || e.Key != Key.F || e.KeyModifiers != KeyModifiers.None)
            return;
        if (ViewportTabs.SelectedIndex is not (SceneViewportIndex or PrefabViewportIndex) || !SceneViewport.IsEffectivelyVisible)
            return;
        if (GetSelectedSceneObject() is not SceneObject target)
            return;
        var viewportSize = SceneViewportSize();
        if (!SceneViewMath.IsValidViewport(viewportSize) || !SceneViewMath.IsValidView(_scenePan, _sceneZoom))
            return;
        var entry = FindLayout(SceneLayouts(viewportSize), target);
        if (entry is null || !SceneViewMath.TryGetSceneCorners(entry, out var corners))
            return;
        if (!SceneViewMath.TryComputeFit(viewportSize, corners, out var pan, out var zoom))
            return;
        _scenePan = pan;
        _sceneZoom = zoom;
        UpdateStatusBarSegments();
        e.Handled = true;
    }

    private void OnSceneViewGlobalKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None)
            return;
        if (!IsSceneDragging) return;
        CancelSceneViewDrag();
        e.Handled = true;
    }

    private void OnSceneViewCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (ReferenceEquals(e.Pointer, _scenePointer)) CancelSceneViewDrag();
    }

    private void AbortSceneDrag()
    {
        var pointer = _scenePointer;
        _scenePointer = null;
        _scenePanning = false;
        _sceneMoveKind = SceneViewMath.GizmoKind.None;
        _dragScene = null;
        _dragTarget = null;
        _dragTransform = null;
        _dragParent = null;
        _dragElement = null;
        // Clear state before CaptureLost is raised; never release capture stolen by another control.
        if (ReferenceEquals(pointer?.Captured, SceneViewport)) pointer!.Capture(null);
    }

    /// <summary>Cancel the current operation and release capture. Never write back into a deleted or replaced scene.</summary>
    internal void CancelSceneViewDrag()
    {
        var target = _dragTarget;
        var transform = _dragTransform;
        var start = _dragStartLocal;
        var scene = _dragScene;
        AbortSceneDrag();
        if (target is null || transform is null || !ReferenceEquals(scene, _editScene.Current)
            || !_editScene.Current.Objects.Contains(target) || !ReferenceEquals(target.GetComponent<Transform>(), transform)) return;
        transform.LocalPosition = start;
        SyncInspectorToDragTarget(target, transform);
    }

    private void SyncInspectorToDrag()
    {
        if (_dragTarget is null || _dragTransform is null)
            return;
        SyncInspectorToDragTarget(_dragTarget, _dragTransform);
    }

    private void SyncInspectorToDragTarget(SceneObject target, Transform transform)
    {
        if (!ReferenceEquals(GetSelectedSceneObject(), target))
            return;
        var card = ComponentEditors.Children.OfType<Border>()
            .FirstOrDefault(candidate => ReferenceEquals(candidate.Tag, transform));
        if (card is null)
            return;
        var boxes = card.GetVisualDescendants().OfType<TextBox>().ToList();
        if (boxes.Count < 3)
            return;
        _sceneViewSyncing = true;
        try
        {
            boxes[0].Text = transform.LocalPosition.X.ToString(System.Globalization.CultureInfo.InvariantCulture);
            boxes[1].Text = transform.LocalPosition.Y.ToString(System.Globalization.CultureInfo.InvariantCulture);
            boxes[2].Text = transform.LocalPosition.Z.ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var box in boxes.Take(3))
                MarkInvalid(box, null, "Enter a number — Press Esc to revert");
        }
        finally
        {
            _sceneViewSyncing = false;
        }
    }

    internal bool IsSyncingInspectorForSceneView() => _sceneViewSyncing;


    /// <summary>Pan for headless verification. Does not change production data or the unsaved state.</summary>
    internal bool TryPanForTest(Vector2 delta)
    {
        if (!float.IsFinite(delta.X) || !float.IsFinite(delta.Y))
            return false;
        if (!SceneViewMath.IsValidView(_scenePan, _sceneZoom))
            return false;
        var next = _scenePan + delta;
        if (!float.IsFinite(next.X) || !float.IsFinite(next.Y))
            return false;
        _scenePan = next;
        return true;
    }

    /// <summary>Zoom for headless verification. Centers on the cursor position without marking unsaved changes.</summary>
    internal bool TryZoomForTest(Vector2 viewPoint, float factor)
    {
        if (!SceneViewMath.TryZoomAt(viewPoint, SceneViewportSize(), _scenePan, _sceneZoom, factor, out var pan, out var zoom))
            return false;
        _scenePan = pan;
        _sceneZoom = zoom;
        UpdateStatusBarSegments();
        return true;
    }

    /// <summary>Move start for headless verification. Takes the already-hit gizmo kind and keeps the start position and parent layout.</summary>
    internal bool TryBeginMoveForTest(SceneObject target, SceneViewMath.GizmoKind kind, Vector2 viewPoint) =>
        BeginSceneMove(target, kind, viewPoint);

    internal bool TryUpdateMoveForTest(Vector2 viewPoint) => UpdateSceneMove(viewPoint);

    internal bool TryConfirmMoveForTest(Vector2 viewPoint) => ConfirmSceneMove(viewPoint);

    /// <summary>F framing for headless verification. Centers the selection with padding without marking unsaved changes.</summary>
    internal bool TryFitForTest()
    {
        if (GetSelectedSceneObject() is not SceneObject target)
            return false;
        var entry = FindLayout(SceneLayouts(SceneViewportSize()), target);
        if (entry is null || !SceneViewMath.TryGetSceneCorners(entry, out var corners))
            return false;
        if (!SceneViewMath.TryComputeFit(SceneViewportSize(), corners, out var pan, out var zoom))
            return false;
        _scenePan = pan;
        _sceneZoom = zoom;
        UpdateStatusBarSegments();
        return true;
    }
}
