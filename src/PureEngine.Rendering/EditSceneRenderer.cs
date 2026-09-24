using System.Numerics;
using PureEngine.Core;

namespace PureEngine.Rendering;

/// <summary>Pass that draws the scene being edited into the Scene View. Resolves parent-child layout first, then sorts ascending by Order. Never calls Start/Update.</summary>
/// <remarks>Uses the same <see cref="PureEngine.Core.SceneViewMath.SortForRender"/> for draw order and hit testing. Order is never inherited from the parent.</remarks>
public static class EditSceneRenderer
{
    public sealed record Diagnostic(Guid ObjectId, string ObjectName, string Message);

    /// <summary>Refills the DrawList with the edit scene and returns diagnostics for targets that could not be drawn. Never throws.</summary>
    public static IReadOnlyList<Diagnostic> Build(
        DrawList draw, Scene scene, IReadOnlyDictionary<Guid, byte[]> images, Vector2 viewportSize) =>
        Build(draw, scene, images, viewportSize, Matrix4x4.Identity);

    /// <summary>Draws with a view transform. Computes layout in scene coordinates, then composes the view last. Never changes the Anchor area.</summary>
    /// <remarks>Resolves parent-child layout first, then sorts draw targets ascending by Order. Ties keep parent-then-child and sibling order.</remarks>
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

    /// <summary>Appends only images without clearing. For the edit path that draws the grid behind. Never throws.</summary>
    /// <remarks>Layout-first and Order sorting match Build. Append order follows the caller DrawList state.</remarks>
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
            try
            {
                UiTextRenderer.DrawEntry(draw, entry.Object, entry.Size, entry.WorldScene, clip, view);
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
                // When the layout itself is broken, passes the parent area through to the child.
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
}
