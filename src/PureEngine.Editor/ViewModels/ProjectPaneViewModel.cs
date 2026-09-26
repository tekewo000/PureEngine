using PureEngine.Core;

namespace PureEngine.Editor;

/// <summary>Project navigation and file presentation, independent of tree and tile controls.</summary>
public sealed class ProjectPaneViewModel : EditorObservable
{
    public string Folder { get; set => SetProperty(ref field, value); } = "Scenes";
    public string? SelectedFile { get; set => SetProperty(ref field, value); }
    public bool ComponentsSelected { get; set => SetProperty(ref field, value); }
    public ProjectExplorerEntry? SelectedEntry { get; set => SetProperty(ref field, value); }
    public IReadOnlyList<ProjectExplorerEntry> Files { get; private set; } = [];
    public IReadOnlyList<string> Directories { get; private set; } = [];
    public string FileCountText => Files.Count == 0 ? "Empty folder" : $"{Files.Count} item(s)";

    public void Clear()
    {
        Files = [];
        Directories = [];
        SelectedEntry = null;
        SelectedFile = null;
        Changed(nameof(Files));
        Changed(nameof(Directories));
        Changed(nameof(FileCountText));
    }

    public void RefreshDirectories(ProjectFile? project)
    {
        Directories = project?.ListDirectories() ?? [];
        Changed(nameof(Directories));
    }

    public void RefreshFiles(ProjectFile? currentProject, ProjectComponents components, string? editPath)
    {
        var entries = new List<ProjectExplorerEntry>();
        if (currentProject is { } project)
        {
            var folder = Folder;
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
                    if (file.EndsWith(".pureasset.yaml", StringComparison.OrdinalIgnoreCase)) continue;
                    var relative = string.IsNullOrEmpty(folder) ? file : $"{folder}/{file}";
                    var isScene = file.EndsWith(".pure.scene.yaml", StringComparison.OrdinalIgnoreCase);
                    var isDataAsset = !isScene && ProjectFile.IsDataAssetFileName(file);
                    var isPrefab = !isScene && !isDataAsset && ProjectFile.IsPrefabFileName(file);
                    var isStartup = isScene && string.Equals(relative, startup, StringComparison.Ordinal);
                    var full = Path.Combine(project.RootDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
                    if (isDataAsset)
                    {
                        entries.Add(new ProjectExplorerEntry(
                            ProjectExplorerKind.DataAsset, file, "Data Asset", relative, relative, full, null, false));
                    }
                    else if (isPrefab)
                    {
                        entries.Add(new ProjectExplorerEntry(
                            ProjectExplorerKind.Prefab, file, "Prefab", relative, relative, full, null, false));
                    }
                    else if (!isScene && file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    {
                        // Shows custom C# files in their folder layout. Does not require a dedicated folder or aggregation into the Components list.
                        var types = components.GetTypesForFile(full);
                        var detail = types.Count == 0 ? "C# (no attachable types)"
                            : types.Count == 1 ? $"C# {types[0].Name}"
                            : $"C# ({types.Count} classes)";
                        var tip = types.Count == 0 ? $"{relative} (no attachable types)"
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
        Files = entries;
        Changed(nameof(Files));
        Changed(nameof(FileCountText));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        SelectedEntry = entries.FirstOrDefault(entry =>
            entry.FullPath is not null && string.Equals(entry.FullPath, SelectedFile, comparison));
        if (SelectedEntry is null && SelectedFile is not null && editPath is not null)
            SelectedEntry = entries.FirstOrDefault(entry => string.Equals(entry.FullPath, editPath, comparison));
    }
}
