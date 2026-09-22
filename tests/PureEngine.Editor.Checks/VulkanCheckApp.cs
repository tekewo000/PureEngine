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
}

