using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using PureEngine.Rendering.Avalonia;

namespace PureEngine.Player;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => VulkanViewport.Configure(AppBuilder.Configure<ProbeApp>().UsePlatformDetect())
        .StartWithClassicDesktopLifetime(args);
}

public sealed class ProbeApp : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewport = new VulkanViewport();
            viewport.RenderingFailed += Console.Error.WriteLine;
            desktop.MainWindow = new Window { Title = "PureEngine — Vulkan V2 rendering probe", Width = 800, Height = 450, Content = viewport };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
