using System.Numerics;
using PureEngine.Core;

namespace PureEngine.Rendering;

/// <summary>編集中SceneをScene Viewへ描く走査。呼び出し側が走査順・画像所有・クリップを担い、Start／Updateは呼ばない。</summary>
public static class EditSceneRenderer
{
    public sealed record Diagnostic(Guid ObjectId, string ObjectName, string Message);

    /// <summary>DrawListを編集Sceneで埋め直し、描けなかった対象の診断を返す。例外は投げない。</summary>
    public static IReadOnlyList<Diagnostic> Build(
        DrawList draw, Scene scene, IReadOnlyDictionary<Guid, byte[]> images, Vector2 viewportSize)
    {
        ArgumentNullException.ThrowIfNull(draw);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(images);
        List<Diagnostic> diagnostics = [];
        draw.Clear();
        if (!float.IsFinite(viewportSize.X) || !float.IsFinite(viewportSize.Y)
            || viewportSize.X < 10 || viewportSize.Y < 10)
            return diagnostics;
        var clip = new Vector4(0, 0, viewportSize.X, viewportSize.Y);
        foreach (var root in scene.RootObjects)
            DrawRecursive(draw, root, viewportSize, Matrix4x4.Identity, images, clip, diagnostics);
        return diagnostics;
    }

    private static void DrawRecursive(
        DrawList draw, SceneObject item, Vector2 parentSize, Matrix4x4 parentWorld,
        IReadOnlyDictionary<Guid, byte[]> images, Vector4 clip, List<Diagnostic> diagnostics)
    {
        var size = parentSize;
        var world = parentWorld;
        try
        {
            var (resolvedSize, resolvedWorld) = UiImageRenderer.Draw(draw, item, parentSize, parentWorld, images, clip);
            size = resolvedSize;
            world = resolvedWorld;
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            diagnostics.Add(new Diagnostic(item.Id, item.Name, error.GetBaseException().Message));
            try
            {
                var transform = item.GetComponent<Transform>();
                var element = item.GetComponent<UiElement>();
                if (transform is not null && element is not null)
                    (size, world) = UiLayout.Calculate(parentSize, parentWorld, transform, element);
            }
            catch (Exception)
            {
                // 配置自体が壊れている場合は親の領域を子へ受け渡す。
            }
        }
        foreach (var child in item.Children)
            DrawRecursive(draw, child, size, world, images, clip, diagnostics);
    }
}
