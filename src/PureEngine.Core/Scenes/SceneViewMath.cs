using System.Numerics;

namespace PureEngine.Core;

/// <summary>Shared coordinate and layout math for V4 Scene View editing. Data keeps XYZ while gizmos handle XY only.</summary>
/// <remarks>Rendering, selection, and gizmos share the same UiLayout results and view transform. The parent area for anchors is unaffected by pan/zoom.</remarks>
public static class SceneViewMath
{
    public const float MinZoom = 0.25f;
    public const float MaxZoom = 8f;
    public const float GridBaseStep = 50f;
    public const float GridMinScreen = 48f;
    public const float GridMaxScreen = 96f;
    public const float FitPad = 24f;
    public const float GizmoLength = 48f;
    public const float GizmoShaftStart = 8f;
    public const float GizmoShaftHalfWidth = 4f;
    public const float GizmoHeadSize = 12f;
    public const float GizmoCenterSize = 14f;
    public const float ResizeHandleSize = 10f;
    public const float RotateHandleOffset = 28f;
    public const float RotateHandleRadius = 8f;

    public sealed record LayoutEntry(
        SceneObject Object,
        Vector2 Size,
        Matrix4x4 WorldScene,
        Vector2 ParentSize,
        Matrix4x4 ParentWorld);

    public enum GizmoKind
    {
        None,
        X,
        Y,
        XY,
    }

    /// <summary>Resize handles on the selection frame. Corners adjust both axes, edges adjust one.</summary>
    public enum ResizeHandle
    {
        None,
        Left,
        Top,
        Right,
        Bottom,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
    }

    public static bool IsValidViewport(Vector2 viewportSize) =>
        float.IsFinite(viewportSize.X) && float.IsFinite(viewportSize.Y)
        && viewportSize.X >= 10 && viewportSize.Y >= 10;

    public static bool IsValidView(Vector2 pan, float zoom) =>
        float.IsFinite(pan.X) && float.IsFinite(pan.Y)
        && float.IsFinite(zoom) && zoom >= MinZoom && zoom <= MaxZoom;

    /// <summary>Converts Scene coordinates to logical view coordinates. Callers guarantee a valid view.</summary>
    public static Vector2 SceneToView(Vector2 scene, Vector2 pan, float zoom) =>
        scene * zoom + pan;

    /// <summary>Converts logical view coordinates to Scene coordinates. Callers guarantee a valid view.</summary>
    public static Vector2 ViewToScene(Vector2 view, Vector2 pan, float zoom) =>
        (view - pan) / zoom;

    public static Matrix4x4 ViewMatrix(Vector2 pan, float zoom) =>
        new(zoom, 0, 0, 0,
            0, zoom, 0, 0,
            0, 0, 1, 0,
            pan.X, pan.Y, 0, 1);

    /// <summary>Zooms while keeping the Scene coordinate under the pointer. Invalid values are a no-op returning false.</summary>
    public static bool TryZoomAt(
        Vector2 viewPoint, Vector2 viewportSize, Vector2 pan, float zoom, float factor,
        out Vector2 nextPan, out float nextZoom)
    {
        nextPan = pan;
        nextZoom = zoom;
        if (!IsValidViewport(viewportSize) || !IsValidView(pan, zoom))
            return false;
        if (!float.IsFinite(viewPoint.X) || !float.IsFinite(viewPoint.Y))
            return false;
        if (!float.IsFinite(factor) || factor <= 0)
            return false;
        var zoomed = Math.Clamp(zoom * factor, MinZoom, MaxZoom);
        if (!float.IsFinite(zoomed))
            return false;
        if (zoomed == zoom)
            return true;
        nextZoom = zoomed;
        nextPan = viewPoint - ((viewPoint - pan) * (zoomed / zoom));
        if (!float.IsFinite(nextPan.X) || !float.IsFinite(nextPan.Y))
        {
            nextPan = pan;
            nextZoom = zoom;
            return false;
        }
        return true;
    }

