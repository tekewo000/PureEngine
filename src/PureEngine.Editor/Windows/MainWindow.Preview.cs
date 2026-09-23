using PureEngine.Core;
using PureEngine.Rendering;

namespace PureEngine.Editor;

public partial class MainWindow
{
    private Rendering.Avalonia.VulkanViewport? _sceneViewport;

    /// <summary>Connects the Scene View to the scene being edited. Never calls Start/Update.</summary>
    private void ConnectPreviewViewport(Rendering.Avalonia.VulkanViewport viewport)
    {
        _sceneViewport = viewport;
        viewport.SceneBuilder = (draw, size) =>
        {
            try
            {
                DrawSceneView(draw, size);
            }
            catch (Exception error)
            {
                Log.Engine.Error($"Scene preview failed: {error.GetBaseException().Message}", error);
            }
        };
    }

    /// <summary>Draws the grid, images, selection frame, and gizmo with the same layout and view transform. Never changes the Anchor area via the view.</summary>
    private void DrawSceneView(DrawList draw, System.Numerics.Vector2 size)
    {
        if (_sceneMoveKind is not SceneViewMath.GizmoKind.None) ValidateSceneMove();
        var viewportSize = size;
        if (!SceneViewMath.IsValidViewport(viewportSize))
        {
            draw.Clear();
            return;
        }
        if (!SceneViewMath.IsValidView(_scenePan, _sceneZoom))
        {
            draw.Clear();
            return;
        }
        var view = SceneViewMath.ViewMatrix(_scenePan, _sceneZoom);
        draw.Clear();
        SceneViewOverlay.DrawGrid(draw, viewportSize, _scenePan, _sceneZoom);
        var diagnostics = EditSceneRenderer.Append(draw, _editScene.Current, _previewImages, viewportSize, view);
        _sceneDrawFailures.Clear();
        foreach (var diagnostic in diagnostics) _sceneDrawFailures.Add(diagnostic.ObjectId);
        if (GetSelectedSceneObject() is not SceneObject selected)
            return;
        if (!TrySceneFrame(selected, viewportSize, out var corners, out var pivot, out var xAxis, out var yAxis, out var gizmoValid))
            return;
        var clip = new System.Numerics.Vector4(0, 0, viewportSize.X, viewportSize.Y);
        SceneViewOverlay.DrawSelection(draw, corners, pivot, clip);
        if (gizmoValid && !IsPlaying)
            SceneViewOverlay.DrawGizmo(draw, pivot, xAxis, yAxis, clip);
    }
}
