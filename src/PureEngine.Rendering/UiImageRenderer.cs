using System.Numerics;
using PureEngine.Core;

namespace PureEngine.Rendering;

/// <summary>Single-object UI layout and Image drawing. The caller controls traversal, order and image ownership.</summary>
/// <remarks>The caller sorts multiple targets ascending by Order before drawing. Resolves layout ahead of time and never changes it by Order.</remarks>
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

    /// <summary>Draws one target with layout already resolved. Entry point for calling in Order after parent-child layout.</summary>
    /// <remarks>Does not recompute layout; uses the passed Size/WorldScene. Skips and returns without Sprite, zero size, or degenerate transforms.</remarks>
    public static void DrawEntry(
        DrawList draw, SceneObject item, Vector2 size, Matrix4x4 worldScene,
        IReadOnlyDictionary<Guid, byte[]> images, Vector4 clip, Matrix4x4? view = null)
    {
        ArgumentNullException.ThrowIfNull(draw);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(images);
        if (item.GetComponent<Image>() is not { Sprite: { } sprite } image) return;
        // Orthographic XY projection. Z is preserved by UiLayout, but does not change 2D submission order.
        // Composes the Editor view transform after layout without changing the Anchor parent area.
        var viewMatrix = view ?? Matrix4x4.Identity;
        var world = worldScene * viewMatrix;
        if (world.M14 != 0 || world.M24 != 0 || world.M34 != 0 || world.M44 != 1)
            throw new NotSupportedException("UI image drawing requires an affine transform.");
        var matrix = new Matrix3x2(world.M11, world.M12, world.M21, world.M22, world.M41, world.M42);
        if (size.X == 0 || size.Y == 0 || matrix.GetDeterminant() == 0) return;
        if (!images.TryGetValue(sprite.ImageId, out var encodedImage))
            throw new InvalidOperationException($"{item.Name}: source image {sprite.ImageId:D} is missing.");
        var color = image.Color;
        draw.Image(sprite, encodedImage, size, matrix, new Vector4(color.R, color.G, color.B, color.A), clip);
    }
}
