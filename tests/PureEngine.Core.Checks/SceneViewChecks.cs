using System.Numerics;
using PureEngine.Core;

internal static class SceneViewChecks
{
    public static void Run()
    {
        Roundtrip();
        ZoomAtCursor();
        GridDensity();
        OverlapOrder();
        ParentMove();
        GizmoHit();
        DegenerateSelection();
        Fit();
        Console.WriteLine("PASS: scene view coordinates, cursor zoom, overlap order, parent-aware move, gizmo hit, and fit.");
    }

    private static void Roundtrip()
    {
        foreach (var zoom in new[] { 0.25f, 0.5f, 1f, 2f, 8f })
        {
            var pan = new Vector2(37, -120);
            var scene = new Vector2(123.5f, -45.25f);
            var view = SceneViewMath.SceneToView(scene, pan, zoom);
            var back = SceneViewMath.ViewToScene(view, pan, zoom);
            Check(Vector2.Distance(back, scene) < 0.001f, $"Coordinate roundtrip failed at zoom {zoom}, got {back}.");
        }
        var matrix = SceneViewMath.ViewMatrix(new Vector2(10, 20), 2f);
        var transformed = Vector3.Transform(new Vector3(5, 7, 3), matrix);
        Check(transformed.X == 20 && transformed.Y == 34 && transformed.Z == 3,
            $"View matrix must scale XY and preserve Z, got {transformed}.");
    }

    private static void ZoomAtCursor()
    {
        var viewport = new Vector2(400, 200);
        var pan = Vector2.Zero;
        var zoom = 1f;
        var cursor = new Vector2(100, 50);
        var before = SceneViewMath.ViewToScene(cursor, pan, zoom);
        Check(SceneViewMath.TryZoomAt(cursor, viewport, pan, zoom, 2f, out var nextPan, out var nextZoom)
            && nextZoom == 2f, "Zoom-in must double within range.");
        var after = SceneViewMath.ViewToScene(cursor, nextPan, nextZoom);
        Check(Vector2.Distance(before, after) < 0.001f, $"Cursor-centered zoom moved the scene point from {before} to {after}.");
        Check(!SceneViewMath.TryZoomAt(cursor, viewport, pan, zoom, float.NaN, out _, out _), "Non-finite zoom factor must be rejected.");
        Check(!SceneViewMath.TryZoomAt(new Vector2(float.NaN, 0), viewport, pan, zoom, 2f, out _, out _), "Non-finite cursor must be rejected.");
        Check(SceneViewMath.TryZoomAt(cursor, viewport, pan, zoom, 100f, out _, out var clamped) && clamped == SceneViewMath.MaxZoom,
            "Zoom must clamp to a finite maximum.");
        Check(SceneViewMath.TryZoomAt(cursor, viewport, pan, zoom, 0.0001f, out _, out var floored) && floored == SceneViewMath.MinZoom,
            "Zoom must clamp to a finite minimum.");
    }

    private static void GridDensity()
    {
        foreach (var zoom in new[] { 0.25f, 0.5f, 1f, 2f, 4f, 8f })
        {
            var step = SceneViewMath.GridStep(zoom);
            var screen = step * zoom;
            Check(float.IsFinite(step) && step > 0 && screen >= SceneViewMath.GridMinScreen && screen < SceneViewMath.GridMaxScreen,
                $"Grid step {step} at zoom {zoom} must keep {screen}px on screen.");
        }
    }

