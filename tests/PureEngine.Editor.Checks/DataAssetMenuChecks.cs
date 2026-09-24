using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PureEngine.Core;
using PureEngine.Editor;

static class DataAssetMenuChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(string root)
    {
        using var session = ProjectSession.Create(root, "DataAssetMenus");
        var project = session.Project;
        Directory.CreateDirectory(Path.Combine(project.RootDirectory, "Scenes", "Nested"));
        session.Components.Registry.Register<MenuAssetFixture>("checks.menu-asset");
        var editor = new MainWindow(session);
        editor.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var files = editor.FindControl<ListBox>("ProjectFiles")!;
            files.SelectedItem = files.ItemsSource!.Cast<ProjectExplorerEntry>().Single(entry => entry.DisplayName == "Nested");
            var treeMenu = editor.FindControl<MenuItem>("TreeCreateDataAssetMenu")!;
            Call(editor, "RefreshDataAssetMenu", treeMenu, true);
            var leaf = treeMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Items"))
                .Items.OfType<MenuItem>().Single();
            Click(leaf);
            var first = Path.Combine(project.RootDirectory, "Scenes", "Weapon.pure.asset.yaml");
            Check(File.Exists(first), "Tree creation must use the selected tree folder, not the selected file-pane folder.");
            var (asset, id, _) = DataAssetFile.Load(first, session.Components.Registry);
            Check(asset is MenuAssetFixture { Attack: 12 } && id != Guid.Empty,
                "Menu creation must persist constructor defaults and identity.");
            var store = (EditSceneStore)typeof(MainWindow).GetField("_editScene", Instance)!.GetValue(editor)!;
            Check(!store.IsDirty && store.Current.Objects.Count == 0, "Creating an asset must not change the scene.");
            Check(files.ItemsSource!.Cast<ProjectExplorerEntry>().Any(entry => entry.IsDataAsset && entry.FullPath == first),
                "The created asset must appear in the Project pane.");

            files.SelectedItem = files.ItemsSource!.Cast<ProjectExplorerEntry>().Single(entry => entry.DisplayName == "Nested");
            var filesMenu = editor.FindControl<MenuItem>("FilesCreateDataAssetMenu")!;
            Call(editor, "RefreshDataAssetMenu", filesMenu, true);
            var fileLeaf = filesMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Items"))
                .Items.OfType<MenuItem>().Single();
            Click(fileLeaf);
            Check(File.Exists(Path.Combine(project.RootDirectory, "Scenes", "Nested", "Weapon.pure.asset.yaml")),
                "Files creation must use its selected folder.");
            Call(editor, "StartPlay");
            Check(editor.IsPlaying, "Play must start for the guard test.");
            var before = Directory.GetFiles(project.RootDirectory, "*.pure.asset.yaml", SearchOption.AllDirectories).Length;
            Click(fileLeaf);
            Check(Directory.GetFiles(project.RootDirectory, "*.pure.asset.yaml", SearchOption.AllDirectories).Length == before,
                "A menu created before Play must not create assets during Play.");
            Call(editor, "StopPlay");
        }
        finally
        {
            if (editor.IsPlaying) Call(editor, "StopPlay");
            editor.Close();
            Dispatcher.UIThread.RunJobs();
        }
        Console.WriteLine("PASS: data asset menus, destination selection, defaults, listing, scene isolation, and Play guard.");
    }

    private static void Call(MainWindow editor, string method, params object?[] args) =>
        typeof(MainWindow).GetMethod(method, Instance)!.Invoke(editor, args);

    private static void Click(MenuItem item)
    {
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

[DataAsset("Items/Weapon")]
public sealed class MenuAssetFixture
{
    [Inspector] public int Attack { get; set; } = 12;
}
