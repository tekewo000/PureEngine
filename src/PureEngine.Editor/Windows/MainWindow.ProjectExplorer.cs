using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using PureEngine.Core;
using Microsoft.CodeAnalysis.CSharp;

namespace PureEngine.Editor;

public enum ProjectExplorerKind
{
    Folder,
    Scene,
    File,
    Component,
}

/// <summary>Project Explorerの右ペインに並ぶ1行。フォルダ・シーンファイル・一般ファイル・コンパイル済みクラスを统一表示する。</summary>
public sealed record ProjectExplorerEntry(
    ProjectExplorerKind Kind,
    string DisplayName,
    string Detail,
    string ToolTip,
    string? RelativePath,
    string? FullPath,
    Type? ComponentType,
    bool IsStartup)
{
    public string KindLabel => Kind switch
    {
        ProjectExplorerKind.Folder => "Folder",
        ProjectExplorerKind.Scene => "Scene",
        ProjectExplorerKind.File => "File",
        _ => "C#",
    };

    /// <summary>Tile frame. Scene reuses structural lavender (same as Startup pill/Engine border/focus ring);
    /// others stay neutral so the grid reads calm.</summary>
    public SolidColorBrush TileBorderBrush => IsScene
        ? new SolidColorBrush(Color.Parse("#B2A0E0"))
        : new SolidColorBrush(Color.Parse("#454545"));

    /// <summary>Icon selectors. Exactly one is true per row; C# files are told apart from plain files by extension.</summary>
    public bool IsFolder => Kind == ProjectExplorerKind.Folder;

    public bool IsScene => Kind == ProjectExplorerKind.Scene;

    public bool IsCSharpFile => (Kind == ProjectExplorerKind.File || Kind == ProjectExplorerKind.Component)
        && FullPath is not null && FullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    public bool IsPlainFile => !IsFolder && !IsScene && !IsCSharpFile;

    public bool HasDetail => !string.IsNullOrEmpty(Detail);
}

public partial class MainWindow
{
    private const string ComponentsNode = "<components>";
    private string _explorerFolder = "Scenes";
    private bool _explorerComponentsSelected;
    private bool _explorerRefreshing;
    private string? _explorerSelectedFile;

    private string? ExplorerSelectedRelativeDirectory =>
        _explorerComponentsSelected ? null : _explorerFolder;

    private bool ExplorerSelectionIsFolder(out string relativeDirectory, out bool isComponents)
    {
        if (ProjectTree.SelectedItem is TreeViewItem node && node.Tag is string tag)
        {
            isComponents = tag == ComponentsNode;
            relativeDirectory = isComponents ? "" : tag;
            return true;
        }
        relativeDirectory = _explorerFolder;
        isComponents = _explorerComponentsSelected;
        return false;
    }

    private void RefreshProjectExplorer()
    {
        CloseProjectMenu.IsEnabled = _project is not null;
        StartupSceneMenu.IsEnabled = _project is not null && !IsPlaying;
        ToolTip.SetTip(ProjectTab, _project is null ? null : $"{_project.Document.Name} — Start: {_project.Document.StartupScene}");
        _explorerRefreshing = true;
        try
        {
            BuildProjectTree();
            RefreshProjectFiles();
        }
        finally
        {
            _explorerRefreshing = false;
        }
    }

    private void BuildProjectTree()
    {
        ProjectTree.Items.Clear();
        if (_project is null)
        {
            _explorerComponentsSelected = false;
            return;
        }
        var root = new TreeViewItem { Header = _project.Document.Name, Tag = "" };
        root.IsExpanded = true;
        ProjectTree.Items.Add(root);
        var nodes = new Dictionary<string, TreeViewItem>(StringComparer.Ordinal) { [""] = root };
        foreach (var directory in _project.ListDirectories())
        {
            var parent = directory.Contains('/')
                ? directory[..directory.LastIndexOf('/')]
                : "";
            if (!nodes.TryGetValue(parent, out var parentNode)) continue;
            var node = new TreeViewItem { Header = Path.GetFileName(directory.Replace('/', Path.DirectorySeparatorChar)), Tag = directory };
            parentNode.Items.Add(node);
            nodes[directory] = node;
        }
        _explorerComponentsSelected = false;
        var target = nodes.ContainsKey(_explorerFolder) ? _explorerFolder : "Scenes";
        nodes.TryGetValue(target, out var selected);
        selected ??= root;
        if (!nodes.ContainsKey(_explorerFolder) && !_explorerComponentsSelected)
            _explorerFolder = nodes.ContainsKey("Scenes") ? "Scenes" : "";
        // 祖先を展開して選択行を見える位置にする。
        for (TreeViewItem? current = selected; current is not null;
             current = current.Parent as TreeViewItem)
            current.IsExpanded = true;
        selected.IsSelected = true;
        _explorerComponentsSelected = selected.Tag is ComponentsNode;
        if (!_explorerComponentsSelected && selected.Tag is string tag) _explorerFolder = tag;
    }

