using System.Numerics;
using PureEngine.Core;

namespace PureEngine.Rendering;

/// <summary>Single-object UI text layout and drawing. The caller controls traversal, order and image ownership.</summary>
/// <remarks>Draws from the UiElement top-left, wrapping at its width without height clipping. Image draws first on the same object.</remarks>
public static class UiTextRenderer
{
    public static (Vector2 Size, Matrix4x4 World) Draw(
        DrawList draw, SceneObject item, Vector2 parentSize, Matrix4x4 parentWorld,
        Vector4 clip, Matrix4x4? view = null, LocalizationService? localization = null)
    {
        ArgumentNullException.ThrowIfNull(draw);
        ArgumentNullException.ThrowIfNull(item);
        var transform = item.GetComponent<Transform>() ?? throw new InvalidOperationException($"{item.Name}: Transform is required.");
        var element = item.GetComponent<UiElement>() ?? throw new InvalidOperationException($"{item.Name}: UiElement is required.");
        var layout = UiLayout.Calculate(parentSize, parentWorld, transform, element);
        DrawEntry(draw, item, layout.Size, layout.World, clip, view, localization);
        return layout;
    }

    /// <summary>Draws one target with layout already resolved. Entry point for calling in Order after parent-child layout.</summary>
    /// <remarks>Does not recompute layout; uses the passed Size/WorldScene. Skips and returns without Text, empty content, zero size, or degenerate transforms.</remarks>
    public static void DrawEntry(
        DrawList draw, SceneObject item, Vector2 size, Matrix4x4 worldScene,
        Vector4 clip, Matrix4x4? view = null, LocalizationService? localization = null)
    {
        ArgumentNullException.ThrowIfNull(draw);
        ArgumentNullException.ThrowIfNull(item);
        if (item.GetComponent<Text>() is not { } text) return;
        var content = LocalizationService.ResolveText(text.LocalizedEntry, text.Content, localization?.CurrentLanguage);
        if (string.IsNullOrEmpty(content)) return;
        // Orthographic XY projection. Z is preserved by UiLayout, but does not change 2D submission order.
        // Composes the Editor view transform after layout without changing the Anchor parent area.
        var viewMatrix = view ?? Matrix4x4.Identity;
        var world = worldScene * viewMatrix;
        if (world.M14 != 0 || world.M24 != 0 || world.M34 != 0 || world.M44 != 1)
            throw new NotSupportedException("UI text drawing requires an affine transform.");
        var matrix = new Matrix3x2(world.M11, world.M12, world.M21, world.M22, world.M41, world.M42);
        if (size.X == 0 || size.Y == 0 || matrix.GetDeterminant() == 0) return;
        var color = text.Color;
        draw.Text(content, text.FontSize, size.X, text.LineSpacing, matrix, new Vector4(color.R, color.G, color.B, color.A), clip);
    }
}