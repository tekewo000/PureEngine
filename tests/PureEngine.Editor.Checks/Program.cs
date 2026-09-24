using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PureEngine.Editor;

internal static class Program
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static T Control<T>(Window window, string name) where T : Control => window.FindControl<T>(name)!;
    private static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    internal static void Until(Func<bool> condition, string message = "Timed out waiting for UI work.")
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            using var slice = new CancellationTokenSource(10);
            Dispatcher.UIThread.MainLoop(slice.Token);
        }
        Check(condition(), message);
    }

    internal static void Wait(Task task)
    {
        Until(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--vulkan"))
        {
            Environment.ExitCode = PureEngine.Rendering.Avalonia.VulkanViewport.Configure(AppBuilder.Configure<VulkanCheckApp>().UsePlatformDetect()).StartWithClassicDesktopLifetime([]);
            return;
        }
        RenderingChecks.Run();
        ComponentSearchChecks.Run();
        EditPreviewChecks.Run();
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PureEngine-LauncherChecks-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions())
                .SetupWithClassicDesktopLifetime([]);
            using var desktop = (ClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
            if (args.Contains("--game-buttons"))
            {
                desktop.MainWindow!.Show();
                GameButtonChecks.Run();
                return;
            }
            Check(desktop.MainWindow is LauncherWindow, "Startup must display the Launcher.");
            var historyPath = Path.Combine(root, "history.json");
            var launcher = new LauncherWindow(new RecentProjects(historyPath));
            desktop.MainWindow = launcher;
            launcher.Show();
            Dispatcher.UIThread.RunJobs();
            UiTextEditorChecks.Run();
            PlayConnectionChecks.Run();
            ProjectDropChecks.Run();
            UiMenuChecks.Run();
            EditorOwnershipChecks.Run(root);
            ConsoleChecks.Run();
            EditorSeparationChecks.Run(root);
            UserCodeChecks.Run(root);
            ProjectIsolationChecks.Run(root);
            ProjectServiceRegistrationChecks.Run(root);
            UserCodeBackgroundChecks.Run(root);
            IntegratedArchitectureChecks.Run(root);
            ProjectAssetChecks.Run(root);
            DataAssetMenuChecks.Run(root);
            UiEndToEndChecks.Run(root);
            SpriteReopenChecks.Run(root);
            Control<TextBox>(launcher, "ProjectLocation").Text = root;
            Control<TextBox>(launcher, "ProjectName").Text = "LauncherTest";
            Click(Control<Button>(launcher, "CreateProjectButton"));
            var editor = desktop.Windows.OfType<MainWindow>().Single();
            Check(editor.IsVisible && !launcher.IsVisible, "Creating a project must enter Editor and hide Launcher.");
            Check(editor.Title!.Contains("LauncherTest") && Control<TreeView>(editor, "SceneObjects").Items.Count == 0,
                "New projects must open an empty Main scene.");
            Check(Control<TreeView>(editor, "ProjectTree").Items.Count > 0, "Project Explorer was not populated.");
            var manifest = Path.Combine(root, "LauncherTest", "Project.pure.project.yaml");
            Check(File.Exists(manifest), "New project was not saved.");
            editor.Close();
            Dispatcher.UIThread.RunJobs();
            Check(launcher.IsVisible && !desktop.Windows.OfType<MainWindow>().Any(), "Closing Editor must return to Launcher.");
            var recent = Control<ListBox>(launcher, "RecentList");
            Check(recent.Items.Count == 1 && !Control<TextBlock>(launcher, "EmptyHistory").IsVisible, "Recent projects were not refreshed.");
            recent.SelectedIndex = 0;
            recent.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Dispatcher.UIThread.RunJobs();
            Until(() => desktop.Windows.OfType<MainWindow>().Any());
            editor = desktop.Windows.OfType<MainWindow>().Single();
            Check(editor.IsVisible && !launcher.IsVisible, "Enter must open the selected recent project.");

            PriorityInspectorChecks.Run(editor);
            InspectorValueEditorChecks.Run(editor);
            ReferenceEditorChecks.Run(editor);
            UiImageEditorChecks.Run(editor);
            // Isolated Scene View editors; closed before the unsaved flow so window counts stay intact.
            SceneViewEditorChecks.Run();
            HierarchySelectionChecks.Run();
            GameButtonChecks.Run();
            // Inspector checks leave unsaved objects; discard them so the following flow starts clean.
            editor.Close();
            Dispatcher.UIThread.RunJobs();
            var inspectorDiscard = desktop.Windows.Single(window => window.Title == "Unsaved Scene");
            Click(inspectorDiscard.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Discard")));
            Dispatcher.UIThread.RunJobs();
            Check(launcher.IsVisible && !desktop.Windows.OfType<MainWindow>().Any(), "Inspector Discard must return to Launcher.");
            recent.SelectedIndex = 0;
            recent.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Dispatcher.UIThread.RunJobs();
            Until(() => desktop.Windows.OfType<MainWindow>().Any());
            editor = desktop.Windows.OfType<MainWindow>().Single();

            // Exercise the real unsaved confirmation through add-object, close, Cancel, and Discard.
            var sceneSurface = Control<Grid>(editor, "SceneSurface");
            sceneSurface.ContextMenu!.Items.OfType<MenuItem>().First().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Check(editor.Title!.StartsWith("* "), "Adding an object must mark the scene dirty.");
            editor.Close();
            Dispatcher.UIThread.RunJobs();
            var confirmation = desktop.Windows.Single(window => window.Title == "Unsaved Scene");
            Click(confirmation.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Cancel")));
            Check(editor.IsVisible && !launcher.IsVisible, "Cancel must keep the current editor open.");
            editor.Close();
            Dispatcher.UIThread.RunJobs();
            confirmation = desktop.Windows.Single(window => window.Title == "Unsaved Scene");
            Click(confirmation.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Discard")));
            Check(launcher.IsVisible && !desktop.Windows.OfType<MainWindow>().Any(), "Discard must return to Launcher.");

            // A same-name creation fails without replacing files or leaving Launcher.
            Click(Control<Button>(launcher, "CreateProjectButton"));
            Check(launcher.IsVisible && Control<TextBlock>(launcher, "LauncherError").IsVisible
                && !desktop.Windows.OfType<MainWindow>().Any(), "Duplicate project failure must remain in Launcher.");
            var history = new RecentProjects(historyPath);
            history.Load();
            Check(history.Entries.Count == 1 && history.Entries[0].ManifestPath == manifest, "History must survive a reload without duplicates.");
            using (var loaded = ProjectSession.Open(manifest))
            {
                Check(loaded.Scene.Objects.Count == 0, "Discard must not write unsaved objects.");
            }

            // Corrupt startup scene and missing manifest both leave Launcher usable.
            var startup = Path.Combine(Path.GetDirectoryName(manifest)!, "Scenes", "Main.pure.scene.yaml");
            File.WriteAllText(startup, "version: 999\nobjects: []\n");
            recent.SelectedIndex = 0;
            recent.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Dispatcher.UIThread.RunJobs();
            Until(() => Control<TextBlock>(launcher, "LauncherError").IsVisible);
            Check(launcher.IsVisible && Control<TextBlock>(launcher, "LauncherError").IsVisible
                && !desktop.Windows.OfType<MainWindow>().Any(), "Invalid startup scene must not open Editor.");
            File.Delete(manifest);
            recent.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Dispatcher.UIThread.RunJobs();
            Check(launcher.IsVisible && !desktop.Windows.OfType<MainWindow>().Any(), "Missing recent project must not close Launcher.");
            File.WriteAllText(historyPath, "{broken");
            history.Load();
            Check(history.Entries.Count == 0 && history.Warning is not null, "Corrupt history must allow a fresh Launcher.");
            var exited = false;
            desktop.Exit += (_, _) => exited = true;
            launcher.Close();
            Check(exited, "Closing Launcher must exit without a hidden window keeping the app alive.");
            Console.WriteLine("PASS: Launcher startup, create, recent reopen, Editor return, unsaved Cancel/Discard, project failures, history, and exit.");
        }
        finally
        {
#pragma warning disable CA2219 // Fail closed: an unsafe cleanup path must never reach Directory.Delete.
            if (Path.GetDirectoryName(root) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                || !Path.GetFileName(root).StartsWith("PureEngine-LauncherChecks-"))
                throw new InvalidOperationException("Unsafe test cleanup path.");
#pragma warning restore CA2219
            Directory.Delete(root, recursive: true);
        }
    }
}
