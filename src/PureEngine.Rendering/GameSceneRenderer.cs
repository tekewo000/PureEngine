using System.Numerics;
using PureEngine.Core;

namespace PureEngine.Rendering;

/// <summary>Pass that draws the runtime scene into Game while playing. Uses the same layout and Order path as editing. Never calls Start/Update.</summary>
/// <remarks>Shows button states by reading the effective color and overdrawing instead of rewriting the saved Image.Color.</remarks>
public static class GameSceneRenderer
{
    public sealed record Diagnostic(Guid ObjectId, string ObjectName, string Message);

    public sealed record ButtonVisual(UiButtonVisualState State, bool Focused);

    /// <summary>Refills the DrawList with the runtime scene and returns diagnostics for targets that could not be drawn. Never throws.</summary>
    public static IReadOnlyList<Diagnostic> Build(
        DrawList draw, Scene scene, IReadOnlyDictionary<Guid, byte[]> images, Vector2 viewportSize,
        IReadOnlyDictionary<Guid, ButtonVisual>? states = null)
    {
        ArgumentNullException.ThrowIfNull(draw);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(images);
        List<Diagnostic> diagnostics = [];
        draw.Clear();
        if (!IsDrawableViewport(viewportSize))
            return diagnostics;
        DrawOrdered(draw, scene, images, viewportSize, states, diagnostics);
        return diagnostics;
    }

    private static bool IsDrawableViewport(Vector2 viewportSize) =>
        float.IsFinite(viewportSize.X) && float.IsFinite(viewportSize.Y)
        && viewportSize.X >= 10 && viewportSize.Y >= 10;

    private static void DrawOrdered(
        DrawList draw, Scene scene, IReadOnlyDictionary<Guid, byte[]> images, Vector2 viewportSize,
        IReadOnlyDictionary<Guid, ButtonVisual>? states, List<Diagnostic> diagnostics)
    {
        var clip = new Vector4(0, 0, viewportSize.X, viewportSize.Y);
        List<SceneViewMath.LayoutEntry> collected = [];
        foreach (var root in scene.RootObjects)
            CollectRecursive(root, viewportSize, Matrix4x4.Identity, collected, diagnostics);
        foreach (var entry in SceneViewMath.SortForRender(collected))
        {
            var hasImage = entry.Object.GetComponent<Image>() is { Sprite: not null };
            try
            {
                UiImageRenderer.DrawEntry(draw, entry.Object, entry.Size, entry.WorldScene, images, clip, null);
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or NotSupportedException)
            {
                diagnostics.Add(new Diagnostic(entry.Object.Id, entry.Object.Name, error.GetBaseException().Message));
                continue;
            }
            DrawButtonOverlay(draw, entry, clip, states, hasImage);
        }
    }

    private static void CollectRecursive(
        SceneObject item, Vector2 parentSize, Matrix4x4 parentWorld,
        List<SceneViewMath.LayoutEntry> collected, List<Diagnostic> diagnostics)
    {
        var size = parentSize;
        var world = parentWorld;
        var transform = item.GetComponent<Transform>();
        var element = item.GetComponent<UiElement>();
        if (transform is not null && element is not null)
        {
            try
            {
                (size, world) = UiLayout.Calculate(parentSize, parentWorld, transform, element);
                collected.Add(new SceneViewMath.LayoutEntry(item, size, world, parentSize, parentWorld));
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                diagnostics.Add(new Diagnostic(item.Id, item.Name, error.GetBaseException().Message));
            }
        }
        else if (transform is not null)
        {
            // Transform-only group nodes are not draw targets, but pass their transform to child layout.
            if (!SceneViewMath.TryPropagateBareTransform(parentWorld, transform, out var bareWorld))
                diagnostics.Add(new Diagnostic(item.Id, item.Name, $"{item.Name}: Transform produced a non-finite matrix."));
            else
                world = bareWorld;
        }
        else
        {
            diagnostics.Add(new Diagnostic(item.Id, item.Name, $"{item.Name}: Transform is required."));
        }
        foreach (var child in item.Children)
            CollectRecursive(child, size, world, collected, diagnostics);
    }

    private static void DrawButtonOverlay(
        DrawList draw, SceneViewMath.LayoutEntry entry, Vector4 clip,
        IReadOnlyDictionary<Guid, ButtonVisual>? states, bool hasImage)
    {
        if (entry.Object.GetComponent<Button>() is not { } button)
            return;
        var visual = states is not null && states.TryGetValue(entry.Object.Id, out var specified)
            ? specified
            : new ButtonVisual(button.Interactable ? UiButtonVisualState.Normal : UiButtonVisualState.Disabled, false);
        if (visual.State == UiButtonVisualState.Normal && !visual.Focused)
            return;
        if (entry.Size.X <= 0 || entry.Size.Y <= 0)
            return;
        var world = entry.WorldScene;
        if (world.M14 != 0 || world.M24 != 0 || world.M34 != 0 || world.M44 != 1)
            return;
        var plane = new Matrix3x2(world.M11, world.M12, world.M21, world.M22, world.M41, world.M42);
        if (plane.GetDeterminant() == 0)
            return;
        if (hasImage)
        {
            var fill = visual.State switch
            {
                UiButtonVisualState.Hover => new Vector4(1f, 1f, 1f, 0.15f),
                UiButtonVisualState.Pressed => new Vector4(0f, 0f, 0f, 0.3f),
                UiButtonVisualState.Disabled => new Vector4(0.5f, 0.5f, 0.5f, 0.5f),
                _ => (Vector4?)null,
            };
            if (fill is { } overlay)
                draw.Rectangle(entry.Size, plane, overlay, clip);
        }
        var outline = visual.State == UiButtonVisualState.Disabled || !visual.Focused
            ? (Vector4?)null
            : UiButtonVisuals.FocusOutline;
        if (outline is { } color)
            DrawOutline(draw, entry.Size, plane, color, clip);
    }

    private static void DrawOutline(DrawList draw, Vector2 size, Matrix3x2 plane, Vector4 color, Vector4 clip)
    {
        const float thickness = 2f;
        if (size.X <= thickness * 2 || size.Y <= thickness * 2)
        {
            draw.Rectangle(size, plane, new Vector4(color.X, color.Y, color.Z, color.W * 0.35f), clip);
            return;
        }
        draw.Rectangle(new Vector2(size.X, thickness),
            Matrix3x2.CreateTranslation(0, 0) * plane, color, clip);
        draw.Rectangle(new Vector2(size.X, thickness),
            Matrix3x2.CreateTranslation(0, size.Y - thickness) * plane, color, clip);
        draw.Rectangle(new Vector2(thickness, size.Y),
            Matrix3x2.CreateTranslation(0, 0) * plane, color, clip);
        draw.Rectangle(new Vector2(thickness, size.Y),
            Matrix3x2.CreateTranslation(size.X - thickness, 0) * plane, color, clip);
    }
}
