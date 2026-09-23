using System.Numerics;

namespace PureEngine.Core;

/// <summary>V4前半のScene View編集操作に使う座標・配置の共通計算。データはXYZを維持し、GizmoはXYだけを扱う。</summary>
/// <remarks>描画・選択・Gizmoで同じUiLayout結果とビュー変換を使う。Anchor用の親領域はパン／ズームで変えない。</remarks>
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

    public static bool IsValidViewport(Vector2 viewportSize) =>
        float.IsFinite(viewportSize.X) && float.IsFinite(viewportSize.Y)
        && viewportSize.X >= 10 && viewportSize.Y >= 10;

    public static bool IsValidView(Vector2 pan, float zoom) =>
        float.IsFinite(pan.X) && float.IsFinite(pan.Y)
        && float.IsFinite(zoom) && zoom >= MinZoom && zoom <= MaxZoom;

    /// <summary>Scene座標を論理表示座標へ変換する。呼び出し側で有効なビューを保証する。</summary>
    public static Vector2 SceneToView(Vector2 scene, Vector2 pan, float zoom) =>
        scene * zoom + pan;

    /// <summary>論理表示座標をScene座標へ変換する。呼び出し側で有効なビューを保証する。</summary>
    public static Vector2 ViewToScene(Vector2 view, Vector2 pan, float zoom) =>
        (view - pan) / zoom;

    public static Matrix4x4 ViewMatrix(Vector2 pan, float zoom) =>
        new(zoom, 0, 0, 0,
            0, zoom, 0, 0,
            0, 0, 1, 0,
            pan.X, pan.Y, 0, 1);

    /// <summary>ポインター直下のScene座標を保持してズームする。無効値はno-opでfalseを返す。</summary>
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

    /// <summary>倍率に応じたグリッド間隔（Scene単位）を求める。画面上では48〜96pxを保つ。</summary>
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

    /// <summary>親→子・兄弟順の深さ優先で有効なUI配置を列挙する。描画順ではなく配置計算順。壊れた配置の子には親領域を受け渡す。</summary>
    /// <remarks>描画・ヒット判定の前後関係は <see cref="SortForRender"/> でOrder昇順へ並べ替える。配置値は並べ替えで変えない。Transformのみのグループノードは矩形を持たないが、その変換は子へ受け渡す。</remarks>
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
            // Transformのみのグループノードは矩形を持たないが、その変換は子へ受け渡す。サイズは継承しない。
            world = bareWorld;
        }
        foreach (var child in item.Children)
            AppendRecursive(child, size, world, entries);
    }

    /// <summary>Transformのみのグループノードを子の配置へ受け渡すワールドへ変換する。UiElementの矩形は作らない。</summary>
    /// <remarks>行ベクトル順（local * parent）でUiLayoutと同じ合成順にする。非有限の行列はfalse。</remarks>
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

    /// <summary>UiElementの有無を問わず、対象の親領域・親ワールド・自身ワールドを求める。Gizmo表示とドラッグ検証用。</summary>
    /// <remarks>配置計算はEnumerateLayoutsと同じ走査規則（UiLayout優先・Transformのみは素通し）を使う。対象不在・Transformなし・非有限はfalse。</remarks>
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

    /// <summary>描画対象のImageのOrderを返す。Imageなしは0。親からは継承せず各対象の値を使う。</summary>
    public static int GetRenderOrder(SceneObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        // 現在の描画経路はImageのみ。他の派生型のアタッチ順にImageの描画順を左右させない。
        return item.GetComponent<global::Image>()?.Order ?? 0;
    }

    /// <summary>配置計算済みの列をOrder昇順へ安定並べ替えする。描画とヒット判定で同じ処理を使う。</summary>
    /// <remarks>同値は元の親→子・兄弟順を維持する。配置値は変えず順序だけを変える。</remarks>
    public static IReadOnlyList<LayoutEntry> SortForRender(IReadOnlyList<LayoutEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return [.. entries.OrderBy(entry => GetRenderOrder(entry.Object))];
    }

    private static Matrix3x2 ViewPlane(Vector2 pan, float zoom) =>
        new(zoom, 0, 0, zoom, pan.X, pan.Y);

    /// <summary>描画順の逆順（Order降順、同値は後方が手前）で矩形ヒット判定する。回転・拡縮・Pivotを反映し、透明ピクセル判定はしない。</summary>
    /// <remarks>描画と同じ <see cref="SortForRender"/> を使い、手前から判定する。</remarks>
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

    /// <summary>選択枠の四隅とPivotを表示座標で求める。UiLayoutと同じ行列から算出する。</summary>
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

    /// <summary>親のUI配置からローカルXY軸の向き（Scene単位の単位ベクトル）を求める。自身の回転は含めない。</summary>
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

    /// <summary>Gizmoのヒット判定。見かけと判定幅は画面の論理ピクセル基準で保つ。中央→X→Yの順に判定する。</summary>
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
        var headCenter = pivot + (dir * GizmoLength);
        var halfHead = GizmoHeadSize / 2;
        if (Math.Abs(point.X - headCenter.X) <= halfHead
            && Math.Abs(point.Y - headCenter.Y) <= halfHead)
            return true;
        if (along < GizmoShaftStart || along > GizmoLength)
            return false;
        var perpendicular = offset - (dir * along);
        return perpendicular.Length() <= GizmoShaftHalfWidth;
    }

    /// <summary>親XY変換の逆行列でScene差分をローカル差分へ戻す。Zは扱わない。</summary>
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

    /// <summary>開始値からの差分でLocalPositionのX・Yだけを更新し、Zを保持する。</summary>
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

    /// <summary>選択矩形（Scene座標の四隅）を余白付きで表示領域へ収めるパン／ズームを求める。</summary>
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

    /// <summary>配置からScene座標の四隅を求める。F表示の入力に使う。</summary>
    public static bool TryGetSceneCorners(LayoutEntry entry, out Vector2[] cornersScene) =>
        TryGetSelectionFrame(entry, Vector2.Zero, 1, out cornersScene, out _);
}
