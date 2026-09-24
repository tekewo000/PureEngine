using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using PureEngine.Core;
using PureEngine.Editor;
using Button = Avalonia.Controls.Button;

static class DataAssetEditorChecks
{
    public static void Run(string parent)
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
        static T Control<T>(Window window, string name) where T : Control => window.FindControl<T>(name)!;
        static TextBox Box(MainWindow window, string automationName) => window.GetVisualDescendants().OfType<TextBox>()
            .Single(box => Equals(box.GetValue(AutomationProperties.NameProperty) as string, automationName));
        static void Click(Button button)
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        }
        static object? Call(MainWindow window, string name, params object?[] args)
        {
            var result = typeof(MainWindow).GetMethod(name,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!.Invoke(window, args);
            if (result is Task task) Program.Wait(task);
            return result;
        }

        using var created = ProjectSession.Create(parent, "DataAssetEdit");
        var root = created.Project.RootDirectory;
        File.WriteAllText(Path.Combine(root, "Sword.cs"), """
            using PureEngine.Core;
            namespace Game;
            [DataAsset("Items/Weapon")]
            public sealed class SwordData
            {
                [Inspector] public string Name { get; set; } = "Iron";
                [Inspector] public int Attack { get; set; } = 10;
            }
            """);
        created.Dispose();
        using var session = ProjectSession.Open(Path.Combine(root, "Project.pure.project.yaml"));
        Check(session.Components.DataAssetTypes.SingleOrDefault()?.Name == "SwordData",
            "The [DataAsset] class must be detected on project open.");
        Check(session.EditServices.Services.GetService(typeof(DataAssetStore)) is DataAssetStore,
            "Edit services must provide a data asset store for constructor injection.");
        var editor = new MainWindow(session);
        editor.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var type = session.Components.DataAssetTypes.Single();
            var path = Path.Combine(root, "Sword.pure.asset.yaml");
            DataAssetFile.Create(path, type, session.Components.Registry);
            typeof(MainWindow).GetField("_explorerFolder",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(editor, "");
            Call(editor, "RefreshProjectExplorer");
            Dispatcher.UIThread.RunJobs();
            var files = Control<ListBox>(editor, "ProjectFiles");
            var entry = files.Items.OfType<ProjectExplorerEntry>()
                .Single(item => item.Kind == ProjectExplorerKind.DataAsset);
            files.SelectedItem = entry;
            Program.Until(() => Control<StackPanel>(editor, "DataAssetInspector").IsVisible,
                "Selecting a data asset must open it in the Inspector.");
            Check(Control<TextBlock>(editor, "DataAssetType").Text == session.Components.Registry.GetId(type),
                "The asset Inspector must show the registered type ID.");
            Check(!Control<TextBlock>(editor, "DataAssetTitle").Text!.StartsWith("* "),
                "A freshly opened asset must not be dirty.");

            // Editing a value marks only the asset dirty and never the scene.
            var attack = Box(editor, "SwordData.Attack");
            Check(attack.Text == "10", $"Initial Attack must be 10, got '{attack.Text}'.");
            attack.Text = "25";
            Dispatcher.UIThread.RunJobs();
            Check(Control<TextBlock>(editor, "DataAssetTitle").Text!.StartsWith("* "),
                "Asset edit must mark the asset dirty.");
            Check(!editor.Title!.StartsWith("* "), "Asset edit must not mark the scene dirty.");
            Click(Control<Button>(editor, "SaveDataAssetButton"));
            Program.Until(() => !Control<TextBlock>(editor, "DataAssetTitle").Text!.StartsWith("* "));
            Check(File.ReadAllText(path).Contains("Attack: 25"), "Save must write the edited value to the file.");
            var store = DataAssetStore.ScanFolder(root, session.Components.Registry, out _);
            Check(store.GetAll<object>().Count == 1, "The saved asset must be visible to the runtime store.");

            // Invalid input shows an error and blocks saving without touching the file.
            attack = Box(editor, "SwordData.Attack");
            attack.Text = "abc";
            Dispatcher.UIThread.RunJobs();
            Check(Control<TextBlock>(editor, "DataAssetInvalid").IsVisible, "Invalid asset input must show an error badge.");
            Click(Control<Button>(editor, "SaveDataAssetButton"));
            Check(!File.ReadAllText(path).Contains("abc"), "Invalid input must not reach the file.");
            attack.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Dispatcher.UIThread.RunJobs();
            Check(attack.Text == "25", $"Esc must restore the last saved value, got '{attack.Text}'.");
            Check(!Control<TextBlock>(editor, "DataAssetInvalid").IsVisible, "Esc must clear the asset error.");
            Click(Control<Button>(editor, "SaveDataAssetButton"));

            // Leaving through hierarchy selection closes the clean asset and shows scene components.
            var target = session.Scene.AddEmpty();
            target.Rename("Knight");
            Call(editor, "SyncHierarchyForTest");
            Call(editor, "SelectSceneObjectForTest", target);
            Dispatcher.UIThread.RunJobs();
            Check(!Control<StackPanel>(editor, "DataAssetInspector").IsVisible
                && Control<StackPanel>(editor, "ObjectInspector").IsVisible,
                "Scene selection must close the asset and show components.");
        }
        finally
        {
            editor.Close();
            Dispatcher.UIThread.RunJobs();
        }
        Console.WriteLine("PASS: data asset Inspector open, edit, save validation, and selection handoff.");
    }
}