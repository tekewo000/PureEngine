namespace PureEngine.Core;

// Defaults intentionally leave required fields missing so malformed files cannot become empty scenes.
public sealed class SceneDocument
{
    public int Version { get; set; }
    public List<SceneObjectDocument>? Objects { get; set; }
}

public sealed class SceneObjectDocument
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public Guid? ParentId { get; set; }
    public int? SiblingIndex { get; set; }
    public Guid? PrefabId { get; set; }
    public List<ComponentDocument>? Components { get; set; }
}

public sealed class ComponentDocument
{
    public Guid Id { get; set; }
    public string? TypeId { get; set; }
    public Dictionary<string, object?>? Values { get; set; }
    // Attach settings, separate from Inspector values. Keys are start/update/destroy; absent means 0.
    public Dictionary<string, object?>? Priorities { get; set; }
}
