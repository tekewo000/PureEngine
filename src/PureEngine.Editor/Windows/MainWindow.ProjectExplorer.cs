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
using Color = Avalonia.Media.Color;
using Microsoft.CodeAnalysis.CSharp;

namespace PureEngine.Editor;

public partial class MainWindow
{
    private const string ComponentsNode = "<components>";
    private bool _explorerRefreshing;

    private string? ExplorerSelectedRelativeDirectory =>
        ViewModel.Project.ComponentsSelected ? null : ViewModel.Project.Folder;

    private bool ExplorerSelectionIsFolder(out string relativeDirectory, out bool isComponents)
    {
        if (ProjectTree.SelectedItem is TreeViewItem node && node.Tag is string tag)
        {
            isComponents = tag == ComponentsNode;
            relativeDirectory = isComponents ? "" : tag;
            return true;
        }
        relativeDirectory = ViewModel.Project.Folder;
        isComponents = ViewModel.Project.ComponentsSelected;
        return false;
    }

    private void RefreshProjectExplorer()
    {
        ToolTip.SetTip(ProjectTab, Project is null ? null : $"{Project.Document.Name} — Start: {Project.Document.StartupScene}");
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
        ViewModel.Project.RefreshDirectories(Project);
        ProjectTree.Items.Clear();
        if (Project is null)
        {
            ViewModel.Project.ComponentsSelected = false;
            return;
        }
        var root = new TreeViewItem
        {
            Header = Project.Document.Name,
            Tag = "",
            IsExpanded = true
        };
        ProjectTree.Items.Add(root);
        var nodes = new Dictionary<string, TreeViewItem>(StringComparer.Ordinal) { [""] = root };
        foreach (var directory in ViewModel.Project.Directories)
        {
            var parent = directory.Contains('/')
                ? directory[..directory.LastIndexOf('/')]
                : "";
            if (!nodes.TryGetValue(parent, out var parentNode)) continue;
            var node = new TreeViewItem { Header = Path.GetFileName(directory.Replace('/', Path.DirectorySeparatorChar)), Tag = directory };
            parentNode.Items.Add(node);
            nodes[directory] = node;
        }
        ViewModel.Project.ComponentsSelected = false;
        var target = nodes.ContainsKey(ViewModel.Project.Folder) ? ViewModel.Project.Folder : "Scenes";
        nodes.TryGetValue(target, out var selected);
        selected ??= root;
        if (!nodes.ContainsKey(ViewModel.Project.Folder) && !ViewModel.Project.ComponentsSelected)
            ViewModel.Project.Folder = nodes.ContainsKey("Scenes") ? "Scenes" : "";
        // Expands ancestors to bring the selected row into view.
        for (var current = selected; current is not null;
             current = current.Parent as TreeViewItem)
            current.IsExpanded = true;
        selected.IsSelected = true;
        ViewModel.Project.ComponentsSelected = selected.Tag is ComponentsNode;
        if (!ViewModel.Project.ComponentsSelected && selected.Tag is string tag) ViewModel.Project.Folder = tag;
    }

    private void RefreshProjectFiles() =>
        ViewModel.Project.RefreshFiles(Project, Components, Documents.Scene.Path);

    private static StringComparison PathComparison() => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private void SelectExplorerNode(string tag)
    {
        foreach (var node in WalkTreeNodes())
            if (node.Tag is string nodeTag && nodeTag == tag)
            {
                node.IsSelected = true;
                if (nodeTag != ComponentsNode) ViewModel.Project.Folder = nodeTag;
                ViewModel.Project.ComponentsSelected = nodeTag == ComponentsNode;
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
            ViewModel.Project.ComponentsSelected = tag == ComponentsNode;
            if (!ViewModel.Project.ComponentsSelected) ViewModel.Project.Folder = tag;
            ViewModel.Project.SelectedFile = null;
            RefreshProjectFiles();
        }
    }

