using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace PureEngine.Editor;

public partial class MainWindow
{
    /// <summary>ProjectペインへのOSファイルD&amp;D受付を初期化する。MainWindow()から1回呼ぶ。</summary>
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
    /// ドロップ位置から取り込み先フォルダ（Project相対）を決める。
    /// Tree上のノード／右ペインのフォルダタイル直上ならそのフォルダ、それ以外は表示中フォルダ。
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
            // 空白部へのドロップでフォルダ行を選択中の場合は、選択フォルダを優先する。
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
                throw new IOException($"ローカルのファイルまたはフォルダをドロップしてください: {item.Name}");
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
            // 複数項目の途中で失敗しても、コピー済みの項目を表示する。
            RefreshProjectExplorer();
        }

        if (imported.Count == 0)
        {
            SetFileStatus("取り込むファイルがありませんでした（同一フォルダへのドロップはスキップします）。");
            return;
        }

        _explorerFolder = targetRelative;
        _explorerSelectedFile = imported.LastOrDefault(File.Exists);
        RefreshProjectExplorer();
        var display = string.IsNullOrEmpty(targetRelative) ? "(ルート)" : targetRelative;
        SetFileStatus($"ファイルをインポートしました: {imported.Count}件 -> {display}");
    }

    private static async Task<string> ImportRemoteFileAsync(IStorageFile source, string targetDirectory)
    {
        await using var read = await source.OpenReadAsync();
        return await ProjectFileImporter.CopyStreamIntoAsync(read, targetDirectory, source.Name);
    }
}
