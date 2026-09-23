using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace PureEngine.Editor;

public partial class MainWindow
{
    /// <summary>Initializes OS file drag-and-drop (D&amp;D) handling for the Project pane. Called once from MainWindow().</summary>
    private void InitProjectDrop()
    {
        ProjectTree.AddHandler(DragDrop.DragOverEvent, OnProjectDropDragOver, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        ProjectTree.AddHandler(DragDrop.DropEvent, OnProjectDrop, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        ProjectFiles.AddHandler(DragDrop.DragOverEvent, OnProjectDropDragOver, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        ProjectFiles.AddHandler(DragDrop.DropEvent, OnProjectDrop, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnProjectDropDragOver(object? sender, DragEventArgs e)
    {
        if (_project is null || IsPlaying || IsFileBusy || !e.DataTransfer.Formats.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.None;
        }
        else
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        e.Handled = true;
    }

    private async void OnProjectDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_project is null) return;
        IStorageItem[] items = [.. e.DataTransfer.TryGetFiles() ?? []];
        if (items.Length == 0) return;

        var targetRelative = ResolveProjectDropFolder(sender, e);
        await RunFileOperation(() => ImportDroppedStorageItemsAsync(targetRelative, items));
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

    private static async Task<string> ImportRemoteFileAsync(IStorageFile source, string targetDirectory)
    {
        await using var read = await source.OpenReadAsync();
        return await ProjectFileImporter.CopyStreamIntoAsync(read, targetDirectory, source.Name);
    }
}
