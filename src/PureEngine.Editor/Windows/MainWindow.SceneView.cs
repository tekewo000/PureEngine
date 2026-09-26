using System.Numerics;
using Avalonia;
using Avalonia.Automation;
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
    private SceneViewMath.ResizeHandle _sceneResizeHandle = SceneViewMath.ResizeHandle.None;
    private bool _sceneRotating;
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
    private Vector2 _dragStartSizeDelta;
    private Vector2 _dragAnchorSpan;
    private Vector2 _dragPivot;
    private Quaternion _dragStartRotation;
    private Vector2 _dragPivotScene;
    private TextBox? _dragPosXBox, _dragPosYBox, _dragPosZBox;
    private TextBox? _dragRotXBox, _dragRotYBox, _dragRotZBox, _dragRotWBox;
    private TextBox? _dragSizeXBox, _dragSizeYBox;
    private readonly HashSet<Guid> _sceneDrawFailures = [];

    internal Vector2 ScenePan => _scenePan;

    internal float SceneZoom => _sceneZoom;

    internal bool IsSceneDragging => _scenePanning
        || _sceneMoveKind is not SceneViewMath.GizmoKind.None
        || _sceneResizeHandle is not SceneViewMath.ResizeHandle.None
        || _sceneRotating;

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
        SceneViewMath.EnumerateLayouts(Documents.Current.Current, viewportSize);

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
        out Vector2 xAxis, out Vector2 yAxis, out bool gizmoValid) =>
        TrySceneFrameWithLayouts(target, SceneLayouts(viewportSize), viewportSize, out cornersView, out pivotView, out xAxis, out yAxis, out gizmoValid);

    /// <summary>Resolves the selection frame from already computed layouts. Reuses the frame layout to avoid a second enumeration.</summary>
    private bool TrySceneFrameWithLayouts(
        SceneObject target, IReadOnlyList<SceneViewMath.LayoutEntry> layouts, Vector2 viewportSize,
        out Vector2[] cornersView, out Vector2 pivotView,
        out Vector2 xAxis, out Vector2 yAxis, out bool gizmoValid)
    {
        cornersView = [];
        pivotView = Vector2.Zero;
        xAxis = Vector2.UnitX;
        yAxis = Vector2.UnitY;
        gizmoValid = false;
        var entry = FindLayout(layouts, target);
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
        if (!SceneViewMath.TryGetTransformFrame(Documents.Current.Current, target, viewportSize, out _, out var parentWorld, out var world))
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
            && TrySceneFrameWithLayouts(selected, entries, viewportSize, out var corners, out var pivot, out var xAxis, out var yAxis, out var gizmoValid)
            && gizmoValid)
        {
            // Resize and rotate need a UiElement frame; bare group parents keep move only.
            if (corners.Length == 4 && selected.GetComponent<UiElement>() is not null)
            {
                if (SceneViewMath.HitRotateHandle(corners, viewPoint)
                    && BeginSceneRotate(selected, viewPoint))
                {
                    _scenePointer = e.Pointer;
                    e.Pointer.Capture(SceneViewport);
                    e.Handled = true;
                    return;
                }
                var resizeHandle = SceneViewMath.HitResizeHandle(corners, viewPoint);
                if (resizeHandle is not SceneViewMath.ResizeHandle.None
                    && BeginSceneResize(selected, resizeHandle, viewPoint))
                {
                    _scenePointer = e.Pointer;
                    e.Pointer.Capture(SceneViewport);
                    e.Handled = true;
                    return;
                }
            }
            if (BeginSceneMove(selected, SceneViewMath.HitGizmo(pivot, xAxis, yAxis, viewPoint), viewPoint))
            {
                _scenePointer = e.Pointer;
                e.Pointer.Capture(SceneViewport);
                e.Handled = true;
                return;
            }
        }
        SelectSceneObject(SceneViewMath.HitTest(entries, viewportSize, _scenePan, _sceneZoom, viewPoint, IsDrawableUi), focus: false);
        e.Handled = true;
    }

    private bool BeginSceneMove(SceneObject target, SceneViewMath.GizmoKind kind, Vector2 viewPoint)
    {
        if (IsPlaying || IsSceneDragging || kind is not (SceneViewMath.GizmoKind.X or SceneViewMath.GizmoKind.Y or SceneViewMath.GizmoKind.XY)
            || !IsDrawableSceneViewport(out var viewportSize)
            || !float.IsFinite(viewPoint.X) || !float.IsFinite(viewPoint.Y)
            || !Documents.Current.Current.Objects.Contains(target)) return false;
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
            if (!SceneViewMath.TryGetTransformFrame(Documents.Current.Current, target, viewportSize, out var parentSize, out var parentWorld, out _))
                return false;
            if (!SceneViewMath.TryGetParentAxes(parentWorld, out _, out _)) return false;
            _dragParentWorld = parentWorld;
            _dragParentSize = parentSize;
            _dragGeometry = (Vector2.Zero, Vector2.Zero, Vector2.Zero, Vector2.Zero, transform.LocalRotation, transform.LocalScale);
        }
        _sceneMoveKind = kind;
        _dragScene = Documents.Current.Current;
        _dragTarget = target;
        _dragTransform = transform;
        _dragElement = element;
        _dragParent = target.Parent;
        _dragStartLocal = transform.LocalPosition;
        _dragStartSizeDelta = element?.SizeDelta ?? Vector2.Zero;
        _dragAnchorSpan = element is null ? Vector2.Zero : _dragParentSize * (element.AnchorMax - element.AnchorMin);
        _dragPivot = element?.Pivot ?? Vector2.Zero;
        _dragStartRotation = transform.LocalRotation;
        _dragViewportSize = viewportSize;
        _dragStartScene = SceneViewMath.ViewToScene(viewPoint, _scenePan, _sceneZoom);
        return true;
    }

    private bool BeginSceneResize(SceneObject target, SceneViewMath.ResizeHandle handle, Vector2 viewPoint)
    {
        if (IsPlaying || IsSceneDragging || handle is SceneViewMath.ResizeHandle.None
            || !IsDrawableSceneViewport(out var viewportSize)
            || !float.IsFinite(viewPoint.X) || !float.IsFinite(viewPoint.Y)
            || !Documents.Current.Current.Objects.Contains(target)) return false;
        if (target.GetComponent<Transform>() is not { } transform) return false;
        if (target.GetComponent<UiElement>() is not { } element) return false;
        var entry = FindLayout(SceneLayouts(viewportSize), target);
        if (entry is null || !SceneViewMath.TryGetSelectionFrame(entry, _scenePan, _sceneZoom, out _, out _)) return false;
        // Refuses fully locked pivot sides outright; basis-vector probes stay strict per axis so half-locked corners still begin.
        SceneViewMath.ResizeAxes(element.Pivot, handle, out var adjustsX, out var adjustsY);
        if (!adjustsX && !adjustsY) return false;
        var world = entry.WorldScene;
        var liveX = !adjustsX
            || SceneViewMath.TrySceneDeltaToResize(new Vector2(world.M11, world.M12), world, element.Pivot, handle, out _);
        var liveY = !adjustsY
            || SceneViewMath.TrySceneDeltaToResize(new Vector2(world.M21, world.M22), world, element.Pivot, handle, out _);
        if (!liveX || !liveY) return false;
        _sceneResizeHandle = handle;
        _dragScene = Documents.Current.Current;
        _dragTarget = target;
        _dragTransform = transform;
        _dragElement = element;
        _dragParent = target.Parent;
        _dragStartLocal = transform.LocalPosition;
        _dragStartSizeDelta = element.SizeDelta;
        _dragAnchorSpan = entry.ParentSize * (element.AnchorMax - element.AnchorMin);
        _dragPivot = element.Pivot;
        _dragStartRotation = transform.LocalRotation;
        _dragParentWorld = entry.ParentWorld;
        _dragParentSize = entry.ParentSize;
        _dragGeometry = (element.AnchorMin, element.AnchorMax, element.Pivot, element.SizeDelta, transform.LocalRotation, transform.LocalScale);
        _dragViewportSize = viewportSize;
        _dragStartScene = SceneViewMath.ViewToScene(viewPoint, _scenePan, _sceneZoom);
        return true;
    }

    private bool BeginSceneRotate(SceneObject target, Vector2 viewPoint)
    {
        if (IsPlaying || IsSceneDragging
            || !IsDrawableSceneViewport(out var viewportSize)
            || !float.IsFinite(viewPoint.X) || !float.IsFinite(viewPoint.Y)
            || !Documents.Current.Current.Objects.Contains(target)) return false;
        if (target.GetComponent<Transform>() is not { } transform) return false;
        if (target.GetComponent<UiElement>() is not { } element) return false;
        var entry = FindLayout(SceneLayouts(viewportSize), target);
        if (entry is null || !SceneViewMath.TryGetSelectionFrame(entry, _scenePan, _sceneZoom, out var corners, out var pivotView)) return false;
        if (!SceneViewMath.HitRotateHandle(corners, viewPoint)) return false;
        var pivotScene = SceneViewMath.ViewToScene(pivotView, _scenePan, _sceneZoom);
        var startScene = SceneViewMath.ViewToScene(viewPoint, _scenePan, _sceneZoom);
        if ((startScene - pivotScene).LengthSquared() <= 1e-6f) return false;
        _sceneRotating = true;
        _dragScene = Documents.Current.Current;
        _dragTarget = target;
        _dragTransform = transform;
        _dragElement = element;
        _dragParent = target.Parent;
        _dragStartLocal = transform.LocalPosition;
        _dragStartSizeDelta = element.SizeDelta;
        _dragAnchorSpan = entry.ParentSize * (element.AnchorMax - element.AnchorMin);
        _dragPivot = element.Pivot;
        _dragStartRotation = transform.LocalRotation;
        _dragPivotScene = pivotScene;
        _dragParentWorld = entry.ParentWorld;
        _dragParentSize = entry.ParentSize;
        _dragGeometry = (element.AnchorMin, element.AnchorMax, element.Pivot, element.SizeDelta, transform.LocalRotation, transform.LocalScale);
        _dragViewportSize = viewportSize;
        _dragStartScene = startScene;
        return true;
    }

    /// <summary>Shared drag checks for move, resize, and rotate. Structural loss aborts; changed conditions cancel and restore the start.</summary>
    private bool ValidateSceneDragCommon(out Vector2 viewportSize)
    {
        viewportSize = SceneViewportSize();
        if (_sceneMoveKind is SceneViewMath.GizmoKind.None
            && _sceneResizeHandle is SceneViewMath.ResizeHandle.None
            && !_sceneRotating) return false;
        if (_dragScene is null || _dragTarget is null || _dragTransform is null
            || !ReferenceEquals(_dragScene, Documents.Current.Current) || !Documents.Current.Current.Objects.Contains(_dragTarget)
            || !ReferenceEquals(_dragTarget.GetComponent<Transform>(), _dragTransform))
        {
            AbortSceneDrag();
            return false;
        }
        var element = _dragTarget.GetComponent<UiElement>();
        if (IsPlaying || !IsDrawableSceneViewport(out _) || viewportSize != _dragViewportSize
            || !ReferenceEquals(_dragTarget.Parent, _dragParent) || !ReferenceEquals(element, _dragElement))
        {
            CancelSceneViewDrag();
            return false;
        }
        return true;
    }

    private bool ValidateSceneDrag()
    {
        if (_sceneMoveKind is not SceneViewMath.GizmoKind.None) return ValidateSceneMove();
        if (_sceneResizeHandle is not SceneViewMath.ResizeHandle.None) return ValidateSceneResize(out _, out _);
        if (_sceneRotating) return ValidateSceneRotate(out _, out _);
        return true;
    }

    private bool ValidateSceneMove()
    {
        if (_sceneMoveKind is SceneViewMath.GizmoKind.None) return false;
        if (!ValidateSceneDragCommon(out var viewportSize)) return false;
        var element = _dragTarget!.GetComponent<UiElement>();
        if (_dragElement is not null)
        {
            var entry = FindLayout(SceneLayouts(viewportSize), _dragTarget);
            if (element is null
                || (element.AnchorMin, element.AnchorMax, element.Pivot, element.SizeDelta, _dragTransform!.LocalRotation, _dragTransform.LocalScale) != _dragGeometry
                || entry is null || entry.ParentSize != _dragParentSize || entry.ParentWorld != _dragParentWorld)
            {
                CancelSceneViewDrag();
                return false;
            }
        }
        else
        {
            if ((_dragTransform!.LocalRotation, _dragTransform.LocalScale) != (_dragGeometry.Rotation, _dragGeometry.Scale))
            {
                CancelSceneViewDrag();
                return false;
            }
            if (!SceneViewMath.TryGetTransformFrame(Documents.Current.Current, _dragTarget, viewportSize, out var parentSize, out var parentWorld, out _)
                || parentSize != _dragParentSize || parentWorld != _dragParentWorld)
            {
                CancelSceneViewDrag();
                return false;
            }
        }
        return true;
    }

    private bool ValidateSceneResize(out Vector2 viewportSize, out SceneViewMath.LayoutEntry entry)
    {
        entry = null!;
        viewportSize = default;
        if (_sceneResizeHandle is SceneViewMath.ResizeHandle.None) return false;
        if (!ValidateSceneDragCommon(out viewportSize)) return false;
        var element = _dragElement!;
        var transform = _dragTransform!;
        // SizeDelta is the drag output; anchors, Pivot, rotation, and scale must stay at the start.
        if ((element.AnchorMin, element.AnchorMax, element.Pivot, transform.LocalRotation, transform.LocalScale)
            != (_dragGeometry.Min, _dragGeometry.Max, _dragGeometry.Pivot, _dragGeometry.Rotation, _dragGeometry.Scale))
        {
            CancelSceneViewDrag();
            return false;
        }
        var found = FindLayout(SceneLayouts(viewportSize), _dragTarget!);
        if (found is null || found.ParentSize != _dragParentSize || found.ParentWorld != _dragParentWorld)
        {
            CancelSceneViewDrag();
            return false;
        }
        entry = found;
        return true;
    }

    private bool ValidateSceneRotate(out Vector2 viewportSize, out SceneViewMath.LayoutEntry entry)
    {
        entry = null!;
        viewportSize = default;
        if (!_sceneRotating) return false;
        if (!ValidateSceneDragCommon(out viewportSize)) return false;
        var element = _dragElement!;
        var transform = _dragTransform!;
        // LocalRotation is the drag output; anchors, Pivot, size, and scale must stay at the start.
        if ((element.AnchorMin, element.AnchorMax, element.Pivot, element.SizeDelta, transform.LocalScale)
            != (_dragGeometry.Min, _dragGeometry.Max, _dragGeometry.Pivot, _dragGeometry.Size, _dragGeometry.Scale))
        {
            CancelSceneViewDrag();
            return false;
        }
        var found = FindLayout(SceneLayouts(viewportSize), _dragTarget!);
        if (found is null || found.ParentSize != _dragParentSize || found.ParentWorld != _dragParentWorld)
        {
            CancelSceneViewDrag();
            return false;
        }
        entry = found;
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
        UpdateSceneDrag(viewPoint);
        e.Handled = true;
    }

    private bool UpdateSceneDrag(Vector2 viewPoint)
    {
        if (_sceneMoveKind is not SceneViewMath.GizmoKind.None) return UpdateSceneMove(viewPoint);
        if (_sceneResizeHandle is not SceneViewMath.ResizeHandle.None) return UpdateSceneResize(viewPoint);
        if (_sceneRotating) return UpdateSceneRotate(viewPoint);
        return false;
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

    private bool UpdateSceneResize(Vector2 viewPoint)
    {
        if (!ValidateSceneResize(out _, out var entry)) return false;
        var delta = SceneViewMath.ViewToScene(viewPoint, _scenePan, _sceneZoom) - _dragStartScene;
        if (!SceneViewMath.TrySceneDeltaToResize(delta, entry.WorldScene, _dragPivot, _sceneResizeHandle, out var resizeDelta)
            || !SceneViewMath.TryApplyResize(_dragStartSizeDelta, _dragAnchorSpan, resizeDelta, _dragPivot, _sceneResizeHandle, out var next))
        {
            CancelSceneViewDrag();
            return false;
        }
        _dragElement!.SizeDelta = next;
        SyncInspectorToDrag();
        return true;
    }

    private bool UpdateSceneRotate(Vector2 viewPoint)
    {
        if (!ValidateSceneRotate(out _, out _)) return false;
        var currentScene = SceneViewMath.ViewToScene(viewPoint, _scenePan, _sceneZoom);
        if (!SceneViewMath.TryRotateAngle(_dragPivotScene, _dragStartScene, currentScene, out var radians)
            || !SceneViewMath.TryApplyRotation(_dragStartRotation, radians, out var next))
        {
            CancelSceneViewDrag();
            return false;
        }
        _dragTransform!.LocalRotation = next;
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

    private bool ConfirmSceneDrag(Vector2 viewPoint)
    {
        if (_sceneMoveKind is not SceneViewMath.GizmoKind.None) return ConfirmSceneMove(viewPoint);
        if (_sceneResizeHandle is not SceneViewMath.ResizeHandle.None) return ConfirmSceneResize(viewPoint);
        if (_sceneRotating) return ConfirmSceneRotate(viewPoint);
        return false;
    }

    private bool ConfirmSceneResize(Vector2 viewPoint)
    {
        if (!UpdateSceneResize(viewPoint)) return false;
        var target = _dragTarget!;
        var transform = _dragTransform!;
        var changed = _dragElement!.SizeDelta != _dragStartSizeDelta;
        AbortSceneDrag();
        if (changed) MarkSceneChanged();
        SyncInspectorToDragTarget(target, transform);
        return changed;
    }

    private bool ConfirmSceneRotate(Vector2 viewPoint)
    {
        if (!UpdateSceneRotate(viewPoint)) return false;
        var target = _dragTarget!;
        var transform = _dragTransform!;
        var changed = transform.LocalRotation != _dragStartRotation;
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
        else ConfirmSceneDrag(new Vector2((float)point.X, (float)point.Y));
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
        _sceneResizeHandle = SceneViewMath.ResizeHandle.None;
        _sceneRotating = false;
        _dragScene = null;
        _dragTarget = null;
        _dragTransform = null;
        _dragParent = null;
        _dragElement = null;
        _dragPosXBox = _dragPosYBox = _dragPosZBox = null;
        _dragRotXBox = _dragRotYBox = _dragRotZBox = _dragRotWBox = null;
        _dragSizeXBox = _dragSizeYBox = null;
        // Clear state before CaptureLost is raised; never release capture stolen by another control.
        if (ReferenceEquals(pointer?.Captured, SceneViewport)) pointer!.Capture(null);
    }

    /// <summary>Cancel the current operation and release capture. Never write back into a deleted or replaced scene.</summary>
    internal void CancelSceneViewDrag()
    {
        var target = _dragTarget;
        var transform = _dragTransform;
        var start = _dragStartLocal;
        var startSize = _dragStartSizeDelta;
        var startRotation = _dragStartRotation;
        var element = _dragElement;
        var scene = _dragScene;
        AbortSceneDrag();
        if (target is null || transform is null || !ReferenceEquals(scene, Documents.Current.Current)
            || !Documents.Current.Current.Objects.Contains(target) || !ReferenceEquals(target.GetComponent<Transform>(), transform)) return;
        transform.LocalPosition = start;
        transform.LocalRotation = startRotation;
        if (element is not null && ReferenceEquals(target.GetComponent<UiElement>(), element))
            element.SizeDelta = startSize;
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
        _sceneViewSyncing = true;
        try
        {
            if (TryGetDragTransformBoxes(transform, out var posX, out var posY, out var posZ,
                out var rotX, out var rotY, out var rotZ, out var rotW))
            {
                SyncDragNumberBox(posX, transform.LocalPosition.X);
                SyncDragNumberBox(posY, transform.LocalPosition.Y);
                SyncDragNumberBox(posZ, transform.LocalPosition.Z);
                SyncDragNumberBox(rotX, transform.LocalRotation.X);
                SyncDragNumberBox(rotY, transform.LocalRotation.Y);
                SyncDragNumberBox(rotZ, transform.LocalRotation.Z);
                SyncDragNumberBox(rotW, transform.LocalRotation.W);
            }
            if (target.GetComponent<UiElement>() is { } element
                && TryGetDragSizeBoxes(element, out var sizeX, out var sizeY))
            {
                SyncDragNumberBox(sizeX, element.SizeDelta.X);
                SyncDragNumberBox(sizeY, element.SizeDelta.Y);
            }
        }
        finally
        {
            _sceneViewSyncing = false;
        }
    }

    /// <summary>Resolves the cached LocalPosition and LocalRotation boxes by automation name. Reuses them across drags when still attached.</summary>
    private bool TryGetDragTransformBoxes(Transform transform,
        out TextBox posX, out TextBox posY, out TextBox posZ,
        out TextBox rotX, out TextBox rotY, out TextBox rotZ, out TextBox rotW)
    {
        posX = _dragPosXBox!;
        posY = _dragPosYBox!;
        posZ = _dragPosZBox!;
        rotX = _dragRotXBox!;
        rotY = _dragRotYBox!;
        rotZ = _dragRotZBox!;
        rotW = _dragRotWBox!;
        if (posX is not null && posY is not null && posZ is not null
            && rotX is not null && rotY is not null && rotZ is not null && rotW is not null
            && IsDragBoxAttached(posX, "Transform.LocalPosition.X")
            && IsDragBoxAttached(posY, "Transform.LocalPosition.Y")
            && IsDragBoxAttached(posZ, "Transform.LocalPosition.Z")
            && IsDragBoxAttached(rotX, "Transform.LocalRotation.X")
            && IsDragBoxAttached(rotY, "Transform.LocalRotation.Y")
            && IsDragBoxAttached(rotZ, "Transform.LocalRotation.Z")
            && IsDragBoxAttached(rotW, "Transform.LocalRotation.W"))
            return true;
        posX = posY = posZ = rotX = rotY = rotZ = rotW = null!;
        _dragPosXBox = _dragPosYBox = _dragPosZBox = null;
        _dragRotXBox = _dragRotYBox = _dragRotZBox = _dragRotWBox = null;
        var card = ComponentEditors.Children.OfType<Border>()
            .FirstOrDefault(candidate => ReferenceEquals(candidate.Tag, transform));
        if (card is null)
            return false;
        TextBox? px = null, py = null, pz = null, rx = null, ry = null, rz = null, rw = null;
        foreach (var box in card.GetVisualDescendants().OfType<TextBox>())
        {
            var name = box.GetValue(AutomationProperties.NameProperty) as string;
            if (name == "Transform.LocalPosition.X") px = box;
            else if (name == "Transform.LocalPosition.Y") py = box;
            else if (name == "Transform.LocalPosition.Z") pz = box;
            else if (name == "Transform.LocalRotation.X") rx = box;
            else if (name == "Transform.LocalRotation.Y") ry = box;
            else if (name == "Transform.LocalRotation.Z") rz = box;
            else if (name == "Transform.LocalRotation.W") rw = box;
            if (px is not null && py is not null && pz is not null
                && rx is not null && ry is not null && rz is not null && rw is not null) break;
        }
        if (px is null || py is null || pz is null || rx is null || ry is null || rz is null || rw is null)
            return false;
        _dragPosXBox = posX = px;
        _dragPosYBox = posY = py;
        _dragPosZBox = posZ = pz;
        _dragRotXBox = rotX = rx;
        _dragRotYBox = rotY = ry;
        _dragRotZBox = rotZ = rz;
        _dragRotWBox = rotW = rw;
        return true;
    }

    /// <summary>Resolves the cached SizeDelta boxes by automation name. Reuses them across drags when still attached.</summary>
    private bool TryGetDragSizeBoxes(UiElement element, out TextBox xBox, out TextBox yBox)
    {
        xBox = _dragSizeXBox!;
        yBox = _dragSizeYBox!;
        if (xBox is not null && yBox is not null
            && IsDragBoxAttached(xBox, "UiElement.SizeDelta.X")
            && IsDragBoxAttached(yBox, "UiElement.SizeDelta.Y"))
            return true;
        xBox = yBox = null!;
        _dragSizeXBox = _dragSizeYBox = null;
        var card = ComponentEditors.Children.OfType<Border>()
            .FirstOrDefault(candidate => ReferenceEquals(candidate.Tag, element));
        if (card is null)
            return false;
        TextBox? x = null, y = null;
        foreach (var box in card.GetVisualDescendants().OfType<TextBox>())
        {
            var name = box.GetValue(AutomationProperties.NameProperty) as string;
            if (name == "UiElement.SizeDelta.X") x = box;
            else if (name == "UiElement.SizeDelta.Y") y = box;
            if (x is not null && y is not null) break;
        }
        if (x is null || y is null)
            return false;
        _dragSizeXBox = xBox = x;
        _dragSizeYBox = yBox = y;
        return true;
    }

    private bool IsDragBoxAttached(TextBox box, string automationName) =>
        box.GetValue(AutomationProperties.NameProperty) as string == automationName
        && box.GetVisualAncestors().Contains(ComponentEditors);

    /// <summary>Updates one cached box only when the displayed value differs. Skips validation churn for already valid fields.</summary>
    private void SyncDragNumberBox(TextBox box, float value)
    {
        var text = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (box.Text != text)
            box.Text = text;
        if (IsInvalidInput(box))
            MarkInvalid(box, null, "Enter a number — Press Esc to revert");
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
        return true;
    }

    /// <summary>Move start for headless verification. Takes the already-hit gizmo kind and keeps the start position and parent layout.</summary>
    internal bool TryBeginMoveForTest(SceneObject target, SceneViewMath.GizmoKind kind, Vector2 viewPoint) =>
        BeginSceneMove(target, kind, viewPoint);

    internal bool TryBeginResizeForTest(SceneObject target, SceneViewMath.ResizeHandle handle, Vector2 viewPoint) =>
        BeginSceneResize(target, handle, viewPoint);

    internal bool TryBeginRotateForTest(SceneObject target, Vector2 viewPoint) =>
        BeginSceneRotate(target, viewPoint);

    internal bool TryUpdateMoveForTest(Vector2 viewPoint) => UpdateSceneMove(viewPoint);

    internal bool TryUpdateResizeForTest(Vector2 viewPoint) => UpdateSceneResize(viewPoint);

    internal bool TryUpdateRotateForTest(Vector2 viewPoint) => UpdateSceneRotate(viewPoint);

    internal bool TryConfirmMoveForTest(Vector2 viewPoint) => ConfirmSceneMove(viewPoint);

    internal bool TryConfirmResizeForTest(Vector2 viewPoint) => ConfirmSceneResize(viewPoint);

    internal bool TryConfirmRotateForTest(Vector2 viewPoint) => ConfirmSceneRotate(viewPoint);

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
        return true;
    }
}
