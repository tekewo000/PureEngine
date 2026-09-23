using System.Numerics;
using PureEngine.Core;

namespace PureEngine.Rendering;

/// <summary>編集中SceneをScene Viewへ描く走査。親子配置を済ませてからOrder昇順へ並べ替え、Start／Updateは呼ばない。</summary>
/// <remarks>描画順とヒット判定で同じ <see cref="PureEngine.Core.SceneViewMath.SortForRender"/> を使う。Orderは親から継承しない。</remarks>
public static class EditSceneRenderer
{
    public sealed record Diagnostic(Guid ObjectId, string ObjectName, string Message);

    /// <summary>DrawListを編集Sceneで埋め直し、描けなかった対象の診断を返す。例外は投げない。</summary>
    public static IReadOnlyList<Diagnostic> Build(
        DrawList draw, Scene scene, IReadOnlyDictionary<Guid, byte[]> images, Vector2 viewportSize) =>
        Build(draw, scene, images, viewportSize, Matrix4x4.Identity);

    /// <summary>ビュー変換付きで描く。配置はScene座標で計算し、最後にビューを合成する。Anchor領域は変えない。</summary>
    /// <remarks>親子の配置計算を済ませてから描画対象をOrder昇順へ並べ替える。同値は親→子・兄弟順を維持する。</remarks>
    public static IReadOnlyList<Diagnostic> Build(
        DrawList draw, Scene scene, IReadOnlyDictionary<Guid, byte[]> images, Vector2 viewportSize, Matrix4x4 view)
    {
        ArgumentNullException.ThrowIfNull(draw);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(images);
        List<Diagnostic> diagnostics = [];
        draw.Clear();
        if (!IsDrawableViewport(viewportSize) || !IsAffine(view))
            return diagnostics;
        DrawOrdered(draw, scene, images, viewportSize, view, diagnostics);
        return diagnostics;
    }

    /// <summary>Clearせずに画像だけを追記する。グリッドを背後に描く編集パス用。例外は投げない。</summary>
    /// <remarks>配置先行・Order整列はBuildと同じ。追記順は呼び出し側のDrawList状態に続く。</remarks>
    public static IReadOnlyList<Diagnostic> Append(
        DrawList draw, Scene scene, IReadOnlyDictionary<Guid, byte[]> images, Vector2 viewportSize, Matrix4x4 view)
    {
        ArgumentNullException.ThrowIfNull(draw);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(images);
        List<Diagnostic> diagnostics = [];
        if (!IsDrawableViewport(viewportSize) || !IsAffine(view))
            return diagnostics;
        DrawOrdered(draw, scene, images, viewportSize, view, diagnostics);
        return diagnostics;
    }

    private static bool IsDrawableViewport(Vector2 viewportSize) =>
        float.IsFinite(viewportSize.X) && float.IsFinite(viewportSize.Y)
        && viewportSize.X >= 10 && viewportSize.Y >= 10;

    private static bool IsAffine(Matrix4x4 view) =>
        float.IsFinite(view.M11 + view.M12 + view.M13 + view.M14
            + view.M21 + view.M22 + view.M23 + view.M24
            + view.M31 + view.M32 + view.M33 + view.M34
            + view.M41 + view.M42 + view.M43 + view.M44)
        && view.M14 == 0 && view.M24 == 0 && view.M34 == 0 && view.M44 == 1;

    private static void DrawOrdered(
        DrawList draw, Scene scene, IReadOnlyDictionary<Guid, byte[]> images, Vector2 viewportSize,
        Matrix4x4 view, List<Diagnostic> diagnostics)
    {
        var clip = new Vector4(0, 0, viewportSize.X, viewportSize.Y);
        List<SceneViewMath.LayoutEntry> collected = [];
        foreach (var root in scene.RootObjects)
            CollectRecursive(root, viewportSize, Matrix4x4.Identity, collected, diagnostics);
        foreach (var entry in SceneViewMath.SortForRender(collected))
        {
            try
            {
                UiImageRenderer.DrawEntry(draw, entry.Object, entry.Size, entry.WorldScene, images, clip, view);
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or NotSupportedException)
            {
                diagnostics.Add(new Diagnostic(entry.Object.Id, entry.Object.Name, error.GetBaseException().Message));
            }
        }
    }

    private static void CollectRecursive(
        SceneObject item, Vector2 parentSize, Matrix4x4 parentWorld,
        List<SceneViewMath.LayoutEntry> collected, List<Diagnostic> diagnostics)
    {
        var size = parentSize;
        var world = parentWorld;
        try
        {
            var transform = item.GetComponent<Transform>()
                ?? throw new InvalidOperationException($"{item.Name}: Transform is required.");
            var element = item.GetComponent<UiElement>()
                ?? throw new InvalidOperationException($"{item.Name}: UiElement is required.");
            (size, world) = UiLayout.Calculate(parentSize, parentWorld, transform, element);
            collected.Add(new SceneViewMath.LayoutEntry(item, size, world, parentSize, parentWorld));
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            diagnostics.Add(new Diagnostic(item.Id, item.Name, error.GetBaseException().Message));
            // 配置自体が壊れている場合は親の領域を子へ受け渡す。
        }
        foreach (var child in item.Children)
            CollectRecursive(child, size, world, collected, diagnostics);
    }
}
