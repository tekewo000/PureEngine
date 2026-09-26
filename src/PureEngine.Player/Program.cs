using Avalonia;
using PureEngine.Rendering.Avalonia;
using PureEngine.Runtime;

namespace PureEngine.Player;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--smoke-test"])
            return SmokeTest();
        return VulkanViewport.Configure(AppBuilder.Configure<PlayerApplication>().UsePlatformDetect())
            .StartWithClassicDesktopLifetime(args);
    }

    private static int SmokeTest()
    {
        try
        {
            using var package = GamePackage.Open(AppContext.BaseDirectory);
            using var session = package.CreateSession();
            session.Start();
            session.Step(1f / 60);
            session.Stop();
            if (session.Runtime.Errors.Count != 0)
                throw new AggregateException("Packaged game lifecycle failed.", session.Runtime.Errors.Select(error => error.Exception));
            Console.WriteLine("PASS: Packaged game loaded, started, stepped, and stopped.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
