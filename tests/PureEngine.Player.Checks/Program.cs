using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using PureEngine.Core;
using PureEngine.Player;
using PureEngine.Runtime;
using Button = PureEngine.Core.Button;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        InputChecks();
        PackageChecks();
        Console.WriteLine("PASS: Player queued pointer/keyboard input, cancellation, clipping, package relocation, invalid packages, window lifecycle, and Editor assembly separation.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static ComponentRegistry Registry()
    {
        var registry = new ComponentRegistry();
        registry.Register<Transform>("core.transform");
        registry.Register<UiElement>("core.ui-element");
        registry.Register<Button>("core.button");
        return registry;
    }

    private static Scene SceneWithButton()
    {
        var scene = new Scene();
        var item = scene.AddEmpty();
        item.Attach(new Transform());
        item.Attach(new UiElement { Pivot = Vector2.Zero, SizeDelta = new Vector2(100, 100) });
        item.Attach(new Button());
        return scene;
    }

    private static void InputChecks()
    {
        using var session = PlaySession.Prepare(SceneWithButton(), Registry(), _ => { });
        var runtime = session.Runtime;
        var item = runtime.Scene.Objects.Single();
        var clicks = 0;
        item.GetComponent<Button>()!.Clicked += _ => clicks++;
        session.Start();
        var input = new PlayerInput(runtime);
        var size = new Vector2(400, 300);
        var point = new Vector2(20, 20);
        Check(input.Press(size, point) && input.Release(size, point), "A matching pointer release must queue a click.");
        Check(clicks == 0, "Input must not execute user callbacks outside Step.");
        session.Step(0);
        Check(clicks == 1, "Step must dispatch one queued click.");
        input.Press(size, point);
        Check(!input.Release(size, new Vector2(200, 200)), "Releasing outside must cancel activation.");
        input.Press(size, point);
        input.Cancel();
        Check(!input.Release(size, point), "Lost focus/capture must cancel activation.");
        input.Tab(size, false);
        Check(input.Focused == item.Id && input.KeyDown(size) && !input.KeyDown(size), "Tab must focus; held keys must activate only once.");
        session.Step(0);
        Check(clicks == 2, "Keyboard activation must pass through Step.");
        input.Tab(size, true);
        Check(!input.KeyDown(size), "Tab while holding an activation key must not reset repeat suppression.");
        input.KeyUp();
        input.Press(size, point);
        item.GetComponent<Button>()!.Interactable = false;
        Check(!input.Release(size, point), "Disabling a button during a press must prevent its click.");
        input.Tab(size, false);
        Check(input.Focused is null, "Disabled buttons must not receive keyboard focus.");
        item.GetComponent<Button>()!.Interactable = true;
        item.GetComponent<Transform>()!.LocalPosition = new Vector3(1000, 1000, 0);
        input.Tab(size, false);
        Check(input.Focused is null, "Fully clipped buttons must not receive keyboard focus.");
        item.GetComponent<Transform>()!.LocalPosition = Vector3.Zero;
        input.Validate(size, [new(item.Id, item.Name, "Invalid image")]);
        Check(!input.Press(size, point), "Undrawable buttons must not be clickable.");
        input.Validate(size, []);
        Check(!input.Press(size, new Vector2(float.NaN, 0)), "Non-finite pointer positions must be ignored.");
        session.Stop();
        Check(!input.Press(size, point) && !runtime.IsRunning, "Stopped sessions must not accept input.");
    }

    private static void PackageChecks()
    {
        var root = Path.Combine(Path.GetTempPath(), "PureEngine-PlayerChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var original = Path.Combine(root, "original");
            var relocated = Path.Combine(root, "relocated");
            Directory.CreateDirectory(Path.Combine(original, "Data", "Scenes"));
            File.WriteAllText(Path.Combine(original, "Data", "Scenes", "Main.pure.scene.yaml"),
                new SceneSerializer(Registry()).Serialize(SceneWithButton()));
            WriteManifest(original, "Scenes/Main.pure.scene.yaml");
            Directory.Move(original, relocated);
            using (var package = GamePackage.Open(relocated))
            {
                Check(package.Name == "Player checks", "Package metadata must survive relocation.");
                using var session = package.CreateSession();
                Check(!session.Runtime.IsRunning, "Package preparation must not start lifecycle callbacks.");
                session.Start();
                session.Step(0);
                Check(session.Runtime.Scene.Objects.Count == 1, "Packaged startup scene must load without its original source path.");
            }
            WindowChecks(relocated);
            WriteManifest(relocated, "../outside.pure.scene.yaml");
            RejectPackage(relocated, "Startup paths escaping Data must be rejected.");
            File.WriteAllText(Path.Combine(relocated, "package.json"), "{broken");
            RejectPackage(relocated, "Corrupt manifests must be rejected.");
            WriteManifest(relocated, "Scenes/Missing.pure.scene.yaml");
            RejectPackage(relocated, "Missing startup scenes must be rejected.");
            string?[] references = [.. typeof(PlayerWindow).Assembly.GetReferencedAssemblies().Select(assembly => assembly.Name)];
            Check(!references.Contains("PureEngine.Editor") && !references.Contains("Microsoft.CodeAnalysis.CSharp"),
                "Player must not depend on the Editor or runtime compilation.");
        }
        finally
        {
#pragma warning disable CA2219 // Fail closed before deleting any temporary test data.
            if (Path.GetDirectoryName(root) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                || !Path.GetFileName(root).StartsWith("PureEngine-PlayerChecks-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe Player test cleanup path.");
#pragma warning restore CA2219
            Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteManifest(string root, string startup) =>
        File.WriteAllText(Path.Combine(root, "package.json"), JsonSerializer.Serialize(new
        {
            Version = 1,
            Name = "Player checks",
            StartupScene = startup,
            GameAssembly = (string?)null,
            Types = new Dictionary<string, string>(),
        }));

    private static void RejectPackage(string root, string message)
    {
        var rejected = false;
        try { using var package = GamePackage.Open(root); }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or JsonException)
        {
            rejected = true;
        }
        Check(rejected, message);
    }

    private static void WindowChecks(string root)
    {
        using var package = GamePackage.Open(root);
        var window = new PlayerWindow(package);
        var surface = (Grid)window.Content!;
        // Native Vulkan presentation is exercised separately on Windows, not by the headless platform.
        surface.Children.Clear();
        var session = (PlaySession)typeof(PlayerWindow).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Check(session.Runtime.IsRunning, "Opening the Player window must start the prepared runtime.");
        var clicks = 0;
        session.Runtime.Scene.Objects.Single().GetComponent<Button>()!.Clicked += _ => clicks++;
        surface.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab });
        surface.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        session.Step(0);
        Check(clicks == 1, "Window keyboard events must reach the runtime input queue.");
        window.Close();
        Dispatcher.UIThread.RunJobs();
        Check(!session.Runtime.IsRunning, "Closing the Player window must stop its runtime.");
    }
}
