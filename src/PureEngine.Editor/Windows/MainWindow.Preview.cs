using PureEngine.Core;
using PureEngine.Rendering;

namespace PureEngine.Editor;

public partial class MainWindow
{
    private Rendering.Avalonia.VulkanViewport? _sceneViewport;

    /// <summary>Scene Viewを編集用Sceneへ接続する。Start／Updateは呼ばない。</summary>
    private void ConnectPreviewViewport(Rendering.Avalonia.VulkanViewport viewport)
    {
        _sceneViewport = viewport;
        viewport.SceneBuilder = (draw, size) =>
        {
            try
            {
                EditSceneRenderer.Build(draw, _editScene.Current, _previewImages, size);
            }
            catch (Exception error)
            {
                Log.Engine.Error($"Scene preview failed: {error.GetBaseException().Message}", error);
            }
        };
    }
}
