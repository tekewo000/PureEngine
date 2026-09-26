using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using PureEngine.Runtime;

namespace PureEngine.Player;

public sealed class PlayerApplication : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            GamePackage? package = null;
            try
            {
                // The executable's directory is the package root, regardless of the launcher's working directory.
                package = GamePackage.Open(AppContext.BaseDirectory);
                desktop.MainWindow = new PlayerWindow(package);
            }
            catch (Exception error)
            {
                try { package?.Dispose(); }
                catch (Exception cleanup) { error = new AggregateException(error, cleanup); }
                Console.Error.WriteLine(error);
                desktop.MainWindow = new Window
                {
                    Title = "Game could not start",
                    Width = 640,
                    Height = 240,
                    Content = new TextBlock
                    {
                        Text = "The game package could not be loaded.\n\n" + error.GetBaseException().Message,
                        Margin = new Thickness(24),
                        TextWrapping = TextWrapping.Wrap,
                    },
                };
                desktop.Exit += (_, e) => e.ApplicationExitCode = 1;
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
