using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.VisualTree;
using PureEngine.Editor;
using PureEngine.Rendering;
using PureEngine.Rendering.Avalonia;
using PureEngine.Core;
using System.Numerics;
using SkiaSharp;

internal sealed class VulkanCheckApp : App
{

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow { WindowState = WindowState.Normal, Width = 1280, Height = 800 };
            desktop.MainWindow = window;
            window.Opened += async (_, _) =>
            {
                try
                {
                    var grid = window.FindControl<Grid>("SceneViewport")!;
                    var viewport = grid.Children.OfType<VulkanViewport>().Single();
                    var tabs = window.FindControl<TabControl>("ViewportTabs") ??
                        Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(grid).OfType<TabControl>().First();
                    var sceneTab = tabs.Items.OfType<TabItem>().First();
                    tabs.SelectedItem = sceneTab;
                    await Frames(viewport);
                    Console.WriteLine($"GPU: {viewport.DeviceName}; scale={window.RenderScaling}; pixels={viewport.ToPixel(new Point(100, 100))}");
                    // A normal Avalonia overlay must compose above the GPU image and remain input-transparent.
                    grid.Children.Add(new Border { Width = 180, Height = 110, Margin = new Thickness(55, 70, 0, 0),
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                        BorderBrush = Brushes.Lime, BorderThickness = new Thickness(2), IsHitTestVisible = false });
                    for (var i = 0; i < 20; i++)
                    {
                        viewport.Width = 640 + i * 7;
                        viewport.Height = 360 + i % 3 * 20;
                        await Frames(viewport);
                    }
                    viewport.Width = 0;
                    await Task.Delay(100);
                    viewport.Width = double.NaN;
                    viewport.Height = double.NaN;
                    await Frames(viewport);
                    window.WindowState = WindowState.Minimized;
                    await Task.Delay(200);
                    window.WindowState = WindowState.Normal;
                    await Frames(viewport);
                    for (var i = 0; i < 5; i++)
                    {
                        tabs.SelectedIndex = 1;
                        await Task.Delay(100);
                        tabs.SelectedItem = sceneTab;
                        await Frames(viewport);
                    }
                    var inspector = window.FindControl<Control>("InspectorPane")!;
                    var hit = window.InputHitTest(inspector.TranslatePoint(new Point(20, 20), window)!.Value);
                    if (hit is not Avalonia.Visual hitVisual || (hitVisual != inspector && !Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(hitVisual).Contains(inspector)))
                        throw new Exception("Viewport interferes with Inspector hit testing.");
                    var handles = new List<int>();
                    for (var i = 0; i < 20; i++)
                    {
                        grid.Children.Remove(viewport);
                        await viewport.ShutdownAsync();
                        if (VulkanRenderer.ActiveInstances != 0) throw new Exception("Renderer remains after detach.");
                        viewport = new VulkanViewport();
                        grid.Children.Insert(0, viewport);
                        await Frames(viewport);
                        using var process = Process.GetCurrentProcess();
                        handles.Add(process.HandleCount);
                    }
                    Console.WriteLine($"20 recreate cycles: handles={string.Join(',', handles)}; active={VulkanRenderer.ActiveInstances}");
                    if (handles[^1] > handles[5] + 10) throw new Exception("Native handle count grows across recreate cycles.");
                    await CheckAtlasRefresh(viewport);
                    if (Environment.GetEnvironmentVariable("PUREENGINE_VISUAL_CHECK") == "1")
                    {
                        Console.WriteLine("Visual inspection ready; close the window to finish.");
                        return;
                    }
                    await viewport.ShutdownAsync();
                    if (VulkanRenderer.ActiveInstances != 0) throw new Exception("Renderer remains after shutdown.");
                    Console.WriteLine("Vulkan Editor GPU checks passed.");
                    window.Close();
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine(error);
                    Environment.ExitCode = 1;
                    window.Close();
                }
            };
        }
    }

    private static async Task Frames(VulkanViewport viewport)
    {
        var first = viewport.FrameCount;
        var watch = Stopwatch.StartNew();
        while (viewport.FrameCount < first + 3)
        {
            if (viewport.Failure is { } error) throw new InvalidOperationException(error);
            if (watch.Elapsed.TotalSeconds > 15) throw new TimeoutException("GPU frames did not complete.");
            await Task.Delay(20);
        }
    }

    private static async Task CheckAtlasRefresh(VulkanViewport viewport)
    {
        var scene = new Scene();
        var item = scene.AddEmpty();
        var id = Guid.NewGuid();
        item.Attach(new PureEngine.Core.Transform());
        item.Attach(new UiElement { Pivot = Vector2.Zero });
        item.Attach(new global::Image { Sprite = new Sprite(id) });
        var images = new Dictionary<Guid, byte[]> { [id] = Encode(SKColors.Red) };
        DrawList? observed = null;
        var previousBuilder = viewport.SceneBuilder;
        viewport.SceneBuilder = (draw, size) =>
        {
            observed = draw;
            EditSceneRenderer.Build(draw, scene, images, size);
        };
        try
        {
            await Frames(viewport);
            if (EditPreviewChecks.ReadFirstPixel(observed!) != SKColors.Red) throw new Exception("Initial atlas image was not red.");
            var previousRevision = observed!.Revision;
            images[id] = Encode(SKColors.Blue);
            viewport.InvalidateImageCache();
            await Frames(viewport);
            if (observed.Revision <= previousRevision || EditPreviewChecks.ReadFirstPixel(observed) != SKColors.Blue)
                throw new Exception("Viewport did not replace the atlas after image invalidation.");
            Console.WriteLine("GPU viewport atlas refresh: same image ID changed from red to blue across completed frames.");
        }
        finally { viewport.SceneBuilder = previousBuilder; }

        static byte[] Encode(SKColor color)
        {
            using var bitmap = new SKBitmap(4, 4);
            bitmap.Erase(color);
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
    }
}

