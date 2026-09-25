using PureEngine.Editor;

internal static class ProjectExplorerIconChecks
{
    public static void Run()
    {
        var folder = new ProjectExplorerEntry(ProjectExplorerKind.Folder, "Assets", "", "Assets", "Assets", null, null, false);
        Check(folder.IsFolder && !folder.IsScene && !folder.IsPrefab && !folder.IsCSharpFile && !folder.IsPlainFile,
            "Folder entries must select the folder icon.");

        var scene = new ProjectExplorerEntry(ProjectExplorerKind.Scene, "Main.pure.scene.yaml", "", "Scenes/Main.pure.scene.yaml",
            "Scenes/Main.pure.scene.yaml", "Main.pure.scene.yaml", null, false);
        Check(scene.IsScene && !scene.IsFolder && !scene.IsPrefab && !scene.IsCSharpFile && !scene.IsPlainFile,
            "Scene entries must select the scene icon.");

        var prefab = new ProjectExplorerEntry(ProjectExplorerKind.Prefab, "Box.pure.prefab.yaml", "Prefab", "Prefabs/Box.pure.prefab.yaml",
            "Prefabs/Box.pure.prefab.yaml", "Box.pure.prefab.yaml", null, false);
        Check(prefab.IsPrefab && !prefab.IsPlainFile && !prefab.IsCSharpFile,
            "Prefab entries must select the prefab icon instead of the generic file icon.");

        var script = new ProjectExplorerEntry(ProjectExplorerKind.File, "Player.cs", "C# Player", "Scripts/Player.cs",
            "Scripts/Player.cs", "Player.cs", null, false);
        Check(script.IsCSharpFile && !script.IsPlainFile,
            "C# entries must select the C# icon instead of the generic file icon.");

        var plain = new ProjectExplorerEntry(ProjectExplorerKind.File, "notes.txt", "", "notes.txt", "notes.txt", "notes.txt", null, false);
        Check(plain.IsPlainFile && !plain.IsFolder && !plain.IsScene && !plain.IsPrefab && !plain.IsCSharpFile,
            "Other files must keep the generic file icon.");

        Console.WriteLine("PASS: project explorer icon selection for folders, scenes, prefabs, C# files, and plain files.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}