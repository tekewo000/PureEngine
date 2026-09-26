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

    public void RefreshOwnership()
    {
        Owned.Clear();
        foreach (var row in Rows) DataAssetEditState.CollectObjects(row.Instance, Owned);
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
