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
        Check(plain.IsPlainFile && !plain.IsFolder && !plain.IsScene && !plain.IsDataAsset && !plain.IsPrefab && !plain.IsImageFile && !plain.IsCSharpFile,
            "Other files must keep the generic file icon.");

        var dataAsset = new ProjectExplorerEntry(ProjectExplorerKind.DataAsset, "Sword.pure.asset.yaml", "Data Asset", "Items/Sword.pure.asset.yaml",
            "Items/Sword.pure.asset.yaml", "Sword.pure.asset.yaml", null, false);
        Check(dataAsset.IsDataAsset && !dataAsset.IsPlainFile && !dataAsset.IsImageFile && !dataAsset.IsCSharpFile,
            "Data asset entries must select the data asset icon instead of the generic file icon.");

        var image = new ProjectExplorerEntry(ProjectExplorerKind.File, "hero.png", "", "Assets/hero.png",
            "Assets/hero.png", "C:/game/Assets/hero.png", null, false);
        Check(image.IsImageFile && !image.IsPlainFile && !image.IsCSharpFile && !image.IsDataAsset,
            "Image entries must select the image icon instead of the generic file icon.");

        Console.WriteLine("PASS: project explorer icon selection for folders, scenes, prefabs, C# files, data assets, images, and plain files.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}