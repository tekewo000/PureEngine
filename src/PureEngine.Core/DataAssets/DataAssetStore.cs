namespace PureEngine.Core;

/// <summary>
/// Snapshot of data asset files for one run. Game code receives it through constructor injection
/// and looks assets up by ID or type. Rebuilt on project open, code reload, and each Play run;
/// edits made during a run never leak into files or other runs.
/// </summary>
public sealed class DataAssetStore
{
    private readonly Dictionary<Guid, (Type Type, string TypeId, object Instance)> _entries = [];

    private DataAssetStore()
    {
    }

    /// <summary>Asset IDs in this store.</summary>
    public IReadOnlyList<Guid> Ids => [.. _entries.Keys];

    /// <summary>Loads every .pure.asset.yaml file under the directory. Skips unreadable files with a diagnostic.</summary>
    public static DataAssetStore ScanFolder(string rootDirectory, ComponentRegistry registry, out IReadOnlyList<string> diagnostics)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(registry);
        var store = new DataAssetStore();
        List<string> problems = [];
        if (!Directory.Exists(rootDirectory))
        {
            diagnostics = problems;
            return store;
        }
        string[] files;
        try
        {
            files = [.. Directory.EnumerateFiles(rootDirectory, "*" + DataAssetSerializer.FileExtension, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false,
            }).Order(StringComparer.Ordinal)];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            problems.Add($"Data assets: cannot scan {rootDirectory} ({error.GetBaseException().Message}).");
            diagnostics = problems;
            return store;
        }
        var serializer = new DataAssetSerializer(registry);
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
            object instance;
            Guid id;
            string typeId;
            bool membersChanged;
            try
            {
                (instance, id) = serializer.Deserialize(yaml, out typeId, out membersChanged);
            }
            catch (Exception error)
            {
                problems.Add($"{relative}: invalid data asset ({error.GetBaseException().Message}).");
                continue;
            }
            if (membersChanged)
                problems.Add($"{relative}: fields changed; re-save to clean up old names.");
            if (!store._entries.TryAdd(id, (instance.GetType(), typeId, instance)))
            {
                problems.Add($"{relative}: duplicate data asset ID {id:D}; keeping the first file.");
                continue;
            }
        }
        diagnostics = problems;
        return store;
    }

    /// <summary>Returns the asset with the ID, cast to the expected type.</summary>
    public T Get<T>(Guid id) where T : class
    {
        if (!TryGet(id, out T? value) || value is null)
            throw new InvalidDataException($"Data asset {id:D} is not a {typeof(T).FullName}.");
        return value;
    }

    /// <summary>Tries the asset with the ID. Returns false for missing IDs and type mismatches.</summary>
    public bool TryGet<T>(Guid id, out T? value) where T : class
    {
        value = null;
        if (!_entries.TryGetValue(id, out var entry) || entry.Instance is not T typed) return false;
        value = typed;
        return true;
    }

    /// <summary>All assets assignable to the type, ordered by ID.</summary>
    public IReadOnlyList<T> GetAll<T>() where T : class
    {
        List<(Guid Id, T Value)> found = [];
        foreach (var (id, entry) in _entries)
            if (entry.Instance is T typed) found.Add((id, typed));
        found.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        return found.Select(pair => pair.Value).ToArray();
    }
}