    private void OnProjectTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ProjectTree).Properties.IsRightButtonPressed) return;
        var row = (e.Source as Visual)?.GetSelfAndVisualAncestors()
            .OfType<TreeViewItem>().FirstOrDefault();
        row?.IsSelected = true;
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
        var hasProject = Project is not null;
        ExplorerSelectionIsFolder(out var folder, out var isComponents);
        var canWrite = hasProject && !isComponents;
        TreeCreateFolderMenu.IsEnabled = canWrite;
        TreeCreateCSharpMenu.IsEnabled = canWrite && !IsPlaying;
        TreeCreateSceneMenu.IsEnabled = canWrite && Project!.IsUnderScenes(folder);
        RefreshDataAssetMenu(TreeCreateDataAssetMenu, canWrite && !IsPlaying);
        var renamable = canWrite && folder != "" && folder != "Scenes";
        TreeRenameMenu.IsEnabled = renamable;
        TreeDeleteMenu.IsEnabled = renamable;
    }

    private void OnProjectFilesContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var entry = ProjectFiles.SelectedItem as ProjectExplorerEntry;
        ExplorerSelectionIsFolder(out var folder, out var isComponents);
        var hasProject = Project is not null;
        FilesOpenMenu.IsEnabled = entry is { Kind: ProjectExplorerKind.Folder or ProjectExplorerKind.Scene or ProjectExplorerKind.DataAsset }
            || entry is { Kind: ProjectExplorerKind.Prefab } && !IsPlaying
            || entry is { IsCSharpFile: true };
        FilesPlaceMenu.IsEnabled = entry is { Kind: ProjectExplorerKind.Prefab } && !IsPlaying;
        FilesStartupMenu.IsEnabled = hasProject && entry is { Kind: ProjectExplorerKind.Scene };
        FilesCreateFolderMenu.IsEnabled = hasProject && !isComponents;
        FilesCreateCSharpMenu.IsEnabled = hasProject && !isComponents && !IsPlaying;
        FilesCreateSceneMenu.IsEnabled = hasProject && !isComponents && Project!.IsUnderScenes(folder);
        RefreshDataAssetMenu(FilesCreateDataAssetMenu, hasProject && !isComponents && !IsPlaying);
        FilesRenameMenu.IsEnabled = hasProject && entry is { Kind: ProjectExplorerKind.Folder or ProjectExplorerKind.Scene or ProjectExplorerKind.File or ProjectExplorerKind.DataAsset or ProjectExplorerKind.Prefab };
        FilesDeleteMenu.IsEnabled = hasProject && entry is { Kind: ProjectExplorerKind.Folder or ProjectExplorerKind.Scene or ProjectExplorerKind.File or ProjectExplorerKind.DataAsset or ProjectExplorerKind.Prefab };
    }

    private async void OnProjectFilesDoubleTapped(object? sender, TappedEventArgs e) => await OpenSelectedExplorerEntry();
    private async void OnExplorerOpen(object? sender, RoutedEventArgs e) => await OpenSelectedExplorerEntry();

    /// <summary>Opens a data asset in the Inspector on selection. Scene files still need a double-click to switch.</summary>
    private async void OnProjectFilesSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ViewModel.Compilation.IsApplying) return;
        if (ProjectFiles.SelectedItem is not ProjectExplorerEntry entry) return;
        if (entry.Kind != ProjectExplorerKind.DataAsset || entry.FullPath is null) return;
        await OpenDataAssetForEdit(entry.FullPath);
    }

    private async void OnProjectFilesKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await OpenSelectedExplorerEntry(); }
        else if (e.Key == Key.Delete) { e.Handled = true; await DeleteSelectedExplorerEntry(); }
        else if (e.Key == Key.F2) { e.Handled = true; await RenameSelectedExplorerEntry(); }
    }

    private async Task OpenSelectedExplorerEntry()
    {
        if (ProjectFiles.SelectedItem is not ProjectExplorerEntry entry) return;
        if (entry.Kind == ProjectExplorerKind.Prefab && entry.FullPath is not null)
        {
            await OpenPrefabEditorAsync(entry.FullPath);
            return;
        }
        if (entry.IsCSharpFile && entry.FullPath is not null)
        {
            OpenCSharpInZed(entry.FullPath);
            return;
        }
        if (entry.FullPath is not null && ProjectFile.IsLocalizationFileName(entry.FullPath))
        {
            if (RejectWhenPlaying("Open localization")) return;
            ActivateEditorViewport(LocalizationViewportIndex);
            return;
        }
        if (IsPlaying)
        {
            SetFileStatus("Cannot switch scenes while playing. Stop first.", true);
            return;
        }
        if (entry.Kind == ProjectExplorerKind.Folder && entry.RelativePath is not null)
        {
            ViewModel.Project.Folder = entry.RelativePath;
            ViewModel.Project.SelectedFile = null;
            SelectExplorerNode(entry.RelativePath);
            RefreshProjectFiles();
            return;
        }
        if (entry.Kind == ProjectExplorerKind.DataAsset && entry.FullPath is not null)
        {
            await OpenDataAssetForEdit(entry.FullPath);
            return;
        }
        if (entry.Kind != ProjectExplorerKind.Scene || entry.FullPath is null || Project is null) return;
        await RunFileOperation(async () =>
        {
            try { await OpenScenePathAsync(entry.FullPath); }
            finally { RefreshProjectExplorer(); }
        });
    }

    /// <summary>Opens a C# file in Zed from the project root without blocking scene switching or Play.</summary>
    private void OpenCSharpInZed(string fullPath)
    {
        if (ExternalEditor.TryOpenCSharpInZed(fullPath, Project?.RootDirectory, out var error))
        {
            SetFileStatus($"Opened in Zed: {Path.GetFileName(fullPath)}");
            return;
        }
        var message = error ?? "Could not open in Zed.";
        SetFileStatus(message, true);
        Log.Engine.Warning(message);
    }

    /// <summary>Destination folder for creation. Uses the right-pane folder row when selected, otherwise the Tree selection.</summary>
    private string ExplorerTargetFolder(string componentsFallback)
    {
        if (ProjectFiles.SelectedItem is ProjectExplorerEntry entry
            && entry.Kind == ProjectExplorerKind.Folder && entry.RelativePath is not null)
            return entry.RelativePath;
        ExplorerSelectionIsFolder(out var folder, out var isComponents);
        return isComponents ? componentsFallback : folder;
    }

    /// <summary>Excludes the Scenes folder itself from rename and delete because it is part of the project structure.</summary>
    private static bool IsStructuralFolder(string? relativePath) =>
        string.Equals(relativePath, "Scenes", StringComparison.Ordinal);

    /// <summary>Creates an empty scene in the selected folder under Scenes. Leaves the scene being edited untouched.</summary>
    private async void OnExplorerCreateScene(object? sender, RoutedEventArgs e) => await RunFileOperation(async () =>
    {
        if (Project is null) return;
        var folder = ExplorerTargetFolder("Scenes");
        if (!Project.IsUnderScenes(folder))
        {
            SetFileStatus("Create scenes inside the Scenes folder.", true);
            return;
        }
        var name = Project.NextSceneName(folder);
        var path = Path.Combine(Project.ResolveDirectoryPath(folder), name);
        SceneFile.Write(path, SceneSerializer.Serialize(new Scene()));
        ViewModel.Project.Folder = folder;
        ViewModel.Project.SelectedFile = path;
        SelectExplorerNode(folder);
        RefreshProjectExplorer();
        SetFileStatus($"Created scene: {folder}/{name}");
    });

    private async void OnExplorerCreateFolder(object? sender, RoutedEventArgs e) => await RunFileOperation(async () =>
    {
        if (Project is null) return;
        var folder = ExplorerTargetFolder("");
        var name = await AskExplorerName("Create Folder", "New folder name", "New Folder");
        if (name is null) return;
        ValidateExplorerFolderName(name);
        var path = Path.Combine(Project.ResolveDirectoryPath(folder), name);
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException("A folder or file with the same name already exists.");
        Directory.CreateDirectory(path);
        var relative = string.IsNullOrEmpty(folder) ? name : $"{folder}/{name}";
        ViewModel.Project.Folder = relative;
        ViewModel.Project.SelectedFile = null;
        RefreshProjectExplorer();
        SetFileStatus($"Created folder: {relative}");
    });

    private async void OnExplorerCreateCSharp(object? sender, RoutedEventArgs e) => await RunFileOperation(async () =>
    {
        if (Project is null) return;
        var folder = ExplorerTargetFolder("");
        if (ReferenceEquals(sender, TreeCreateCSharpMenu))
            ExplorerSelectionIsFolder(out folder, out _);
        var name = await AskExplorerName("Create C#", "File name (class name, .cs optional)", "NewScript");
        if (name is null) return;
        var className = name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? name[..^3] : name;
        if (!SyntaxFacts.IsValidIdentifier(className)
            || SyntaxFacts.GetKeywordKind(className) != SyntaxKind.None
            || className.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Enter a valid C# class name (no spaces, symbols, or reserved words).");
        var path = Path.Combine(Project.ResolveDirectoryPath(folder), className + ".cs");
        if (File.Exists(path) || Directory.Exists(path))
            throw new IOException("A folder or file with the same name already exists.");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
            writer.Write($"public sealed class {className}{Environment.NewLine}{{{Environment.NewLine}{Environment.NewLine}}}{Environment.NewLine}");
        ViewModel.Project.Folder = folder;
        ViewModel.Project.SelectedFile = path;
        RefreshProjectExplorer();
        SetFileStatus($"Created C#: {className}.cs");
    });

    /// <summary>Rebuilds the Create Data Asset submenu from registered [DataAsset] types. Shows diagnostics when a type is unusable.</summary>
    private void RefreshDataAssetMenu(MenuItem menu, bool enabled)
    {
        menu.IsEnabled = enabled;
        menu.Items.Clear();
        if (!enabled) return;
        var descriptors = DataAssetDescriptor.DescribeAll(Components.Registry, out var diagnostics, Components.DataAssetTypes);
        foreach (var problem in diagnostics)
            menu.Items.Add(new MenuItem { Header = $"Invalid: {problem}", IsEnabled = false });
        if (descriptors.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "No data assets (add [DataAsset] to a class)", IsEnabled = false });
            return;
        }
        var folders = new Dictionary<string, MenuItem>(StringComparer.Ordinal);
        foreach (var descriptor in descriptors.OrderBy(d => d.MenuPath, StringComparer.Ordinal))
        {
            var parts = descriptor.MenuPath.Split('/');
            var parent = menu;
            var prefix = "";
            for (var i = 0; i < parts.Length - 1; i++)
            {
                prefix = prefix.Length == 0 ? parts[i] : $"{prefix}/{parts[i]}";
                if (!folders.TryGetValue(prefix, out var folder))
                {
                    folder = new MenuItem { Header = parts[i] };
                    folders.Add(prefix, folder);
                    parent.Items.Add(folder);
                }
                parent = folder;
            }
            var leaf = new MenuItem
            {
                Header = parts[^1],
                Tag = (descriptor.TypeId, ReferenceEquals(menu, TreeCreateDataAssetMenu)),
            };
            leaf.Click += OnExplorerCreateDataAsset;
            parent.Items.Add(leaf);
        }
    }

    /// <summary>Creates a data asset file of the menu-selected type in the target folder. Leaves the scene being edited untouched.</summary>
    private async void OnExplorerCreateDataAsset(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: ValueTuple<string, bool> selection }) return;
        await RunFileOperation(async () =>
        {
            if (Project is null || RejectWhenPlaying("Create Data Asset")) return;
            var type = Components.Registry.GetType(selection.Item1);
            if (!DataAssetDescriptor.TryCreate(type, Components.Registry, out var descriptor, out var error) || descriptor is null)
            {
                SetFileStatus(error ?? $"{type.FullName}: invalid data asset type.", true);
                return;
            }
            var folder = ExplorerTargetFolder("");
            if (selection.Item2) ExplorerSelectionIsFolder(out folder, out _);
            var name = Project.NextDataAssetName(folder, descriptor.DisplayName);
            var path = Path.Combine(Project.ResolveDirectoryPath(folder), name);
            Project.ValidateDataAssetPath(path);
            if (File.Exists(path) || Directory.Exists(path))
                throw new IOException("A folder or file with the same name already exists.");
            DataAssetFile.Create(path, type, Components.Registry);
            ViewModel.Project.Folder = folder;
            ViewModel.Project.SelectedFile = path;
            RefreshProjectExplorer();
            RescanTableRows();
            SetFileStatus($"Created data asset: {folder}/{name}");
            await Task.CompletedTask;
        });
    }

    private async void OnExplorerRename(object? sender, RoutedEventArgs e) => await RenameSelectedExplorerEntry();
    private async void OnExplorerDelete(object? sender, RoutedEventArgs e) => await DeleteSelectedExplorerEntry();
    private async void OnExplorerRefresh(object? sender, RoutedEventArgs e) => await RunFileOperation(async () =>
    {
        RefreshProjectAssets();
        RefreshProjectExplorer();
        RefreshComponents();
        if (await ConfirmTableRowsClose(closeOnConfirm: false)) RescanTableRows();
        await RescanLocalizationRows();
        SetFileStatus(Project is null ? "No project is open." : $"Refreshed: {Project.Document.Name}");
        await Task.CompletedTask;
    });

    private static void ValidateExplorerFolderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name is "." or ".."
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.')
            || name.Contains('/') || name.Contains('\\'))
            throw new ArgumentException("Specify a valid folder name.");
    }

    private async Task RenameSelectedExplorerEntry() =>
        await RunFileOperation(async () =>
        {
            if (Project is null) return;
            string? oldFull = null, newFull = null;
            var isTreeFolder = false;
            if (ProjectFiles.SelectedItem is ProjectExplorerEntry entry
                && entry is { Kind: ProjectExplorerKind.Folder or ProjectExplorerKind.Scene or ProjectExplorerKind.File or ProjectExplorerKind.DataAsset or ProjectExplorerKind.Prefab })
            {
                oldFull = entry.FullPath!;
            }
            else if (ExplorerSelectionIsFolder(out var folder, out var isComponents) && !isComponents && folder != "")
            {
                oldFull = Project.ResolveDirectoryPath(folder);
                isTreeFolder = true;
            }
            else return;

            var oldName = Path.GetFileName(oldFull!);
            var isScene = oldFull!.EndsWith(".pure.scene.yaml", StringComparison.OrdinalIgnoreCase);
            var isDirectory = Directory.Exists(oldFull);
            var oldRelative = Path.GetRelativePath(Project.RootDirectory, oldFull).Replace('\\', '/');
            if (!isScene && isDirectory && IsStructuralFolder(oldRelative))
            {
                SetFileStatus("Cannot rename the Scenes folder itself.", true);
                return;
            }
            var name = await AskExplorerName("Rename", $"New name ({oldName})", oldName);
            if (name is null) return;
            if (isScene && !name.EndsWith(".pure.scene.yaml", StringComparison.OrdinalIgnoreCase))
                name += ".pure.scene.yaml";
            if (!isScene && !isDirectory && ProjectFile.IsDataAssetFileName(oldFull!)
                && !ProjectFile.IsDataAssetFileName(name))
                name += ".pure.asset.yaml";
            if (!isScene && !isDirectory && ProjectFile.IsPrefabFileName(oldFull!)
                && !ProjectFile.IsPrefabFileName(name))
                name += ".pure.prefab.yaml";
            if (name == oldName) return;
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("Specify a valid name.");
            newFull = Path.Combine(Path.GetDirectoryName(oldFull!)!, name);
            if (isScene) Project.ValidateScenePath(newFull);
            else if (isDirectory) Project.ValidateFolderPath(newFull);
            else Project.ValidateFolderPath(Path.GetDirectoryName(newFull)!);
            if (File.Exists(newFull) || Directory.Exists(newFull)) throw new IOException("A folder or file with the same name already exists.");
            if (ContainsOpenDataAsset(oldFull!) && !await ConfirmCloseDataAsset()) return;
            if (ContainsOpenTable(oldFull!) && !await ConfirmCloseTableRows()) return;
            if (ContainsOpenLocalization(oldFull!) && !await ConfirmLocalizationClose()) return;
            if (ContainsOpenPrefab(oldFull!, isDirectory) && !await ConfirmClosePrefabEditor()) return;
            if (isDirectory) Directory.Move(oldFull!, newFull);
            else File.Move(oldFull!, newFull);
            RemapSceneReferences(oldFull!, newFull, isDirectory);
            if (isTreeFolder || isDirectory)
            {
                ViewModel.Project.Folder = Path.GetRelativePath(Project.RootDirectory, newFull).Replace('\\', '/');
                ViewModel.Project.SelectedFile = null;
            }
            else
            {
                ViewModel.Project.SelectedFile = newFull;
            }
            RefreshProjectExplorer();
            RescanTableRows();
            await RescanLocalizationRows();
            SetFileStatus($"Renamed to: {name}");
        });

    private async Task DeleteSelectedExplorerEntry() =>
        await RunFileOperation(async () =>
        {
            if (Project is null) return;
            string? target = null;
            var isDirectory = false;
            if (ProjectFiles.SelectedItem is ProjectExplorerEntry entry
                && entry is { Kind: ProjectExplorerKind.Folder or ProjectExplorerKind.Scene or ProjectExplorerKind.File or ProjectExplorerKind.DataAsset or ProjectExplorerKind.Prefab })
            {
                target = entry.FullPath!;
                isDirectory = entry.Kind == ProjectExplorerKind.Folder;
            }
            else if (ExplorerSelectionIsFolder(out var folder, out var isComponents) && !isComponents && folder != "")
            {
                target = Project.ResolveDirectoryPath(folder);
                isDirectory = true;
            }
            else return;

            if (ContainsOpenDataAsset(target) && !await ConfirmCloseDataAsset()) return;
            if (ContainsOpenTable(target) && !await ConfirmCloseTableRows()) return;
            if (ContainsOpenLocalization(target) && !await ConfirmLocalizationClose()) return;
            var startup = Project.StartupScenePath;
            var targetRelative = Path.GetRelativePath(Project.RootDirectory, target).Replace('\\', '/');
            if (IsStructuralFolder(targetRelative))
            {
                SetFileStatus("Cannot delete the Scenes folder itself.", true);
                return;
            }
            var containsStartup = string.Equals(target, startup, PathComparison())
                || (isDirectory && (startup + Path.DirectorySeparatorChar).StartsWith(target + Path.DirectorySeparatorChar, PathComparison()));
            if (containsStartup)
            {
                SetFileStatus("Cannot delete because it contains the startup scene. Change the startup scene first.", true);
                return;
            }
            var editPath = Documents.Scene.Path;
            var containsOpen = editPath is not null && (string.Equals(target, editPath, PathComparison())
                || (isDirectory && (editPath + Path.DirectorySeparatorChar).StartsWith(target + Path.DirectorySeparatorChar, PathComparison())));
            if (containsOpen)
            {
                SetFileStatus("Cannot delete because it contains the open scene. Open another scene first.", true);
                return;
            }
            var display = Path.GetRelativePath(Project.RootDirectory, target).Replace('\\', '/');
            if (!await ConfirmExplorerDelete(display, isDirectory)) return;
            if (ContainsOpenPrefab(target, isDirectory) && !await ConfirmClosePrefabEditor()) return;
            if (isDirectory) Directory.Delete(target, recursive: true);
            else File.Delete(target);
            if (isDirectory && string.Equals(ViewModel.Project.Folder, display, StringComparison.Ordinal))
            {
                ViewModel.Project.Folder = display.Contains('/') ? display[..display.LastIndexOf('/')] : "";
                ViewModel.Project.SelectedFile = null;
            }
            else if (!isDirectory && string.Equals(ViewModel.Project.SelectedFile, target, PathComparison()))
            {
                ViewModel.Project.SelectedFile = null;
            }
            RefreshProjectExplorer();
            RescanTableRows();
            await RescanLocalizationRows();
            SetFileStatus($"Deleted: {display}");
        });

    private bool ContainsOpenPrefab(string path, bool isDirectory) => Documents.Prefab?.Path is { } prefabPath
        && (string.Equals(prefabPath, path, PathComparison())
            || isDirectory && prefabPath.StartsWith(
                Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar, PathComparison()));

    /// <summary>Repoints the edited-scene and startup-scene references after a move or rename. Rejects moves that take the startup scene outside Scenes.</summary>
    private void RemapSceneReferences(string oldFull, string newFull, bool isDirectory)
    {
        if (Project is null) return;
        var editPath = Documents.Scene.Path;
        if (editPath is not null && (string.Equals(editPath, oldFull, PathComparison())
            || (isDirectory && (editPath + Path.DirectorySeparatorChar).StartsWith(oldFull + Path.DirectorySeparatorChar, PathComparison()))))
        {
            Documents.Scene.SetPath(isDirectory
                ? Path.Combine(newFull, Path.GetRelativePath(oldFull, editPath))
                : newFull);
            UpdateSceneTitle();
        }
        var startup = Project.StartupScenePath;
        if (string.Equals(startup, oldFull, PathComparison())
            || (isDirectory && (startup + Path.DirectorySeparatorChar).StartsWith(oldFull + Path.DirectorySeparatorChar, PathComparison())))
        {
            var remapped = isDirectory
                ? Path.Combine(newFull, Path.GetRelativePath(oldFull, startup))
                : newFull;
            Project.SetStartupScene(remapped);
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
        var ok = new Avalonia.Controls.Button { Content = "OK", IsDefault = true };
        ok.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(name.Text)) dialog.Close(name.Text.Trim()); };
        var cancel = new Avalonia.Controls.Button { Content = "Cancel", IsCancel = true };
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
        var delete = new Avalonia.Controls.Button { Content = "Delete", IsDefault = true };
        delete.Click += (_, _) => dialog.Close(true);
        var cancel = new Avalonia.Controls.Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 20,
            Children =
            {
                new TextBlock
                {
                    Text = $"Delete {(isDirectory ? "folder" : "file")} \"{display}\"?",
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
        if (Project is null) return Task.FromResult<IStorageFolder?>(null);
        var folder = ViewModel.Project.ComponentsSelected ? "Scenes" : ViewModel.Project.Folder;
        if (!Project.IsUnderScenes(folder)) folder = "Scenes";
        var directory = Project.ResolveDirectoryPath(folder);
        if (!Directory.Exists(directory)) directory = Project.ScenesDirectory;
        return StorageProvider.TryGetFolderFromPathAsync(directory);
    }
}