    private static void OverlapOrder()
    {
        var scene = new Scene();
        var back = scene.AddEmpty();
        back.Rename("Back");
        back.Attach(new Transform { LocalPosition = new Vector3(10, 10, 0) });
        back.Attach(new UiElement { Pivot = Vector2.Zero, SizeDelta = new Vector2(100, 100) });
        back.Attach(new global::Image { Sprite = new Sprite(Guid.NewGuid()) });
        var front = scene.AddEmpty();
        front.Rename("Front");
        front.Attach(new Transform { LocalPosition = new Vector3(50, 50, 9) });
        front.Attach(new UiElement { Pivot = Vector2.Zero, SizeDelta = new Vector2(100, 100) });
        front.Attach(new global::Image { Sprite = new Sprite(Guid.NewGuid()) });
        var viewport = new Vector2(400, 200);
        var entries = SceneViewMath.EnumerateLayouts(scene, viewport);
        Check(entries.Count == 2, "Both UI objects must enumerate.");
        // Z must not change draw order: later siblings stay in front even with a smaller Z.
        var hit = SceneViewMath.HitTest(entries, viewport, Vector2.Zero, 1f, new Vector2(60, 60), IsImage);
        Check(ReferenceEquals(hit, front), "Overlap hit must follow sibling order, not Z.");
        var rotated = scene.AddEmpty();
        rotated.Rename("Rotated");
        rotated.Attach(new Transform
        {
            LocalPosition = new Vector3(200, 100, 0),
            LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4),
        });
        rotated.Attach(new UiElement { Pivot = new Vector2(0.5f, 0.5f), SizeDelta = new Vector2(100, 20) });
        rotated.Attach(new global::Image { Sprite = new Sprite(Guid.NewGuid()) });
        entries = SceneViewMath.EnumerateLayouts(scene, viewport);
        var rotatedEntry = entries.First(entry => ReferenceEquals(entry.Object, rotated));
        Check(SceneViewMath.TryGetSelectionFrame(rotatedEntry, Vector2.Zero, 1f, out var corners, out var pivot)
            && corners.Length == 4, "Rotated selection frame must produce four corners.");
        var center = (corners[0] + corners[2]) / 2;
        Check(Vector2.Distance(center, pivot) < 0.01f, "Pivot must stay at the rotated rectangle center.");
        var blank = SceneViewMath.HitTest(entries, viewport, Vector2.Zero, 1f, new Vector2(390, 190), IsImage);
        Check(blank is null, "Blank clicks must hit nothing.");
        var empty = scene.AddEmpty();
        empty.Rename("NoImage");
        empty.Attach(new Transform());
        empty.Attach(new UiElement());
        entries = SceneViewMath.EnumerateLayouts(scene, viewport);
        var emptyHit = SceneViewMath.HitTest(entries, viewport, Vector2.Zero, 1f, new Vector2(50, 50), IsImage);
        Check(!ReferenceEquals(emptyHit, empty), "Objects without a sprite must not be picked by image clicks.");
        var zero = scene.AddEmpty();
        zero.Rename("Zero");
        zero.Attach(new Transform());
        zero.Attach(new UiElement { SizeDelta = Vector2.Zero });
        zero.Attach(new global::Image { Sprite = new Sprite(Guid.NewGuid()) });
        entries = SceneViewMath.EnumerateLayouts(scene, viewport);
        var zeroHit = SceneViewMath.HitTest(entries, viewport, Vector2.Zero, 1f, Vector2.Zero, IsImage);
        Check(!ReferenceEquals(zeroHit, zero), "Zero-size objects must not be hittable.");