    /// <summary>Computes the grid spacing (in Scene units) for the zoom level. Keeps 48-96px on screen.</summary>
    public static float GridStep(float zoom)
    {
        if (!float.IsFinite(zoom) || zoom <= 0)
            return GridBaseStep;
        var step = GridBaseStep;
        for (var i = 0; i < 32 && step * zoom < GridMinScreen; i++)
            step *= 2;
        for (var i = 0; i < 32 && step * zoom >= GridMaxScreen; i++)
            step /= 2;
        return float.IsFinite(step) && step > 0 ? step : GridBaseStep;
    }

    public static bool TryGetLayout(
        SceneObject item, Vector2 parentSize, Matrix4x4 parentWorld,
        out Vector2 size, out Matrix4x4 world)
    {
        size = Vector2.Zero;
        world = Matrix4x4.Identity;
        if (item.GetComponent<Transform>() is not { } transform)
            return false;
        if (item.GetComponent<UiElement>() is not { } element)
            return false;
        try
        {
            (size, world) = UiLayout.Calculate(parentSize, parentWorld, transform, element);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
        return true;
    }

    /// <summary>Enumerates valid UI layouts depth-first in parent-to-child and sibling order. This is layout-calculation order, not render order. Children of broken layouts receive the parent area.</summary>
    /// <remarks>Render and hit-test ordering is rearranged into ascending Order via <see cref="SortForRender"/>. Layout values are unchanged by sorting. Transform-only group nodes have no rectangle, but their transform is passed to children.</remarks>
    public static IReadOnlyList<LayoutEntry> EnumerateLayouts(Scene scene, Vector2 viewportSize)
    {
        ArgumentNullException.ThrowIfNull(scene);
        List<LayoutEntry> entries = [];
        if (!float.IsFinite(viewportSize.X) || !float.IsFinite(viewportSize.Y)
            || viewportSize.X < 0 || viewportSize.Y < 0)
            return entries;
        foreach (var root in scene.RootObjects)
            AppendRecursive(root, viewportSize, Matrix4x4.Identity, entries);
        return entries;
    }

    private static void AppendRecursive(
        SceneObject item, Vector2 parentSize, Matrix4x4 parentWorld, List<LayoutEntry> entries)
    {
        var size = parentSize;
        var world = parentWorld;
        if (TryGetLayout(item, parentSize, parentWorld, out var resolvedSize, out var resolvedWorld))
        {
            size = resolvedSize;
            world = resolvedWorld;
            entries.Add(new LayoutEntry(item, size, world, parentSize, parentWorld));
        }
        else if (item.GetComponent<UiElement>() is null && item.GetComponent<Transform>() is { } bare
            && TryPropagateBareTransform(parentWorld, bare, out var bareWorld))
        {
            // Transform-only group nodes have no rectangle, but their transform is passed to children. Size is not inherited.
            world = bareWorld;
        }
        foreach (var child in item.Children)
            AppendRecursive(child, size, world, entries);
    }

    /// <summary>Converts a Transform-only group node to the world passed to child layouts. Does not create a UiElement rectangle.</summary>
    /// <remarks>Uses row-vector order (local * parent) for the same composition order as UiLayout. Non-finite matrices return false.</remarks>
    public static bool TryPropagateBareTransform(Matrix4x4 parentWorld, Transform transform, out Matrix4x4 world)
    {
        world = Matrix4x4.Identity;
        ArgumentNullException.ThrowIfNull(transform);
        if (!IsFiniteMatrix(parentWorld))
            return false;
        var local = transform.LocalMatrix;
        if (!IsFiniteMatrix(local))
            return false;
        var combined = local * parentWorld;
        if (!IsFiniteMatrix(combined))
            return false;
        world = combined;
        return true;
    }

    /// <summary>Resolves the parent area, parent world, and self world for a target with or without a UiElement. Used for gizmo display and drag validation.</summary>
    /// <remarks>Layout calculation uses the same traversal rules as EnumerateLayouts (UiLayout first, Transform-only passed through). Missing targets, missing Transforms, or non-finite values return false.</remarks>
    public static bool TryGetTransformFrame(
        Scene scene, SceneObject target, Vector2 viewportSize,
        out Vector2 parentSize, out Matrix4x4 parentWorld, out Matrix4x4 world)
    {
        parentSize = Vector2.Zero;
        parentWorld = Matrix4x4.Identity;
        world = Matrix4x4.Identity;
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(target);
        if (target.GetComponent<Transform>() is null)
            return false;
        if (!float.IsFinite(viewportSize.X) || !float.IsFinite(viewportSize.Y)
            || viewportSize.X < 0 || viewportSize.Y < 0)
            return false;
        foreach (var root in scene.RootObjects)
            if (TryFindFrame(root, target, viewportSize, Matrix4x4.Identity, out parentSize, out parentWorld, out world))
                return true;
        return false;
    }

    private static bool TryFindFrame(
        SceneObject current, SceneObject target, Vector2 parentSize, Matrix4x4 parentWorld,
        out Vector2 foundParentSize, out Matrix4x4 foundParentWorld, out Matrix4x4 foundWorld)
    {
        foundParentSize = parentSize;
        foundParentWorld = parentWorld;
        foundWorld = parentWorld;
        var size = parentSize;
        var world = parentWorld;
        var hasWorld = false;
        if (TryGetLayout(current, parentSize, parentWorld, out var resolvedSize, out var resolvedWorld))
        {
            size = resolvedSize;
            world = resolvedWorld;
            hasWorld = true;
        }
        else if (current.GetComponent<UiElement>() is null && current.GetComponent<Transform>() is { } bare
            && TryPropagateBareTransform(parentWorld, bare, out var bareWorld))
        {
            world = bareWorld;
            hasWorld = true;
        }
        if (ReferenceEquals(current, target))
        {
            if (!hasWorld)
                return false;
            foundParentSize = parentSize;
            foundParentWorld = parentWorld;
            foundWorld = world;
            return true;
        }
        foreach (var child in current.Children)
            if (TryFindFrame(child, target, size, world, out foundParentSize, out foundParentWorld, out foundWorld))
                return true;
        return false;
    }

    private static bool IsFiniteMatrix(Matrix4x4 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14)
        && float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24)
        && float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34)
        && float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);

    private static Matrix3x2 ToPlane(Matrix4x4 world) =>
        new(world.M11, world.M12, world.M21, world.M22, world.M41, world.M42);

    /// <summary>Returns the largest Order among the Image and Text on the target. Zero when there is neither.</summary>
    /// <remarks>Image and Text on the same object draw as a unit (Image first, then Text) and sort by the larger Order. Use separate objects for independent ordering. Order is never inherited from the parent.</remarks>
    public static int GetRenderOrder(SceneObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        // The current render path handles Image and Text. Attach order of other derived types does not affect the result.
        var order = 0;
        var hasTarget = false;
        if (item.GetComponent<Image>() is { Order: var imageOrder })
        {
            order = imageOrder;
            hasTarget = true;
        }
        if (item.GetComponent<Text>() is { Order: var textOrder })
        {
            order = hasTarget ? Math.Max(order, textOrder) : textOrder;
            hasTarget = true;
        }
        return hasTarget ? order : 0;
    }

    /// <summary>Stably sorts layout entries into ascending Order. Rendering and hit-testing share this step.</summary>
    /// <remarks>Equal values keep the original parent-to-child and sibling order. Only the order changes; layout values are unchanged.</remarks>
    public static IReadOnlyList<LayoutEntry> SortForRender(IReadOnlyList<LayoutEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return [.. entries.OrderBy(entry => GetRenderOrder(entry.Object))];
    }

    private static Matrix3x2 ViewPlane(Vector2 pan, float zoom) =>
        new(zoom, 0, 0, zoom, pan.X, pan.Y);

    /// <summary>Hit-tests rectangles in reverse render order (descending Order, later entries in front on ties). Reflects rotation, scale, and Pivot; does not test transparent pixels.</summary>
    /// <remarks>Uses the same <see cref="SortForRender"/> as rendering and tests from front to back.</remarks>
    public static SceneObject? HitTest(
        IReadOnlyList<LayoutEntry> entries, Vector2 viewportSize, Vector2 pan, float zoom,
        Vector2 viewPoint, Func<SceneObject, bool> isDrawable)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(isDrawable);
        if (!IsValidViewport(viewportSize) || !IsValidView(pan, zoom))
            return null;
        if (!float.IsFinite(viewPoint.X) || !float.IsFinite(viewPoint.Y))
            return null;
        if (viewPoint.X < 0 || viewPoint.Y < 0 || viewPoint.X > viewportSize.X || viewPoint.Y > viewportSize.Y)
            return null;
        var view = ViewPlane(pan, zoom);
        var ordered = SortForRender(entries);
        for (var i = ordered.Count - 1; i >= 0; i--)
        {
            var entry = ordered[i];
            if (!isDrawable(entry.Object))
                continue;
            if (!float.IsFinite(entry.Size.X) || !float.IsFinite(entry.Size.Y))
                continue;
            if (entry.Size.X <= 0 || entry.Size.Y <= 0)
                continue;
            var combined = ToPlane(entry.WorldScene) * view;
            if (!Matrix3x2.Invert(combined, out var inverted))
                continue;
            if (!float.IsFinite(inverted.M11 + inverted.M12 + inverted.M21 + inverted.M22 + inverted.M31 + inverted.M32))
                continue;
            var local = Vector2.Transform(viewPoint, inverted);
            if (!float.IsFinite(local.X) || !float.IsFinite(local.Y))
                continue;
            if (local.X >= 0 && local.X <= entry.Size.X && local.Y >= 0 && local.Y <= entry.Size.Y)
                return entry.Object;
        }
        return null;
    }

    /// <summary>Computes the selection-frame corners and Pivot in view coordinates. Derived from the same matrices as UiLayout.</summary>
    public static bool TryGetSelectionFrame(
        LayoutEntry entry, Vector2 pan, float zoom,
        out Vector2[] cornersView, out Vector2 pivotView)
    {
        cornersView = [];
        pivotView = Vector2.Zero;
        if (!IsValidView(pan, zoom))
            return false;
        if (!float.IsFinite(entry.Size.X) || !float.IsFinite(entry.Size.Y))
            return false;
        if (entry.Size.X <= 0 || entry.Size.Y <= 0)
            return false;
        var transform = entry.Object.GetComponent<Transform>();
        var element = entry.Object.GetComponent<UiElement>();
        if (transform is null || element is null)
            return false;
        if (!float.IsFinite(element.Pivot.X) || !float.IsFinite(element.Pivot.Y))
            return false;
        var combined = ToPlane(entry.WorldScene) * ViewPlane(pan, zoom);
        if (!Matrix3x2.Invert(combined, out var inverse)
            || !float.IsFinite(inverse.M11 + inverse.M12 + inverse.M21 + inverse.M22 + inverse.M31 + inverse.M32))
            return false;
        Vector2[] locals =
        [
            new(0, 0),
            new(entry.Size.X, 0),
            new(entry.Size.X, entry.Size.Y),
            new(0, entry.Size.Y),
        ];
        var views = new Vector2[4];
        for (var i = 0; i < 4; i++)
        {
            views[i] = Vector2.Transform(locals[i], combined);
            if (!float.IsFinite(views[i].X) || !float.IsFinite(views[i].Y))
                return false;
        }
        var pivotLocal = element.Pivot * entry.Size;
        var pivot = Vector2.Transform(pivotLocal, combined);
        if (!float.IsFinite(pivot.X) || !float.IsFinite(pivot.Y))
            return false;
        cornersView = views;
        pivotView = pivot;
        return true;
    }

    /// <summary>Derives the local XY axis directions (unit vectors in Scene units) from the parent UI layout. Excludes the target's own rotation.</summary>
    public static bool TryGetParentAxes(Matrix4x4 parentWorld, out Vector2 xAxis, out Vector2 yAxis)
    {
        xAxis = Vector2.UnitX;
        yAxis = Vector2.UnitY;
        if (!TrySceneDeltaToLocal(Vector2.Zero, parentWorld, out _)) return false;
        var x = new Vector2(parentWorld.M11, parentWorld.M12);
        var y = new Vector2(parentWorld.M21, parentWorld.M22);
        if (!float.IsFinite(x.X) || !float.IsFinite(x.Y) || !float.IsFinite(y.X) || !float.IsFinite(y.Y))
            return false;
        if (x.LengthSquared() <= float.Epsilon || y.LengthSquared() <= float.Epsilon)
            return false;
        xAxis = Vector2.Normalize(x);
        yAxis = Vector2.Normalize(y);
        return float.IsFinite(xAxis.X + xAxis.Y + yAxis.X + yAxis.Y);
    }

    /// <summary>Gizmo hit-testing. Appearance and hit width stay in screen logical pixels. Tests center, then X, then Y.</summary>
    public static GizmoKind HitGizmo(
        Vector2 pivotView, Vector2 xAxis, Vector2 yAxis, Vector2 viewPoint)
    {
        if (!float.IsFinite(pivotView.X + pivotView.Y + viewPoint.X + viewPoint.Y))
            return GizmoKind.None;
        if (!float.IsFinite(xAxis.X + xAxis.Y + yAxis.X + yAxis.Y))
            return GizmoKind.None;
        if (xAxis.LengthSquared() <= float.Epsilon || yAxis.LengthSquared() <= float.Epsilon)
            return GizmoKind.None;
        var x = Vector2.Normalize(xAxis);
        var y = Vector2.Normalize(yAxis);
        var halfCenter = GizmoCenterSize / 2;
        if (Math.Abs(viewPoint.X - pivotView.X) <= halfCenter
            && Math.Abs(viewPoint.Y - pivotView.Y) <= halfCenter)
            return GizmoKind.XY;
        if (HitAxis(pivotView, x, viewPoint))
            return GizmoKind.X;
        if (HitAxis(pivotView, y, viewPoint))
            return GizmoKind.Y;
        return GizmoKind.None;
    }

    private static bool HitAxis(Vector2 pivot, Vector2 dir, Vector2 point)
    {
        var offset = point - pivot;
        var along = Vector2.Dot(offset, dir);
        var perpendicular = offset - (dir * along);
        var baseStart = GizmoLength - GizmoHeadSize;
        if (along >= baseStart && along <= GizmoLength)
        {
            var halfHead = GizmoHeadSize / 2;
            var widthAt = halfHead * ((GizmoLength - along) / GizmoHeadSize);
            if (perpendicular.Length() <= widthAt)
                return true;
        }
        if (along < GizmoShaftStart || along > GizmoLength)
            return false;
        return perpendicular.Length() <= GizmoShaftHalfWidth;
    }

    /// <summary>Maps a Scene delta back to a local delta with the inverse parent XY transform. Z is not handled.</summary>
    public static bool TrySceneDeltaToLocal(
        Vector2 sceneDelta, Matrix4x4 parentWorld, out Vector2 localDelta)
    {
        localDelta = Vector2.Zero;
        if (!float.IsFinite(sceneDelta.X) || !float.IsFinite(sceneDelta.Y))
            return false;
        var a = parentWorld.M11;
        var b = parentWorld.M12;
        var c = parentWorld.M21;
        var d = parentWorld.M22;
        if (!float.IsFinite(a + b + c + d))
            return false;
        var determinant = (a * d) - (b * c);
        if (!float.IsFinite(determinant) || determinant == 0)
            return false;
        var x = ((sceneDelta.X * d) - (sceneDelta.Y * c)) / determinant;
        var y = ((sceneDelta.Y * a) - (sceneDelta.X * b)) / determinant;
        if (!float.IsFinite(x) || !float.IsFinite(y))
            return false;
        localDelta = new Vector2(x, y);
        return true;
    }

    /// <summary>Updates only the X and Y of LocalPosition from the delta off the start value, preserving Z.</summary>
    public static bool TryApplyMove(
        Vector3 startLocal, Vector2 localDelta, GizmoKind kind, out Vector3 nextLocal)
    {
        nextLocal = startLocal;
        if (!float.IsFinite(startLocal.X + startLocal.Y + startLocal.Z))
            return false;
        if (!float.IsFinite(localDelta.X) || !float.IsFinite(localDelta.Y))
            return false;
        if (kind is GizmoKind.None)
            return false;
        var next = startLocal;
        if (kind is GizmoKind.X or GizmoKind.XY)
            next.X = startLocal.X + localDelta.X;
        if (kind is GizmoKind.Y or GizmoKind.XY)
            next.Y = startLocal.Y + localDelta.Y;
        if (!float.IsFinite(next.X) || !float.IsFinite(next.Y) || !float.IsFinite(next.Z))
            return false;
        nextLocal = next;
        return true;
    }

    /// <summary>Returns the screen-space center of one resize handle from view-space frame corners (top-left, top-right, bottom-right, bottom-left).</summary>
    public static bool TryGetResizeCenter(Vector2[] cornersView, ResizeHandle handle, out Vector2 center)
    {
        center = Vector2.Zero;
        if (handle is ResizeHandle.None || cornersView.Length != 4)
            return false;
        foreach (var corner in cornersView)
        {
            if (!float.IsFinite(corner.X) || !float.IsFinite(corner.Y))
                return false;
        }
        var topLeft = cornersView[0];
        var topRight = cornersView[1];
        var bottomRight = cornersView[2];
        var bottomLeft = cornersView[3];
        var resolved = handle switch
        {
            ResizeHandle.Left => (bottomLeft + topLeft) / 2,
            ResizeHandle.Top => (topLeft + topRight) / 2,
            ResizeHandle.Right => (topRight + bottomRight) / 2,
            ResizeHandle.Bottom => (bottomRight + bottomLeft) / 2,
            ResizeHandle.TopLeft => topLeft,
            ResizeHandle.TopRight => topRight,
            ResizeHandle.BottomLeft => bottomLeft,
            ResizeHandle.BottomRight => bottomRight,
            _ => new Vector2(float.NaN, float.NaN),
        };
        if (!float.IsFinite(resolved.X) || !float.IsFinite(resolved.Y))
            return false;
        center = resolved;
        return true;
    }

    /// <summary>Returns the screen-space rotate handle center above the top edge midpoint.</summary>
    public static bool TryGetRotateCenter(Vector2[] cornersView, out Vector2 center)
    {
        center = Vector2.Zero;
        if (!TryGetResizeCenter(cornersView, ResizeHandle.Top, out var top))
            return false;
        var resolved = top - new Vector2(0, RotateHandleOffset);
        if (!float.IsFinite(resolved.X) || !float.IsFinite(resolved.Y))
            return false;
        center = resolved;
        return true;
    }

    /// <summary>Hit-tests the resize squares in screen logical pixels. Corners win over edges on overlap.</summary>
    public static ResizeHandle HitResizeHandle(Vector2[] cornersView, Vector2 viewPoint)
    {
        if (cornersView.Length != 4 || !float.IsFinite(viewPoint.X) || !float.IsFinite(viewPoint.Y))
            return ResizeHandle.None;
        ResizeHandle[] order =
        [
            ResizeHandle.TopLeft, ResizeHandle.TopRight, ResizeHandle.BottomLeft, ResizeHandle.BottomRight,
            ResizeHandle.Left, ResizeHandle.Top, ResizeHandle.Right, ResizeHandle.Bottom,
        ];
        var half = ResizeHandleSize / 2;
        foreach (var handle in order)
        {
            if (!TryGetResizeCenter(cornersView, handle, out var center))
                return ResizeHandle.None;
            if (Math.Abs(viewPoint.X - center.X) <= half && Math.Abs(viewPoint.Y - center.Y) <= half)
                return handle;
        }
        return ResizeHandle.None;
    }

    /// <summary>Hit-tests the rotate handle circle in screen logical pixels.</summary>
    public static bool HitRotateHandle(Vector2[] cornersView, Vector2 viewPoint)
    {
        if (!float.IsFinite(viewPoint.X) || !float.IsFinite(viewPoint.Y))
            return false;
        if (!TryGetRotateCenter(cornersView, out var center))
            return false;
        return Vector2.DistanceSquared(center, viewPoint) <= RotateHandleRadius * RotateHandleRadius;
    }

    /// <summary>Maps a Scene delta to a SizeDelta change for the dragged handle. Inactive axes stay zero; locked pivot sides and singular worlds fail.</summary>
    /// <remarks>The dragged corner tracks the pointer: the Scene delta passes through the inverse full-world XY transform, then divides by the pivot-relative gain of each active axis. A stationary locked axis reads as zero instead of failing, so axis-aligned drags keep the free axis.</remarks>
    public static bool TrySceneDeltaToResize(
        Vector2 sceneDelta, Matrix4x4 world, Vector2 pivot, ResizeHandle handle, out Vector2 resizeDelta)
    {
        resizeDelta = Vector2.Zero;
        if (handle is ResizeHandle.None)
            return false;
        if (!float.IsFinite(sceneDelta.X) || !float.IsFinite(sceneDelta.Y))
            return false;
        if (!float.IsFinite(pivot.X) || !float.IsFinite(pivot.Y))
            return false;
        var a = world.M11;
        var b = world.M12;
        var c = world.M21;
        var d = world.M22;
        if (!float.IsFinite(a + b + c + d))
            return false;
        var determinant = (a * d) - (b * c);
        if (!float.IsFinite(determinant) || determinant == 0)
            return false;
        var localX = ((sceneDelta.X * d) - (sceneDelta.Y * c)) / determinant;
        var localY = ((sceneDelta.Y * a) - (sceneDelta.X * b)) / determinant;
        if (!float.IsFinite(localX) || !float.IsFinite(localY))
            return false;
        var (gainX, gainY, usesX, usesY) = ResizeGains(pivot, handle);
        // A stationary axis needs no gain; only real movement on a locked side fails.
        var x = usesX ? (localX == 0 ? 0 : localX / gainX) : 0;
        var y = usesY ? (localY == 0 ? 0 : localY / gainY) : 0;
        if (!float.IsFinite(x) || !float.IsFinite(y))
            return false;
        resizeDelta = new Vector2(x, y);
        return true;
    }

    /// <summary>Reports whether the handle adjusts each axis. Sides sitting exactly on the Pivot stay fixed.</summary>
    public static void ResizeAxes(Vector2 pivot, ResizeHandle handle, out bool adjustsX, out bool adjustsY)
    {
        var (gainX, gainY, usesX, usesY) = ResizeGains(pivot, handle);
        adjustsX = usesX && gainX != 0;
        adjustsY = usesY && gainY != 0;
    }

    private static (float GainX, float GainY, bool UsesX, bool UsesY) ResizeGains(Vector2 pivot, ResizeHandle handle) => handle switch
    {
        ResizeHandle.Left => (-pivot.X, 0, true, false),
        ResizeHandle.Top => (0, -pivot.Y, false, true),
        ResizeHandle.Right => (1 - pivot.X, 0, true, false),
        ResizeHandle.Bottom => (0, 1 - pivot.Y, false, true),
        ResizeHandle.TopLeft => (-pivot.X, -pivot.Y, true, true),
        ResizeHandle.TopRight => (1 - pivot.X, -pivot.Y, true, true),
        ResizeHandle.BottomLeft => (-pivot.X, 1 - pivot.Y, true, true),
        ResizeHandle.BottomRight => (1 - pivot.X, 1 - pivot.Y, true, true),
        _ => (0, 0, false, false),
    };

    /// <summary>Applies a resize change to the start SizeDelta on the handle axes. Resolved sizes clamp at zero; other members are untouched.</summary>
    public static bool TryApplyResize(
        Vector2 startSize, Vector2 anchorSpan, Vector2 resizeDelta, Vector2 pivot, ResizeHandle handle, out Vector2 nextSize)
    {
        nextSize = startSize;
        if (handle is ResizeHandle.None)
            return false;
        if (!float.IsFinite(startSize.X + startSize.Y) || !float.IsFinite(anchorSpan.X + anchorSpan.Y))
            return false;
        if (!float.IsFinite(resizeDelta.X) || !float.IsFinite(resizeDelta.Y))
            return false;
        if (!float.IsFinite(pivot.X) || !float.IsFinite(pivot.Y))
            return false;
        var (_, _, usesX, usesY) = ResizeGains(pivot, handle);
        if (!usesX && !usesY)
            return false;
        var next = startSize;
        if (usesX)
            next.X = Math.Max(startSize.X + resizeDelta.X, -anchorSpan.X);
        if (usesY)
            next.Y = Math.Max(startSize.Y + resizeDelta.Y, -anchorSpan.Y);
        if (!float.IsFinite(next.X) || !float.IsFinite(next.Y))
            return false;
        nextSize = next;
        return true;
    }

    /// <summary>Measures the pointer angle change around the drag-start pivot in Scene coordinates. Tiny radii fail.</summary>
    public static bool TryRotateAngle(Vector2 pivotScene, Vector2 fromScene, Vector2 toScene, out float radians)
    {
        radians = 0;
        if (!float.IsFinite(pivotScene.X + pivotScene.Y + fromScene.X + fromScene.Y + toScene.X + toScene.Y))
            return false;
        var from = fromScene - pivotScene;
        var to = toScene - pivotScene;
        if (from.LengthSquared() <= 1e-6f || to.LengthSquared() <= 1e-6f)
            return false;
        var delta = MathF.Atan2(to.Y, to.X) - MathF.Atan2(from.Y, from.X);
        if (!float.IsFinite(delta))
            return false;
        if (delta > MathF.PI)
            delta -= MathF.PI * 2;
        else if (delta < -MathF.PI)
            delta += MathF.PI * 2;
        radians = delta;
        return true;
    }

    /// <summary>Composes a Scene-plane rotation onto the start rotation. Existing tilt is preserved, never reset.</summary>
    public static bool TryApplyRotation(Quaternion start, float radians, out Quaternion next)
    {
        next = start;
        if (!float.IsFinite(radians))
            return false;
        if (!float.IsFinite(start.X + start.Y + start.Z + start.W) || start.LengthSquared() <= float.Epsilon)
            return false;
        // Parent-space Z rotation composes on the left (local * parent order), so X/Y tilt survives the drag.
        var composed = Quaternion.Concatenate(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, radians), start);
        var normalized = Quaternion.Normalize(composed);
        if (!float.IsFinite(normalized.X + normalized.Y + normalized.Z + normalized.W))
            return false;
        next = normalized;
        return true;
    }

    /// <summary>Computes the pan/zoom that fits the selection rectangle (Scene-coordinate corners) into the view with padding.</summary>
    public static bool TryComputeFit(
        Vector2 viewportSize, Vector2[] cornersScene, out Vector2 pan, out float zoom)
    {
        pan = Vector2.Zero;
        zoom = 1f;
        if (!IsValidViewport(viewportSize))
            return false;
        if (cornersScene.Length != 4)
            return false;
        var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
        var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
        foreach (var corner in cornersScene)
        {
            if (!float.IsFinite(corner.X) || !float.IsFinite(corner.Y))
                return false;
            min = Vector2.Min(min, corner);
            max = Vector2.Max(max, corner);
        }
        var bounds = max - min;
        if (!float.IsFinite(bounds.X + bounds.Y) || bounds.X <= 0 || bounds.Y <= 0)
            return false;
        var available = viewportSize - new Vector2(FitPad * 2, FitPad * 2);
        if (available.X <= 0 || available.Y <= 0)
            return false;
        var fitted = Math.Min(available.X / bounds.X, available.Y / bounds.Y);
        if (!float.IsFinite(fitted) || fitted <= 0)
            return false;
        zoom = Math.Clamp(fitted, MinZoom, MaxZoom);
        var centerScene = (min + max) / 2;
        var centerView = viewportSize / 2;
        pan = centerView - (centerScene * zoom);
        if (!float.IsFinite(pan.X + pan.Y))
            return false;
        return true;
    }

    /// <summary>Computes the Scene-coordinate corners from a layout. Used as input for the F view.</summary>
    public static bool TryGetSceneCorners(LayoutEntry entry, out Vector2[] cornersScene) =>
        TryGetSelectionFrame(entry, Vector2.Zero, 1, out cornersScene, out _);
}
