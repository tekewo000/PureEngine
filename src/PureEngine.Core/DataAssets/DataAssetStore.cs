using System.Reflection;
using System.Runtime.CompilerServices;

namespace PureEngine.Core;

/// <summary>
/// Snapshot of data asset files for one run. Game code receives it through constructor injection
/// and looks assets up by ID or type. Rebuilt on project open, code reload, and each Play run;
/// edits made during a run never leak into files or other runs.
/// </summary>
public sealed class DataAssetStore
{
    private readonly Dictionary<Guid, (Type Type, string TypeId, object Instance)> _entries = [];
    private sealed record Identity(Guid Id);
    private readonly ConditionalWeakTable<object, Identity> _identities = [];
    private readonly Dictionary<Guid, string> _paths = [];

    public DataAssetStore()
    {
    }

    public static bool IsAssetType(Type type) =>
        type.IsDefined(typeof(DataAssetAttribute), inherit: false);

    public bool TryGetId(object instance, out Guid id)
    {
        if (_identities.TryGetValue(instance, out var identity))
        {
            id = identity.Id;
            return true;
        }
        id = Guid.Empty;
        return false;
    }

    public string DisplayName(Guid id) => _paths.GetValueOrDefault(id) ?? id.ToString("D");

    /// <summary>Refreshes an editing snapshot without changing live root identities. Never call during Play.</summary>
    public void Refresh(DataAssetStore source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (ReferenceEquals(source, this)) return;
        foreach (var id in _entries.Keys.Except(source._entries.Keys).ToArray()) _entries.Remove(id);
        _paths.Clear();
        foreach (var (id, path) in source._paths) _paths.Add(id, path);
        foreach (var (id, entry) in source._entries)
        {
            var instance = entry.Instance;
            if (_entries.TryGetValue(id, out var previous) && previous.Type == entry.Type)
            {
                instance = previous.Instance;
                foreach (var member in ComponentSchema.GetInspectorMembers(entry.Type))
                {
                    if (member is FieldInfo field) field.SetValue(instance, field.GetValue(entry.Instance));
                    else if (member is PropertyInfo property) property.SetValue(instance, property.GetValue(entry.Instance));
                }
            }
            _entries[id] = (entry.Type, entry.TypeId, instance);
            _identities.GetValue(instance, _ => new Identity(id));
        }
    }

    public object? Find(Guid id, Type type)
    {
        if (!_entries.TryGetValue(id, out var entry)) return null;
        if (!type.IsInstanceOfType(entry.Instance))
            throw new InvalidDataException($"Asset {id:D} is not a {type.FullName}.");
        return entry.Instance;
    }

    /// <summary>Copies assets for an independent scene/run, retaining shared identity within the copy.</summary>
    public DataAssetStore Clone(ComponentRegistry registry)
    {
        var copy = new DataAssetStore();
        var serializer = new DataAssetSerializer(registry);
        foreach (var (id, entry) in _entries)
        {
            var (instance, _) = serializer.Deserialize(serializer.Serialize(entry.Instance, id));
            copy._entries.Add(id, (instance.GetType(), entry.TypeId, instance));
            copy._identities.Add(instance, new Identity(id));
            copy._paths.Add(id, DisplayName(id));
        }
        return copy;
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
            if (duplicated.Contains(id) || !store._entries.TryAdd(id, (instance.GetType(), typeId, instance)))
            {
                duplicated.Add(id);
                store._entries.Remove(id);
                store._paths.Remove(id);
                problems.Add($"{relative}: duplicate data asset ID {id:D}; excluded from resolution.");
                continue;
            }
            store._identities.Add(instance, new Identity(id));
            store._paths.Add(id, relative);
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