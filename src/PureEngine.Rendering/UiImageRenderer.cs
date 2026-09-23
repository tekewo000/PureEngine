using System.Numerics;
using PureEngine.Core;

namespace PureEngine.Rendering;

/// <summary>Single-object UI layout and Image drawing. The caller controls traversal, order and image ownership.</summary>
/// <remarks>複数対象の描画順は呼び出し側がOrder昇順へ並べ替える。配置は事前に求め、Orderでは変えない。</remarks>
public static class UiImageRenderer
{
    public static (Vector2 Size, Matrix4x4 World) Draw(
        DrawList draw, SceneObject item, Vector2 parentSize, Matrix4x4 parentWorld,
        IReadOnlyDictionary<Guid, byte[]> images, Vector4 clip, Matrix4x4? view = null)
    {
        ArgumentNullException.ThrowIfNull(draw);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(images);
        var transform = item.GetComponent<Transform>() ?? throw new InvalidOperationException($"{item.Name}: Transform is required.");
        var element = item.GetComponent<UiElement>() ?? throw new InvalidOperationException($"{item.Name}: UiElement is required.");
        var layout = UiLayout.Calculate(parentSize, parentWorld, transform, element);
        DrawEntry(draw, item, layout.Size, layout.World, images, clip, view);
        return layout;
    }

    /// <summary>配置計算済みの1対象を描く。親子配置を済ませてOrder順に呼ぶための入口。</summary>
    /// <remarks>配置の再計算はせず、渡されたSize／WorldSceneを使う。Spriteなし・0サイズ・退化変換は描かずに戻る。</remarks>
    public static void DrawEntry(
        DrawList draw, SceneObject item, Vector2 size, Matrix4x4 worldScene,
        IReadOnlyDictionary<Guid, byte[]> images, Vector4 clip, Matrix4x4? view = null)
    {
        ArgumentNullException.ThrowIfNull(draw);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(images);
        if (item.GetComponent<Image>() is not { Sprite: { } sprite } image) return;
        // Orthographic XY projection. Z is preserved by UiLayout, but does not change 2D submission order.
        // Editorのビュー変換は配置の後に合成し、Anchor用の親領域は変えない。
        var viewMatrix = view ?? Matrix4x4.Identity;
        var world = worldScene * viewMatrix;
        if (world.M14 != 0 || world.M24 != 0 || world.M34 != 0 || world.M44 != 1)
            throw new NotSupportedException("UI image drawing requires an affine transform.");
        var matrix = new Matrix3x2(world.M11, world.M12, world.M21, world.M22, world.M41, world.M42);
        if (size.X == 0 || size.Y == 0 || matrix.GetDeterminant() == 0) return;
        if (!images.TryGetValue(sprite.ImageId, out var encodedImage))
            throw new InvalidOperationException($"{item.Name}: source image {sprite.ImageId:D} is missing.");
        draw.Image(sprite, encodedImage, size, matrix, image.Color, clip);
    }
}
