using Avalonia;

namespace PureEngine.Editor;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        PureEngine.Rendering.Avalonia.VulkanViewport.Configure(AppBuilder.Configure<App>()
            .UsePlatformDetect())
            .StartWithClassicDesktopLifetime(args);
}