    private void RefreshProjectFiles()
    {
        var entries = new List<ProjectExplorerEntry>();
        if (_project is { } project)
        {
            var folder = _explorerFolder;
            var directory = project.ResolveDirectoryPath(folder);
            if (Directory.Exists(directory))
            {
                var subdirectories = project.ListDirectories()
                    .Where(d => (d.Contains('/') ? d[..d.LastIndexOf('/')] : "") == folder)
                    .Order(StringComparer.Ordinal);
                foreach (var sub in subdirectories)
                {
                    var relative = string.IsNullOrEmpty(folder) ? sub : $"{folder}/{sub[(folder.Length + 1)..]}";
                    entries.Add(new ProjectExplorerEntry(ProjectExplorerKind.Folder,
                        Path.GetFileName(sub.Replace('/', Path.DirectorySeparatorChar)), "", relative,
                        relative, Path.Combine(project.RootDirectory, relative.Replace('/', Path.DirectorySeparatorChar)), null, false));
                }
                var startup = (project.Document.StartupScene ?? "").Replace('\\', '/');
                foreach (var file in project.ListFiles(folder))
                {
                    var relative = string.IsNullOrEmpty(folder) ? file : $"{folder}/{file}";
                    var isScene = file.EndsWith(".pure.scene.yaml", StringComparison.OrdinalIgnoreCase);
                    var isStartup = isScene && string.Equals(relative, startup, StringComparison.Ordinal);
                    var full = Path.Combine(project.RootDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
                    if (!isScene && file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    {
                        // 自作C#はフォルダ構成のまま表示する。専用フォルダへの配置やComponents一覧への集約は要求しない。
                        var types = ComponentAssets.GetTypesForFile(full);
                        var detail = types.Count == 0 ? "C#（アタッチ対象なし／未反映）"
                            : types.Count == 1 ? $"C# {types[0].Name}"
                            : $"C# {types.Count}クラス";
                        var tip = types.Count == 0 ? $"{relative}（アタッチ対象なし）"
                            : $"{relative}: {string.Join(", ", types.Select(t => t.FullName ?? t.Name))}";
                        entries.Add(new ProjectExplorerEntry(
                            ProjectExplorerKind.File, file, detail, tip, relative, full, null, false));
                    }
                    else
                    {
                        entries.Add(new ProjectExplorerEntry(
                            isScene ? ProjectExplorerKind.Scene : ProjectExplorerKind.File,
                            file, "", relative, relative, full, null, isStartup));
                    }
                }
            }
        }
        ProjectFiles.ItemsSource = entries;
        ProjectFilesCount.Text = entries.Count == 0 ? "空のフォルダ" : $"{entries.Count} 件";
        ProjectFiles.SelectedItem = entries.FirstOrDefault(entry =>
            entry.FullPath is not null && string.Equals(entry.FullPath, _explorerSelectedFile, PathComparison()));
        var editPath = _editScene.Path;
        if (ProjectFiles.SelectedItem is null && _explorerSelectedFile is not null
            && editPath is not null && entries.Any(entry => string.Equals(entry.FullPath, editPath, PathComparison())))
            ProjectFiles.SelectedItem = entries.First(entry => string.Equals(entry.FullPath, editPath, PathComparison()));
    }

    private static StringComparison PathComparison() => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private void SelectExplorerNode(string tag)
    {
        foreach (var node in WalkTreeNodes())
            if (node.Tag is string nodeTag && nodeTag == tag)
            {
                node.IsSelected = true;
                if (nodeTag != ComponentsNode) _explorerFolder = nodeTag;
                _explorerComponentsSelected = nodeTag == ComponentsNode;
                return;
            }
    }

    private IEnumerable<TreeViewItem> WalkTreeNodes()
    {
        var stack = new Stack<TreeViewItem>(ProjectTree.Items.OfType<TreeViewItem>().Reverse());
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            foreach (var child in node.Items.OfType<TreeViewItem>().Reverse()) stack.Push(child);
        }
    }

    private void OnProjectTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_explorerRefreshing) return;
        if (ProjectTree.SelectedItem is TreeViewItem node && node.Tag is string tag)
        {
            _explorerComponentsSelected = tag == ComponentsNode;
            if (!_explorerComponentsSelected) _explorerFolder = tag;
            _explorerSelectedFile = null;
            RefreshProjectFiles();
        }
    }

    private void OnProjectTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ProjectTree).Properties.IsRightButtonPressed) return;
        var row = (e.Source as Visual)?.GetSelfAndVisualAncestors()
            .OfType<TreeViewItem>().FirstOrDefault();
        if (row is not null) row.IsSelected = true;
        ProjectTree.Focus();
    }

    private void OnProjectFilesPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ProjectFiles).Properties.IsRightButtonPressed) return;
        var row = (e.Source as Visual)?.GetSelfAndVisualAncestors()
            .OfType<ListBoxItem>().FirstOrDefault();
        if (row is not null) ProjectFiles.SelectedItem = row.DataContext;
        ProjectFiles.Focus();
    }

    private void OnProjectTreeContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var hasProject = _project is not null;
        ExplorerSelectionIsFolder(out var folder, out var isComponents);
        var canWrite = hasProject && !isComponents;
        TreeCreateFolderMenu.IsEnabled = canWrite;
        TreeCreateCSharpMenu.IsEnabled = canWrite && !IsPlaying;
        TreeCreateSceneMenu.IsEnabled = canWrite && _project!.IsUnderScenes(folder);
        var renamable = canWrite && folder != "" && folder != "Scenes";
        TreeRenameMenu.IsEnabled = renamable;
        TreeDeleteMenu.IsEnabled = renamable;
    }

    private void OnProjectFilesContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var entry = ProjectFiles.SelectedItem as ProjectExplorerEntry;
        ExplorerSelectionIsFolder(out var folder, out var isComponents);
        var hasProject = _project is not null;
        FilesOpenMenu.IsEnabled = entry is { Kind: ProjectExplorerKind.Folder or ProjectExplorerKind.Scene };
        FilesStartupMenu.IsEnabled = hasProject && entry is { Kind: ProjectExplorerKind.Scene };
        FilesCreateFolderMenu.IsEnabled = hasProject && !isComponents;
        FilesCreateCSharpMenu.IsEnabled = hasProject && !isComponents && !IsPlaying;
        FilesCreateSceneMenu.IsEnabled = hasProject && !isComponents && _project!.IsUnderScenes(folder);
        FilesRenameMenu.IsEnabled = hasProject && entry is { Kind: ProjectExplorerKind.Folder or ProjectExplorerKind.Scene or ProjectExplorerKind.File };
        FilesDeleteMenu.IsEnabled = hasProject && entry is { Kind: ProjectExplorerKind.Folder or ProjectExplorerKind.Scene or ProjectExplorerKind.File };
    }

    private async void OnProjectFilesDoubleTapped(object? sender, TappedEventArgs e) => await OpenSelectedExplorerEntry();
    private async void OnExplorerOpen(object? sender, RoutedEventArgs e) => await OpenSelectedExplorerEntry();

    private async void OnProjectFilesKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await OpenSelectedExplorerEntry(); }
        else if (e.Key == Key.Delete) { e.Handled = true; await DeleteSelectedExplorerEntry(); }
        else if (e.Key == Key.F2) { e.Handled = true; await RenameSelectedExplorerEntry(); }
    }

    private async Task OpenSelectedExplorerEntry()
    {
        if (IsPlaying)
        {
            SetFileStatus("Play中はシーンを切り替えできません。先にStopしてください。", true);
            return;
        }
        if (ProjectFiles.SelectedItem is not ProjectExplorerEntry entry) return;
        if (entry.Kind == ProjectExplorerKind.Folder && entry.RelativePath is not null)
        {
            _explorerFolder = entry.RelativePath;
            _explorerSelectedFile = null;
            SelectExplorerNode(entry.RelativePath);
            RefreshProjectFiles();
            return;
        }
        if (entry.Kind != ProjectExplorerKind.Scene || entry.FullPath is null || _project is null) return;
        await RunFileOperation(async () =>
        {
            try { await OpenScenePathAsync(entry.FullPath); }
            finally { RefreshProjectExplorer(); }
        });
    }

    /// <summary>作成先フォルダ。右ペインでフォルダ行を選んでいればそれを、なければTreeの選択を使う。</summary>
    private string ExplorerTargetFolder(string componentsFallback)
    {
        if (ProjectFiles.SelectedItem is ProjectExplorerEntry entry
            && entry.Kind == ProjectExplorerKind.Folder && entry.RelativePath is not null)
            return entry.RelativePath;
        ExplorerSelectionIsFolder(out var folder, out var isComponents);
        return isComponents ? componentsFallback : folder;
    }

    /// <summary>Scenesフォルダ自体はProject構造のため改名・削除の対象外にする。</summary>
    private static bool IsStructuralFolder(string? relativePath) =>
        string.Equals(relativePath, "Scenes", StringComparison.Ordinal);

    /// <summary>Scenes配下の選択フォルダに空シーンを新規作成する。編集中シーンは触らない。</summary>
    private async void OnExplorerCreateScene(object? sender, RoutedEventArgs e) => await RunFileOperation(async () =>
    {
        if (_project is null) return;
        var folder = ExplorerTargetFolder("Scenes");
        if (!_project.IsUnderScenes(folder))
        {
            SetFileStatus("シーンはScenesフォルダ内に作成してください。", true);
            return;
        }
        var name = _project.NextSceneName(folder);
        var path = Path.Combine(_project.ResolveDirectoryPath(folder), name);
        SceneFile.Write(path, _sceneSerializer.Serialize(new Scene()));
        _explorerFolder = folder;
        _explorerSelectedFile = path;
        SelectExplorerNode(folder);
        RefreshProjectExplorer();
        SetFileStatus($"シーンを作成しました: {folder}/{name}");
    });

    private async void OnExplorerCreateFolder(object? sender, RoutedEventArgs e) => await RunFileOperation(async () =>
    {
        if (_project is null) return;
        var folder = ExplorerTargetFolder("");
        var name = await AskExplorerName("Create Folder", "新しいフォルダ名", "New Folder");
        if (name is null) return;
        ValidateExplorerFolderName(name);
        var path = Path.Combine(_project.ResolveDirectoryPath(folder), name);
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException("同名のフォルダまたはファイルが既にあります。");
        Directory.CreateDirectory(path);
        var relative = string.IsNullOrEmpty(folder) ? name : $"{folder}/{name}";
        _explorerFolder = relative;
        _explorerSelectedFile = null;
        RefreshProjectExplorer();
        SetFileStatus($"フォルダを作成しました: {relative}");
    });

    private async void OnExplorerCreateCSharp(object? sender, RoutedEventArgs e) => await RunFileOperation(async () =>
    {
        if (_project is null) return;
        var folder = ExplorerTargetFolder("");
        if (ReferenceEquals(sender, TreeCreateCSharpMenu))
            ExplorerSelectionIsFolder(out folder, out _);
        var name = await AskExplorerName("Create C#", "ファイル名（クラス名。.csは省略可）", "NewScript");
        if (name is null) return;
        var className = name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? name[..^3] : name;
        if (!SyntaxFacts.IsValidIdentifier(className)
            || SyntaxFacts.GetKeywordKind(className) != SyntaxKind.None
            || className.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("C#のクラス名として使える名前を入力してください（空白・記号・予約語は使用できません）。");
        var path = Path.Combine(_project.ResolveDirectoryPath(folder), className + ".cs");
        if (File.Exists(path) || Directory.Exists(path))
            throw new IOException("同名のフォルダまたはファイルが既にあります。");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
            writer.Write($"public sealed class {className}{Environment.NewLine}{{{Environment.NewLine}{Environment.NewLine}}}{Environment.NewLine}");
        _explorerFolder = folder;
        _explorerSelectedFile = path;
        RefreshProjectExplorer();
        SetFileStatus($"C#を作成しました: {className}.cs");
    });

    private async void OnExplorerRename(object? sender, RoutedEventArgs e) => await RenameSelectedExplorerEntry();
    private async void OnExplorerDelete(object? sender, RoutedEventArgs e) => await DeleteSelectedExplorerEntry();
    private async void OnExplorerRefresh(object? sender, RoutedEventArgs e) => await RunFileOperation(async () =>
    {
        RefreshProjectExplorer();
        SetFileStatus(_project is null ? "Projectが開かれていません。" : $"更新しました: {_project.Document.Name}");
        await Task.CompletedTask;
    });

    private static void ValidateExplorerFolderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name is "." or ".."
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.')
            || name.Contains('/') || name.Contains('\\'))
            throw new ArgumentException("フォルダ名には有効な名前を指定してください。");
    }

    private async Task RenameSelectedExplorerEntry()
    {
        await RunFileOperation(async () =>
        {
            if (_project is null) return;
            string? oldFull = null, newFull = null;
            var isTreeFolder = false;
            if (ProjectFiles.SelectedItem is ProjectExplorerEntry entry
                && entry is { Kind: ProjectExplorerKind.Folder or ProjectExplorerKind.Scene or ProjectExplorerKind.File })
            {
                oldFull = entry.FullPath!;
            }
            else if (ExplorerSelectionIsFolder(out var folder, out var isComponents) && !isComponents && folder != "")
            {
                oldFull = _project.ResolveDirectoryPath(folder);
                isTreeFolder = true;
            }
            else return;

            var oldName = Path.GetFileName(oldFull!);
            var isScene = oldFull!.EndsWith(".pure.scene.yaml", StringComparison.OrdinalIgnoreCase);
            var isDirectory = Directory.Exists(oldFull);
            var oldRelative = Path.GetRelativePath(_project.RootDirectory, oldFull).Replace('\\', '/');
            if (!isScene && isDirectory && IsStructuralFolder(oldRelative))
            {
                SetFileStatus("Scenesフォルダ自体の名前は変更できません。", true);
                return;
            }
            var name = await AskExplorerName("Rename", $"新しい名前（{oldName}）", oldName);
            if (name is null) return;
            if (isScene && !name.EndsWith(".pure.scene.yaml", StringComparison.OrdinalIgnoreCase))
                name += ".pure.scene.yaml";
            if (name == oldName) return;
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("有効な名前を指定してください。");
            newFull = Path.Combine(Path.GetDirectoryName(oldFull!)!, name);
            if (isScene) _project.ValidateScenePath(newFull);
            else if (isDirectory) _project.ValidateFolderPath(newFull);
            else _project.ValidateFolderPath(Path.GetDirectoryName(newFull)!);
            if (File.Exists(newFull) || Directory.Exists(newFull)) throw new IOException("同名のフォルダまたはファイルが既にあります。");
            if (isDirectory) Directory.Move(oldFull!, newFull);
            else File.Move(oldFull!, newFull);
            RemapSceneReferences(oldFull!, newFull, isDirectory);
            if (isTreeFolder || isDirectory)
            {
                _explorerFolder = Path.GetRelativePath(_project.RootDirectory, newFull).Replace('\\', '/');
                _explorerSelectedFile = null;
            }
            else
            {
                _explorerSelectedFile = newFull;
            }
            RefreshProjectExplorer();
            SetFileStatus($"名前を変更しました: {name}");
        });
    }

    private async Task DeleteSelectedExplorerEntry()
    {
        await RunFileOperation(async () =>
        {
            if (_project is null) return;
            string? target = null;
            var isDirectory = false;
            if (ProjectFiles.SelectedItem is ProjectExplorerEntry entry
                && entry is { Kind: ProjectExplorerKind.Folder or ProjectExplorerKind.Scene or ProjectExplorerKind.File })
            {
                target = entry.FullPath!;
                isDirectory = entry.Kind == ProjectExplorerKind.Folder;
            }
            else if (ExplorerSelectionIsFolder(out var folder, out var isComponents) && !isComponents && folder != "")
            {
                target = _project.ResolveDirectoryPath(folder);
                isDirectory = true;
            }
            else return;

            var startup = _project.StartupScenePath;
            var targetRelative = Path.GetRelativePath(_project.RootDirectory, target).Replace('\\', '/');
            if (IsStructuralFolder(targetRelative))
            {
                SetFileStatus("Scenesフォルダ自体は削除できません。", true);
                return;
            }
            var containsStartup = string.Equals(target, startup, PathComparison())
                || (isDirectory && (startup + Path.DirectorySeparatorChar).StartsWith(target + Path.DirectorySeparatorChar, PathComparison()));
            if (containsStartup)
            {
                SetFileStatus("起動シーンを含むため削除できません。先に起動シーンを変更してください。", true);
                return;
            }
            var editPath = _editScene.Path;
            var containsOpen = editPath is not null && (string.Equals(target, editPath, PathComparison())
                || (isDirectory && (editPath + Path.DirectorySeparatorChar).StartsWith(target + Path.DirectorySeparatorChar, PathComparison())));
            if (containsOpen)
            {
                SetFileStatus("編集中のシーンを含むため削除できません。先に別のシーンを開いてください。", true);
                return;
            }
            var display = Path.GetRelativePath(_project.RootDirectory, target).Replace('\\', '/');
            if (!await ConfirmExplorerDelete(display, isDirectory)) return;
            if (isDirectory) Directory.Delete(target, recursive: true);
            else File.Delete(target);
            if (isDirectory && string.Equals(_explorerFolder, display, StringComparison.Ordinal))
            {
                _explorerFolder = display.Contains('/') ? display[..display.LastIndexOf('/')] : "";
                _explorerSelectedFile = null;
            }
            else if (!isDirectory && string.Equals(_explorerSelectedFile, target, PathComparison()))
            {
                _explorerSelectedFile = null;
            }
            RefreshProjectExplorer();
            SetFileStatus($"削除しました: {display}");
        });
    }

    /// <summary>移動・改名後に編集中シーンと起動シーンの参照を付け替える。起動シーンのScenes外脱出は拒否される。</summary>
    private void RemapSceneReferences(string oldFull, string newFull, bool isDirectory)
    {
        if (_project is null) return;
        var editPath = _editScene.Path;
        if (editPath is not null && (string.Equals(editPath, oldFull, PathComparison())
            || (isDirectory && (editPath + Path.DirectorySeparatorChar).StartsWith(oldFull + Path.DirectorySeparatorChar, PathComparison()))))
        {
            _editScene.SetPath(isDirectory
                ? Path.Combine(newFull, Path.GetRelativePath(oldFull, editPath))
                : newFull);
            UpdateSceneTitle();
        }
        var startup = _project.StartupScenePath;
        if (string.Equals(startup, oldFull, PathComparison())
            || (isDirectory && (startup + Path.DirectorySeparatorChar).StartsWith(oldFull + Path.DirectorySeparatorChar, PathComparison())))
        {
            var remapped = isDirectory
                ? Path.Combine(newFull, Path.GetRelativePath(oldFull, startup))
                : newFull;
            _project.SetStartupScene(remapped);
        }
    }

    private async Task<string?> AskExplorerName(string title, string message, string initial)
    {
        var dialog = new Window
        {
            Title = title, Width = 420, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var name = new TextBox { Text = initial };
        name.SetValue(AutomationProperties.NameProperty, title);
        var ok = new Button { Content = "OK", IsDefault = true };
        ok.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(name.Text)) dialog.Close(name.Text.Trim()); };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => dialog.Close(null);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 12,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                name,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
            },
        };
        dialog.Opened += (_, _) => { name.Focus(); name.SelectAll(); };
        return await dialog.ShowDialog<string?>(this);
    }

    private async Task<bool> ConfirmExplorerDelete(string display, bool isDirectory)
    {
        var dialog = new Window
        {
            Title = "Delete", Width = 420, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var delete = new Button { Content = "Delete", IsDefault = true };
        delete.Click += (_, _) => dialog.Close(true);
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 20,
            Children =
            {
                new TextBlock
                {
                    Text = $"{(isDirectory ? "フォルダ" : "ファイル")}「{display}」を削除しますか？",
                    TextWrapping = TextWrapping.Wrap,
                },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right, Children = { delete, cancel } },
            },
        };
        return await dialog.ShowDialog<bool>(this);
    }

    private Task<IStorageFolder?> ExplorerSaveDirectory()
    {
        if (_project is null) return Task.FromResult<IStorageFolder?>(null);
        var folder = _explorerComponentsSelected ? "Scenes" : _explorerFolder;
        if (!_project.IsUnderScenes(folder)) folder = "Scenes";
        var directory = _project.ResolveDirectoryPath(folder);
        if (!Directory.Exists(directory)) directory = _project.ScenesDirectory;
        return StorageProvider.TryGetFolderFromPathAsync(directory);
    }
}
