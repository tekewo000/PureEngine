using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PureEngine.Core;

namespace PureEngine.Editor;

public partial class MainWindow
{
    internal static readonly DataFormat<string> PrefabPathFormat =
        DataFormat.CreateInProcessFormat<string>("PureEngine.PrefabPath");
    private ProjectExplorerEntry? _pressedPrefab;

    /// <summary>Builds the Project-to-Stuffs drag payload carrying the prefab file path.</summary>
    internal static DataTransfer CreatePrefabTransfer(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(PrefabPathFormat, Path.GetFullPath(path)));
        return transfer;
    }

    /// <summary>Builds a prefab catalog snapshot for the project. Empty without a project or on scan failure.</summary>
    private PrefabCatalog BuildPrefabCatalog()
    {
        if (_project is null) return PrefabCatalog.Empty;
        try
        {
            var catalog = PrefabCatalog.ScanFolder(_project.RootDirectory, out var diagnostics);
            foreach (var diagnostic in diagnostics) Log.Engine.Warning(diagnostic);
            return catalog;
        }
        catch (Exception error)
        {
            Log.Engine.Error("Cannot scan prefabs.", error);
            return PrefabCatalog.Empty;
        }
    }

    private async void OnSavePrefab(object? sender, RoutedEventArgs e)
    {
        if (GetSelectedSceneObject() is not SceneObject root) return;
        await RunFileOperation(async () =>
        {
            if (_project is null || RejectWhenPlaying("Save Prefab")) return;
            var name = await AskExplorerName("Save as Prefab", "Prefab file name", _project.NextPrefabName(_explorerFolder, root.Name));
            if (name is null) return;
            SavePrefabToPath(root, _explorerFolder, name);
            await Task.CompletedTask;
        });
    }

    /// <summary>Saves the subtree as a prefab file. Testable core of the Save as Prefab menu without dialogs.</summary>
    internal string SavePrefabToPath(SceneObject root, string relativeDirectory, string fileName)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (_project is null) throw new InvalidOperationException("Open a project first.");
        if (RejectWhenPlaying("Save Prefab")) throw new InvalidOperationException("Cannot save prefabs while playing.");
        if (!string.Equals(fileName, fileName.Trim(), StringComparison.Ordinal)
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Specify a valid file name.");
        if (!ProjectFile.IsPrefabFileName(fileName)) fileName += ".pure.prefab.yaml";
        var path = Path.Combine(_project.ResolveDirectoryPath(relativeDirectory), fileName);
        _project.ValidatePrefabPath(path);
        if (File.Exists(path) || Directory.Exists(path))
            throw new IOException("A folder or file with the same name already exists.");
        PrefabFile.Create(path, _editScene.Current, root, _components.Registry);
        _explorerFolder = relativeDirectory;
        _explorerSelectedFile = path;
        RefreshProjectExplorer();
        SetFileStatus($"Saved prefab: {fileName}");
        return path;
    }

    /// <summary>Creates a prefab file from a Stuffs object dropped onto the Project pane. Testable core of Stuff-to-Project drag-drop.</summary>
    internal string CreatePrefabFromDrop(Guid objectId, string targetRelative)
    {
        if (_project is null) throw new InvalidOperationException("Open a project first.");
        if (RejectWhenPlaying("Save Prefab")) throw new InvalidOperationException("Cannot save prefabs while playing.");
        var root = FindObject(objectId) ?? throw new ArgumentException("The dragged object was not found.");
        var fileName = _project.NextPrefabName(targetRelative, root.Name);
        return SavePrefabToPath(root, targetRelative, fileName);
    }

    private async void OnExplorerPlacePrefab(object? sender, RoutedEventArgs e)
    {
        if (ProjectFiles.SelectedItem is not ProjectExplorerEntry { Kind: ProjectExplorerKind.Prefab, FullPath: not null } entry) return;
        await RunFileOperation(async () =>
        {
            if (HasInputErrors || !await ConfirmCloseDataAsset()) return;
            ActivateEditorViewport(0);
            PlacePrefabForTest(entry.FullPath);
        });
    }

    /// <summary>Places a prefab file into the editing scene under the selected parent. Testable core of prefab placement.</summary>
    internal SceneObject PlacePrefabForTest(string path) => PlacePrefabAt(path, GetSelectedSceneObject());

    /// <summary>Places a prefab file under the given parent. Shared by the placement menu and drag-drop.</summary>
    internal SceneObject PlacePrefabAt(string path, SceneObject? parent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (_project is null) throw new InvalidOperationException("Open a project first.");
        if (RejectWhenPlaying("Place Prefab")) throw new InvalidOperationException("Cannot place prefabs while playing.");
        if (IsPrefabEditing) parent ??= _prefabScene!.Current.RootObjects.Single();
        _project.ValidatePrefabPath(path);
        var document = PrefabFile.Load(path);
        SceneObject placed;
        try
        {
            placed = new PrefabSerializer(_components.Registry).Instantiate(
                _editScene.Current, document, out _, parent, EditSession.Factory);
        }
        catch (Exception error)
        {
            SetFileStatus($"Cannot place prefab: {error.GetBaseException().Message}", true);
            throw;
        }
        MarkSceneChanged();
        RefreshHierarchy(placed.Id, expandId: parent?.Id);
        RefreshObjectInspector();
        SetFileStatus($"Placed prefab: {Path.GetFileName(path)}");
        return placed;
    }
}
