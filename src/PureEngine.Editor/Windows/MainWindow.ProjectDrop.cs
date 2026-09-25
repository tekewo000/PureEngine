using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace PureEngine.Editor;

public partial class MainWindow
{
    /// <summary>In-process D&D payload for moving a project file or folder within the Project pane. Carries the source full path.</summary>
    internal static readonly DataFormat<string> ProjectPathFormat =
        DataFormat.CreateInProcessFormat<string>("PureEngine.ProjectPath");

    private ProjectExplorerEntry? _pressedMoveEntry;
    private PointerPressedEventArgs? _treePress;
    private Point _treePressPosition;
    private string? _treeDragPath;

    /// <summary>Initializes OS file and internal move drag-and-drop (D&D) handling for the Project pane. Called once from MainWindow().</summary>
    private void InitProjectDrop()
    {
        ProjectTree.AddHandler(DragDrop.DragOverEvent, OnProjectDropDragOver, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        ProjectTree.AddHandler(DragDrop.DropEvent, OnProjectDrop, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        ProjectFiles.AddHandler(DragDrop.DragOverEvent, OnProjectDropDragOver, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        ProjectFiles.AddHandler(DragDrop.DropEvent, OnProjectDrop, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        ProjectTree.AddHandler(PointerPressedEvent, OnProjectTreePressed, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: false);
        ProjectTree.AddHandler(PointerMovedEvent, OnProjectTreeMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        ProjectTree.AddHandler(PointerReleasedEvent, OnProjectTreeReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>Whether a right-pane entry can be moved with D&D. Covers files and folders shown in the Project pane.</summary>
    private static bool IsMovableExplorerEntry(ProjectExplorerEntry? entry) => entry is
        { Kind: ProjectExplorerKind.Folder or ProjectExplorerKind.Scene or ProjectExplorerKind.File or ProjectExplorerKind.DataAsset or ProjectExplorerKind.Prefab, FullPath: not null };

    private void OnProjectTreePressed(object? sender, PointerPressedEventArgs e)
    {
        _treePress = null;
        _treeDragPath = null;
        if (!e.GetCurrentPoint(ProjectTree).Properties.IsLeftButtonPressed) return;
        if (_project is null || IsPlaying) return;
        var node = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault();
        if (node?.Tag is not string tag || tag == ComponentsNode || tag == "") return;
        // The project root itself is not movable; structural folders are rejected on drop with a message.
        _treePress = e;
        _treePressPosition = e.GetPosition(ProjectTree);
        _treeDragPath = _project.ResolveDirectoryPath(tag);
    }

    private async void OnProjectTreeMoved(object? sender, PointerEventArgs e)
    {
        if (_treePress is null || _treeDragPath is null) return;
        if (!e.GetCurrentPoint(ProjectTree).Properties.IsLeftButtonPressed)
        {
            _treePress = null;
            _treeDragPath = null;
            return;
        }
        var delta = e.GetPosition(ProjectTree) - _treePressPosition;
        if (Math.Abs(delta.X) < 4 && Math.Abs(delta.Y) < 4) return;
        var press = _treePress;
        var path = _treeDragPath;
        _treePress = null;
        _treeDragPath = null;
        using var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(ProjectPathFormat, path));
        try { await DragDrop.DoDragDropAsync(press, transfer, DragDropEffects.Move); }
        finally { _treePress = null; _treeDragPath = null; }
    }

    private void OnProjectTreeReleased(object? sender, PointerReleasedEventArgs e)
    {
        _treePress = null;
        _treeDragPath = null;
    }

    private static string? TryGetProjectDragPath(DragEventArgs e) =>
        e.DataTransfer.TryGetValue(ProjectPathFormat);

    private void OnProjectDropDragOver(object? sender, DragEventArgs e)
    {
        if (_project is null || IsPlaying || IsFileBusy)
        {
            e.DragEffects = DragDropEffects.None;
        }
        else if (TryGetProjectDragPath(e) is { } sourceFull)
        {
            var moveRelative = ResolveMoveTargetFolder(sender, e);
            e.DragEffects = CanDropProjectPath(sourceFull, moveRelative) ? DragDropEffects.Move : DragDropEffects.None;
        }
        else if (!e.DataTransfer.Formats.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.None;
        }
        else
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        e.Handled = true;
    }

    /// <summary>Lightweight hover check for internal moves. Full validation (scene containment scan, open editors) runs on drop.</summary>
    private bool CanDropProjectPath(string sourceFull, string targetRelative)
    {
        if (_project is null) return false;
        string source;
        string targetDir;
        try
        {
            source = Path.GetFullPath(sourceFull);
            targetDir = _project.ResolveDirectoryPath(targetRelative);
        }
        catch
        {
            return false;
        }
        var isDirectory = Directory.Exists(source);
        if (!isDirectory && !File.Exists(source)) return false;
        if (string.Equals(Path.GetDirectoryName(source), targetDir, PathComparison())) return false;
        if (isDirectory)
        {
            var relative = Path.GetRelativePath(source, targetDir);
            if (!Path.IsPathRooted(relative) && relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return false;
            var sourceRelative = Path.GetRelativePath(_project.RootDirectory, source).Replace('\\', '/');
            if (IsStructuralFolder(sourceRelative)) return false;
        }
        else if (source.EndsWith(".pure.scene.yaml", StringComparison.OrdinalIgnoreCase) && !_project.IsUnderScenes(targetRelative))
        {
            return false;
        }
        else
        {
            try
            {
                var sourceRelative = Path.GetRelativePath(_project.RootDirectory, source).Replace('\\', '/');
                if (IsStructuralFolder(sourceRelative)) return false;
            }
            catch
            {
                return false;
            }
        }
        return true;
    }

    private async void OnProjectDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_project is null) return;
        if (TryGetProjectDragPath(e) is { } sourceFull)
        {
            var moveTarget = ResolveMoveTargetFolder(sender, e);
            await RunFileOperation(() => MoveProjectEntryAsync(sourceFull, moveTarget));
            return;
        }
        IStorageItem[] items = [.. e.DataTransfer.TryGetFiles() ?? []];
        if (items.Length == 0) return;

        var targetRelative = ResolveProjectDropFolder(sender, e);
        await RunFileOperation(() => ImportDroppedStorageItemsAsync(targetRelative, items));
    }

    /// <summary>
    /// Resolves the move destination folder (project-relative) from the drop position.
    /// Uses the folder under the Tree node or the right-pane folder tile, or the currently shown folder otherwise.
    /// Unlike imports, empty-space drops use the shown folder because pressing the source row already selects it.
    /// </summary>
    private string ResolveMoveTargetFolder(object? sender, DragEventArgs e)
    {
        var visual = e.Source as Visual;
        if (ReferenceEquals(sender, ProjectTree))
        {
            var node = visual?.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault();
            if (node?.Tag is string tag && tag != ComponentsNode)
                return tag;
            return _explorerFolder;
        }

        var tile = visual?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        if (tile?.DataContext is ProjectExplorerEntry entry
            && entry.Kind == ProjectExplorerKind.Folder && entry.RelativePath is not null)
            return entry.RelativePath;
        ExplorerSelectionIsFolder(out var folder, out var isComponents);
        return isComponents ? "" : folder;
    }

    /// <summary>
    /// Resolves the import destination folder (project-relative) from the drop position.
    /// Uses the folder under the Tree node or the right-pane folder tile, or the currently shown folder otherwise.
    /// </summary>
    private string ResolveProjectDropFolder(object? sender, DragEventArgs e)
    {
        var visual = e.Source as Visual;
        if (ReferenceEquals(sender, ProjectTree))
        {
            var node = visual?.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault();
            if (node?.Tag is string tag && tag != ComponentsNode)
                return tag;
            return _explorerFolder;
        }

        var tile = visual?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        if (tile?.DataContext is ProjectExplorerEntry entry
            && entry.Kind == ProjectExplorerKind.Folder && entry.RelativePath is not null)
            return entry.RelativePath;
        ExplorerSelectionIsFolder(out var folder, out var isComponents);
        if (!isComponents && ProjectFiles.SelectedItem is ProjectExplorerEntry selected
            && selected.Kind == ProjectExplorerKind.Folder && selected.RelativePath is not null
            && tile is null)
        {
            // Prefer the selected folder when a folder row is selected and the drop lands on empty space.
            return selected.RelativePath;
        }
        return isComponents ? "" : folder;
    }

    private async Task ImportDroppedStorageItemsAsync(string targetRelative, IStorageItem[] items)
    {
        if (_project is null) return;
        var targetDir = _project.ResolveDirectoryPath(targetRelative);
        var localPaths = new List<string>();
        var remoteFiles = new List<IStorageFile>();
        foreach (var item in items)
        {
            var local = item.TryGetLocalPath();
            if (local is not null && (File.Exists(local) || Directory.Exists(local)))
            {
                localPaths.Add(local);
            }
            else if (item is IStorageFile file)
            {
                remoteFiles.Add(file);
            }
            else
            {
                throw new IOException($"Drop a local file or folder: {item.Name}");
            }
        }

        var imported = new List<string>();
        try
        {
            if (localPaths.Count > 0)
                imported.AddRange(await Task.Run(() => ProjectFileImporter.ImportLocalPaths(targetDir, localPaths)));
            foreach (var file in remoteFiles)
                imported.Add(await ImportRemoteFileAsync(file, targetDir));
        }
        finally
        {
            // Still shows copied items when a multi-item import fails partway.
            RefreshProjectExplorer();
        }

        if (imported.Count == 0)
        {
            SetFileStatus("No files to import (drops into the same folder are skipped).");
            return;
        }

        _explorerFolder = targetRelative;
        _explorerSelectedFile = imported.LastOrDefault(File.Exists);
        RefreshProjectExplorer();
        var display = string.IsNullOrEmpty(targetRelative) ? "(root)" : targetRelative;
        SetFileStatus($"Imported files: {imported.Count} -> {display}");
    }

    /// <summary>Moves a project file or folder to another project folder. Keeps image sidecars, scene references, and the asset index consistent.</summary>
    private async Task MoveProjectEntryAsync(string sourceFull, string targetRelative)
    {
        if (_project is null) return;
        sourceFull = Path.GetFullPath(sourceFull);
        _project.ValidateFolderPath(sourceFull);
        var targetDir = _project.ResolveDirectoryPath(targetRelative);
        var isDirectory = Directory.Exists(sourceFull);
        var isFile = File.Exists(sourceFull);
        if (!isDirectory && !isFile)
            throw new FileNotFoundException("The source file or folder was not found.", sourceFull);
        var sourceRelative = Path.GetRelativePath(_project.RootDirectory, sourceFull).Replace('\\', '/');
        if (IsStructuralFolder(sourceRelative))
        {
            SetFileStatus("Cannot move the Scenes folder itself.", true);
            return;
        }
        if (string.Equals(Path.GetDirectoryName(sourceFull), targetDir, PathComparison()))
        {
            SetFileStatus("Already in this folder.");
            return;
        }
        if (isDirectory)
        {
            var relative = Path.GetRelativePath(sourceFull, targetDir);
            if (!Path.IsPathRooted(relative) && relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new IOException("Cannot move a folder into itself.");
            if (!_project.IsUnderScenes(targetRelative)
                && Directory.EnumerateFiles(sourceFull, "*.pure.scene.yaml", SearchOption.AllDirectories).Any())
            {
                SetFileStatus("Move scenes inside the Scenes folder.", true);
                return;
            }
        }
        else if (sourceFull.EndsWith(".pure.scene.yaml", StringComparison.OrdinalIgnoreCase))
        {
            if (!_project.IsUnderScenes(targetRelative))
            {
                SetFileStatus("Move scenes inside the Scenes folder.", true);
                return;
            }
        }
        if (ContainsOpenDataAsset(sourceFull) && !await ConfirmCloseDataAsset()) return;
        if (ContainsOpenTable(sourceFull) && !await ConfirmCloseTableRows()) return;
        if (ContainsOpenPrefab(sourceFull, isDirectory) && !await ConfirmClosePrefabEditor()) return;

        var fileName = Path.GetFileName(sourceFull);
        var sidecarSource = sourceFull + ".pureasset.yaml";
        var hasSidecar = !isDirectory && ProjectAssets.IsSupportedImage(sourceFull) && File.Exists(sidecarSource);
        var destination = ResolveMoveDestination(targetDir, fileName, isDirectory, hasSidecar);
        if (!isDirectory && destination.EndsWith(".pure.scene.yaml", StringComparison.OrdinalIgnoreCase))
            _project.ValidateScenePath(destination);
        else if (isDirectory)
            _project.ValidateFolderPath(destination);
        else
            _project.ValidateFolderPath(Path.GetDirectoryName(destination)!);

        await Task.Run(() =>
        {
            if (isDirectory)
                Directory.Move(sourceFull, destination);
            else
                File.Move(sourceFull, destination);
            if (hasSidecar)
                File.Move(sidecarSource, destination + ".pureasset.yaml");
        });
        RemapSceneReferences(sourceFull, destination, isDirectory);
        RefreshProjectAssets();
        _explorerFolder = targetRelative;
        _explorerSelectedFile = destination;
        RefreshProjectExplorer();
        RescanTableRows();
        var displayTarget = string.IsNullOrEmpty(targetRelative) ? "(root)" : targetRelative;
        SetFileStatus($"Moved {fileName} -> {displayTarget}");
    }

    /// <summary>Finds a destination that avoids collisions for the moved entry and its image sidecar.</summary>
    private static string ResolveMoveDestination(string targetDirectory, string fileName, bool isDirectory, bool hasSidecar)
    {
        Directory.CreateDirectory(targetDirectory);
        targetDirectory = Path.GetFullPath(targetDirectory);
        var candidate = Path.Combine(targetDirectory, fileName);
        if (!File.Exists(candidate) && !Directory.Exists(candidate)
            && (!hasSidecar || !File.Exists(candidate + ".pureasset.yaml")))
            return candidate;
        var stem = isDirectory ? fileName : Path.GetFileNameWithoutExtension(fileName);
        var extension = isDirectory ? "" : Path.GetExtension(fileName);
        for (var number = 2; ; number++)
        {
            var numbered = string.IsNullOrEmpty(extension) ? $"{stem} ({number})" : $"{stem} ({number}){extension}";
            candidate = Path.Combine(targetDirectory, numbered);
            if (!File.Exists(candidate) && !Directory.Exists(candidate)
                && (!hasSidecar || !File.Exists(candidate + ".pureasset.yaml")))
                return candidate;
        }
    }

    private static async Task<string> ImportRemoteFileAsync(IStorageFile source, string targetDirectory)
    {
        await using var read = await source.OpenReadAsync();
        return await ProjectFileImporter.CopyStreamIntoAsync(read, targetDirectory, source.Name);
    }
}