        static bool IsImage(SceneObject item) => item.GetComponent<global::Image>() is { Sprite: not null };
    }

    private static void ParentMove()
    {
        var scene = new Scene();
        var parent = scene.AddEmpty();
        parent.Rename("Parent");
        parent.Attach(new Transform
        {
            LocalPosition = new Vector3(100, 50, 0),
            LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2),
            LocalScale = new Vector3(2, 2, 1),
        });
        parent.Attach(new UiElement { Pivot = Vector2.Zero, SizeDelta = new Vector2(200, 200) });
        var child = scene.AddEmpty();
        child.Rename("Child");
        child.SetParent(parent);
        var start = new Vector3(10, 20, 5);
        child.Attach(new Transform { LocalPosition = start });
        child.Attach(new UiElement { Pivot = Vector2.Zero, SizeDelta = new Vector2(40, 30) });
        var viewport = new Vector2(400, 200);
        var entries = SceneViewMath.EnumerateLayouts(scene, viewport);
        var childEntry = entries.First(entry => ReferenceEquals(entry.Object, child));
        Check(SceneViewMath.TryGetParentAxes(childEntry.ParentWorld, out var xAxis, out var yAxis),
            "Parent axes must resolve under rotation and scale.");
        // Parent rotated +90° and scaled by 2: local X maps to scene (0, 2), local Y maps to scene (-2, 0).
        Check(Vector2.Distance(xAxis, new Vector2(0, 1)) < 0.001f, $"Parent X axis wrong: {xAxis}.");
        Check(Vector2.Distance(yAxis, new Vector2(-1, 0)) < 0.001f, $"Parent Y axis wrong: {yAxis}.");
        var sceneDelta = new Vector2(0, 20);
        Check(SceneViewMath.TrySceneDeltaToLocal(sceneDelta, childEntry.ParentWorld, out var localDelta),
            "Scene delta must convert through the parent inverse.");
        Check(Vector2.Distance(localDelta, new Vector2(10, 0)) < 0.001f, $"Parent-aware delta wrong: {localDelta}.");
        Check(SceneViewMath.TryApplyMove(start, localDelta, SceneViewMath.GizmoKind.X, out var movedX)
            && movedX == new Vector3(20, 20, 5), $"X-only move must keep Y/Z, got {movedX}.");
        var sceneDeltaY = new Vector2(-20, 0);
        Check(SceneViewMath.TrySceneDeltaToLocal(sceneDeltaY, childEntry.ParentWorld, out var localDeltaY)
            && Vector2.Distance(localDeltaY, new Vector2(0, 10)) < 0.001f,
            $"Parent-aware Y delta wrong: {localDeltaY}.");
        Check(SceneViewMath.TryApplyMove(start, localDeltaY, SceneViewMath.GizmoKind.Y, out var movedY)
            && movedY == new Vector3(10, 30, 5), $"Y-only move must keep X/Z, got {movedY}.");
        Check(SceneViewMath.TryApplyMove(start, localDelta + localDeltaY, SceneViewMath.GizmoKind.XY, out var moved)
            && moved == new Vector3(20, 30, 5), $"XY move must preserve Z, got {moved}.");
        var flatParent = Matrix4x4.CreateScale(0, 1, 1);
        Check(!SceneViewMath.TrySceneDeltaToLocal(new Vector2(1, 0), flatParent, out _),
            "Non-invertible parents must disable gizmo moves.");
        Check(!SceneViewMath.TryGetParentAxes(flatParent, out _, out _),
            "Degenerate parent axes must disable the gizmo.");
    }

    private static void GizmoHit()
    {
        var pivot = new Vector2(200, 100);
        var x = Vector2.UnitX;
        var y = Vector2.UnitY;
        Check(SceneViewMath.HitGizmo(pivot, x, y, pivot) == SceneViewMath.GizmoKind.XY, "Center must win at the pivot.");
        Check(SceneViewMath.HitGizmo(pivot, x, y, pivot + new Vector2(SceneViewMath.GizmoLength, 0)) == SceneViewMath.GizmoKind.X,
            "X head must hit on its axis.");
        Check(SceneViewMath.HitGizmo(pivot, x, y, pivot + new Vector2(0, SceneViewMath.GizmoLength)) == SceneViewMath.GizmoKind.Y,
            "Y head must hit on its axis.");
        Check(SceneViewMath.HitGizmo(pivot, x, y, pivot + new Vector2(24, 0)) == SceneViewMath.GizmoKind.X,
            "X shaft must hit within screen width.");
        Check(SceneViewMath.HitGizmo(pivot, x, y, pivot + new Vector2(0, 100)) == SceneViewMath.GizmoKind.None,
            "Far points must miss the constant-size gizmo.");
        Check(SceneViewMath.HitGizmo(pivot, x, y, pivot + new Vector2(SceneViewMath.GizmoLength + 5, 5)) == SceneViewMath.GizmoKind.X,
            "The visible outer half of the X head must be clickable.");
        Check(SceneViewMath.HitGizmo(pivot, x, y, pivot + new Vector2(5, SceneViewMath.GizmoLength + 5)) == SceneViewMath.GizmoKind.Y,
            "The visible outer half of the Y head must be clickable.");
    }

    private static void DegenerateSelection()
    {
        var scene = new Scene();
        var item = scene.AddEmpty();
        var transform = new Transform { LocalScale = new(0, 1, 1) };
        item.Attach(transform);
        item.Attach(new UiElement());
        var entry = SceneViewMath.EnumerateLayouts(scene, new(400, 200)).Single();
        Check(!SceneViewMath.TryGetSelectionFrame(entry, Vector2.Zero, 1, out _, out _),
            "A collapsed object must not expose a movable gizmo even when its parent is invertible.");
        Check(!SceneViewMath.TryGetSceneCorners(entry, out _), "F framing must ignore a collapsed transform.");
        transform.LocalScale = Vector3.One;
        transform.LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4);
        item.GetComponent<UiElement>()!.SizeDelta = new(0, 100);
        entry = SceneViewMath.EnumerateLayouts(scene, new(400, 200)).Single();
        Check(!SceneViewMath.TryGetSceneCorners(entry, out _), "A rotated zero-width rectangle must not become a valid framing box.");
        var parallel = Matrix4x4.Identity;
        parallel.M11 = parallel.M12 = parallel.M21 = parallel.M22 = 1;
        Check(!SceneViewMath.TryGetParentAxes(parallel, out _, out _), "Parallel projected axes must not enable a gizmo.");
    }

    private static void Fit()
    {
        var viewport = new Vector2(400, 200);
        var corners = new[] { new Vector2(10, 20), new Vector2(110, 20), new Vector2(110, 60), new Vector2(10, 60) };
        Check(SceneViewMath.TryComputeFit(viewport, corners, out var pan, out var zoom), "Fit must succeed for valid bounds.");
        foreach (var corner in corners)
        {
            var view = SceneViewMath.SceneToView(corner, pan, zoom);
            Check(view.X >= 0 && view.X <= viewport.X && view.Y >= 0 && view.Y <= viewport.Y,
                $"Fit must keep {corner} inside the viewport, got {view}.");
        }
        var centerScene = (corners[0] + corners[2]) / 2;
        var centerView = SceneViewMath.SceneToView(centerScene, pan, zoom);
        Check(Vector2.Distance(centerView, viewport / 2) < 0.01f, "Fit must center the selection.");
        Check(!SceneViewMath.TryComputeFit(viewport, [new Vector2(5, 5), new Vector2(5, 5), new Vector2(5, 5), new Vector2(5, 5)], out _, out _),
            "Zero-size bounds must be a safe no-op.");
        Check(!SceneViewMath.TryComputeFit(new Vector2(5, 5), corners, out _, out _), "Tiny viewports must be a safe no-op.");
        Check(!SceneViewMath.TryComputeFit(viewport, [Vector2.Zero, Vector2.Zero, Vector2.Zero], out _, out _),
            "Malformed corners must be a safe no-op.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
