using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PureEngine.Core;
using PureEngine.Editor;

internal static class DirectAssetEditorChecks
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static object? Call(MainWindow window, string name, params object?[] args)
    {
        var result = typeof(MainWindow).GetMethod(name, Flags)!.Invoke(window, args);
        if (result is Task task) Program.Wait(task);
        return result;
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static ComboBox ReferenceBox(MainWindow editor) =>
        editor.GetVisualDescendants().OfType<ComboBox>().Single(box => AutomationProperties.GetName(box) == "Test.DataAssets");

    public static void Run(string parent)
    {
        using var created = ProjectSession.Create(parent, "DirectAssetReference");
        var project = created.Project;
        created.Dispose();
        var codePath = Path.Combine(project.RootDirectory, "Test.cs");
        const string code = """
            using PureEngine.Core;
            [DataAsset]
            public sealed class TestDataAssets
            {
                [Inspector] public int TestInt { get; set; } = 1;
            }
            public sealed class Test
            {
                [Inspector] public TestDataAssets DataAssets { get; init; }
                public int Observed { get; private set; }
                [Start] public void Start() { Observed = DataAssets.TestInt; DataAssets.TestInt = 99; }
            }
            """;
        File.WriteAllText(codePath, code);
        using var session = ProjectSession.Open(project.ManifestPath);
        var type = session.Components.UserTypes.Single(type => type.Name == "Test");
        var assetType = session.Components.DataAssetTypes.Single();
        var path = Path.Combine(project.RootDirectory, "TestAsset.pure.asset.yaml");
        var id = DataAssetFile.Create(path, assetType, session.Components.Registry);
        var item = session.Scene.AddEmpty();
        Check(session.Components.TryAttach(item, type), "Fixture must attach.");
        var editor = new MainWindow(session) { WindowState = WindowState.Normal, Width = 1280, Height = 800 };
        editor.FindControl<Grid>("SceneViewport")!.Children.Clear();
        editor.FindControl<Grid>("GameViewport")!.Children.Clear();
        editor.Show();
        Dispatcher.UIThread.RunJobs();
        var state = (EditSceneStore)typeof(MainWindow).GetProperty("EditSceneStore", Flags)!.GetValue(editor)!;
        try
        {
            typeof(MainWindow).GetField("_explorerFolder", Flags)!.SetValue(editor, "");
            Call(editor, "RefreshProjectExplorer");
            Call(editor, "SelectSceneObjectForTest", item);
            Dispatcher.UIThread.RunJobs();
            var combo = ReferenceBox(editor);
            var candidate = combo.Items.Cast<object>().Single(option => option.ToString() == "TestAsset.pure.asset.yaml");
            combo.SelectedItem = candidate;
            Dispatcher.UIThread.RunJobs();
            var holder = item.Components.Single();
            Check(holder.GetType().GetProperty("DataAssets")!.GetValue(holder) is not null, "Direct init property must receive the asset.");
            var clear = editor.GetVisualDescendants().OfType<Avalonia.Controls.Button>()
                .Single(button => AutomationProperties.GetName(button) == "Test.DataAssets.Clear");
            clear.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Check(holder.GetType().GetProperty("DataAssets")!.GetValue(holder) is null, "Clear must remove the assignment.");

            // Real pointer drag: pressing the asset must not switch the Inspector.
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            var row = editor.GetVisualDescendants().OfType<ListBoxItem>().Single(row =>
                row.DataContext is ProjectExplorerEntry { Kind: ProjectExplorerKind.DataAsset });
            var from = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), editor)!.Value;
            combo = ReferenceBox(editor);
            var to = combo.TranslatePoint(new Point(combo.Bounds.Width / 2, combo.Bounds.Height / 2), editor)!.Value;
            editor.MouseDown(from, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Check(ReferenceEquals(Call(editor, "GetSelectedSceneObject"), item), "Press must preserve the target Inspector.");
            editor.MouseMove(from + new Vector(8, 0), RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
            editor.MouseMove(to, RawInputModifiers.LeftMouseButton);
            editor.MouseUp(to, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Check(holder.GetType().GetProperty("DataAssets")!.GetValue(holder) is not null, "Actual file D&D must assign a direct asset reference.");
            Call(editor, "SaveSceneAsync", false);
            Check(File.ReadAllText(project.StartupScenePath).Contains(id.ToString("D")), "Save must persist the asset ID.");

            // Recompiled types and the startup scene must resolve to the current snapshot.
            File.WriteAllText(codePath, code + "\n// Reload fixture\n");
            Call(editor, "ReloadUserCode");
            holder = state.Current.Objects.Single().Components.Single();
            Check(holder.GetType() != type && holder.GetType().GetProperty("DataAssets")!.GetValue(holder) is not null,
                "Code reload must resolve the reference against new types.");
            Call(editor, "SaveSceneAsync", false);
            Call(editor, "OpenScenePathAsync", project.StartupScenePath);
            for (var run = 0; run < 2; run++)
            {
                Call(editor, "StartPlay");
                var play = (PureEngine.Runtime.PlaySession)typeof(MainWindow).GetProperty("ActivePlay", Flags)!.GetValue(editor)!;
                var live = play.Runtime.Scene.Objects.Single().Components.Single();
                Check((int)live.GetType().GetProperty("Observed")!.GetValue(live)! == 1, "Each Play must read saved data through the direct property.");
                Call(editor, "StopPlay");
            }
            Check(File.ReadAllText(path).Contains("TestInt: 1"), "Play must not write asset mutations.");
            File.Delete(path);
            Call(editor, "OpenScenePathAsync", project.StartupScenePath);
            var missing = state.Current.Objects.Single().Components.Single();
            Check(missing.GetType().GetProperty("DataAssets")!.GetValue(missing) is null, "Missing asset must resolve to null.");
            Call(editor, "SaveSceneAsync", false);
            Check(File.ReadAllText(project.StartupScenePath).Contains(id.ToString("D")), "Missing ID must remain in the scene.");
        }
        finally
        {
            state.MarkClean();
            editor.Close();
            Dispatcher.UIThread.RunJobs();
        }
        Console.WriteLine("PASS: direct asset dropdown, Clear, real file D&D, save/reopen, code reload, Play, and missing IDs.");
    }
}
