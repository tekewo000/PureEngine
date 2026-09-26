using PureEngine.Core;

namespace PureEngine.Editor;

/// <summary>Owns editable table documents and validates all rows before publishing saves or code changes.</summary>
public sealed class DataAssetTableDocument
{
    public DataAssetDescriptor? Type { get; set; }
    public List<DataAssetEditState> Rows { get; } = [];
    public HashSet<object> Owned { get; } = [with(ReferenceEqualityComparer.Instance)];
    public bool IsDirty => Rows.Any(row => row.Dirty);

    public void Clear()
    {
        Rows.Clear();
        Owned.Clear();
    }

    public string? Scan(ProjectFile project, ComponentRegistry registry)
    {
        Clear();
        var descriptor = Type;
        if (descriptor is null) return null;
        var serializer = new DataAssetSerializer(registry);
        var root = project.RootDirectory;
        string[] files;
        try
        {
            files = [.. Directory.EnumerateFiles(root, "*" + DataAssetSerializer.FileExtension, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false,
            }).Order(StringComparer.Ordinal)];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Engine.Warning($"Data asset table: cannot scan {root} ({error.GetBaseException().Message}).");
            return $"Cannot scan data assets: {error.GetBaseException().Message}";
        }
        var byId = new Dictionary<Guid, DataAssetEditState>();
        var duplicated = new HashSet<Guid>();
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            string yaml;
            try { yaml = File.ReadAllText(file); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Log.Engine.Warning($"{relative}: cannot read ({error.GetBaseException().Message}).");
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
                Log.Engine.Warning($"{relative}: invalid data asset ({error.GetBaseException().Message}).");
                continue;
            }
            if (typeId != descriptor.TypeId) continue;
            var full = Path.GetFullPath(file);
            if (duplicated.Contains(id) || !byId.TryAdd(id, new DataAssetEditState(full, instance, id, typeId)
                { Dirty = membersChanged }))
            {
                duplicated.Add(id);
                byId.Remove(id);
                Log.Engine.Warning($"{relative}: duplicate data asset ID {id:D}; excluded from the table.");
            }
        }
        Rows.AddRange(byId.Values.OrderBy(row =>
            Path.GetRelativePath(root, row.Path).Replace('\\', '/'), StringComparer.Ordinal));
        RefreshOwnership();
        return null;
    }

    public void RefreshOwnership()
    {
        Owned.Clear();
        foreach (var row in Rows) DataAssetEditState.CollectObjects(row.Instance, Owned);
    }

    public DataAssetEditState Add(ProjectFile project, ComponentRegistry registry)
    {
        var descriptor = Type ?? throw new InvalidOperationException("Select a data asset type first.");
        var folder = Rows.Count == 0 ? "Assets"
            : Path.GetRelativePath(project.RootDirectory, Path.GetDirectoryName(Rows[0].Path)!).Replace('\\', '/');
        Directory.CreateDirectory(project.ResolveDirectoryPath(folder));
        var path = Path.Combine(project.ResolveDirectoryPath(folder), project.NextDataAssetName(folder, descriptor.DisplayName));
        project.ValidateDataAssetPath(path);
        var id = DataAssetFile.Create(path, descriptor.Type, registry);
        var (instance, _, _) = DataAssetFile.Load(path, registry);
        var row = new DataAssetEditState(path, instance, id, descriptor.TypeId);
        Rows.Add(row);
        RefreshOwnership();
        return row;
    }

    public DataAssetEditState Duplicate(DataAssetEditState source, ProjectFile project, ComponentRegistry registry)
    {
        if (!Rows.Contains(source)) throw new InvalidOperationException("The row is no longer open.");
        var serializer = new DataAssetSerializer(registry);
        var folder = Path.GetRelativePath(project.RootDirectory, Path.GetDirectoryName(source.Path)!).Replace('\\', '/');
        var path = Path.Combine(project.ResolveDirectoryPath(folder), project.NextDataAssetName(folder, FileBase(source.Path)));
        project.ValidateDataAssetPath(path);
        var id = Guid.NewGuid();
        var (instance, _) = serializer.Deserialize(source.Serialize(registry));
        SceneFile.Write(path, serializer.Serialize(instance, id));
        var row = new DataAssetEditState(path, instance, id, source.TypeId);
        Rows.Add(row);
        RefreshOwnership();
        return row;
    }

    public void Delete(DataAssetEditState row)
    {
        if (!Rows.Contains(row)) throw new InvalidOperationException("The row is no longer open.");
        File.Delete(row.Path);
        Rows.Remove(row);
        RefreshOwnership();
    }

    public static string FileBase(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.EndsWith(".pure.asset", StringComparison.Ordinal) ? name[..^".pure.asset".Length] : name;
    }

    public DataAssetEditState? FindOwner(object owner)
    {
        foreach (var row in Rows)
        {
            if (ReferenceEquals(row.Instance, owner)) return row;
            HashSet<object> graph = [with(ReferenceEqualityComparer.Instance)];
            DataAssetEditState.CollectObjects(row.Instance, graph);
            if (graph.Contains(owner)) return row;
        }
        return null;
    }

    public List<(DataAssetEditState Row, string Yaml)> PrepareSave(ComponentRegistry registry) =>
        [.. Rows.Where(row => row.Dirty).Select(row => (row, row.Serialize(registry)))];

    public static void Save(List<(DataAssetEditState Row, string Yaml)> pending)
    {
        foreach (var (row, yaml) in pending) SceneFile.Write(row.Path, yaml);
        foreach (var (row, _) in pending) row.Dirty = false;
    }

    public List<DataAssetEditState>? PrepareReload(ComponentRegistry previous, ComponentRegistry next) =>
        Type is null ? null : [.. Rows.Select(row => row.Migrate(previous, next))];

    public void AdoptReload(List<DataAssetEditState>? candidate, ProjectComponents components)
    {
        if (candidate is null || Type is null) return;
        var typeId = Type.TypeId;
        Rows.Clear();
        Rows.AddRange(candidate);
        var descriptors = DataAssetDescriptor.DescribeAll(components.Registry, out _, components.DataAssetTypes);
        Type = descriptors.FirstOrDefault(descriptor => descriptor.TypeId == typeId) ?? Type;
        RefreshOwnership();
    }
}
