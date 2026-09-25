namespace PureEngine.Core;

/// <summary>
/// Prefab files available to one run. Game code looks prefabs up by prefab ID through
/// <see cref="PrefabSpawner"/>. Rebuilt on project open and each Play run;
/// edits made during a run never leak into files or other runs.
/// </summary>
public sealed class PrefabCatalog
{
    private readonly Dictionary<Guid, (string Path, PrefabDocument Document)> _entries = [];

    public static PrefabCatalog Empty => new();

    /// <summary>Prefab IDs in this catalog.</summary>
    public IReadOnlyList<Guid> Ids => [.. _entries.Keys];

    public string DisplayName(Guid id) => _entries.GetValueOrDefault(id).Path ?? id.ToString("D");

    public PrefabDocument? Find(Guid id) => _entries.TryGetValue(id, out var entry) ? entry.Document : null;

    internal void Add(PrefabDocument document) => _entries[document.Id] = (DisplayName(document.Id), document);

    internal PrefabCatalog Copy()
    {
        var copy = new PrefabCatalog();
        foreach (var (id, entry) in _entries) copy._entries.Add(id, entry);
        return copy;
    }

    /// <summary>Loads every .pure.prefab.yaml file under the directory. Skips unreadable files with a diagnostic.</summary>
    public static PrefabCatalog ScanFolder(string rootDirectory, out IReadOnlyList<string> diagnostics)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var catalog = new PrefabCatalog();
        List<string> problems = [];
        if (!Directory.Exists(rootDirectory))
        {
            diagnostics = problems;
            return catalog;
        }
        string[] files;
        try
        {
            files = [.. Directory.EnumerateFiles(rootDirectory, "*" + PrefabSerializer.FileExtension, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false,
            }).Order(StringComparer.Ordinal)];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            problems.Add($"Prefabs: cannot scan {rootDirectory} ({error.GetBaseException().Message}).");
            diagnostics = problems;
            return catalog;
        }
        var duplicated = new HashSet<Guid>();
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(rootDirectory, file).Replace('\\', '/');
            string yaml;
            try
            {
                yaml = File.ReadAllText(file);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{relative}: cannot read ({error.GetBaseException().Message}).");
                continue;
            }
            PrefabDocument document;
            try
            {
                document = PrefabSerializer.Parse(yaml);
            }
            catch (Exception error)
            {
                problems.Add($"{relative}: invalid prefab ({error.GetBaseException().Message}).");
                continue;
            }
            if (duplicated.Contains(document.Id) || !catalog._entries.TryAdd(document.Id, (relative, document)))
            {
                duplicated.Add(document.Id);
                catalog._entries.Remove(document.Id);
                problems.Add($"{relative}: duplicate prefab ID {document.Id:D}; excluded from resolution.");
                continue;
            }
        }
        diagnostics = problems;
        return catalog;
    }
}
