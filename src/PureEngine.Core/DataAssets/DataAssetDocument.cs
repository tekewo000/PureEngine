namespace PureEngine.Core;

/// <summary>Stored form of a single data asset file. Keeps the engine-managed identity separate from the user data.</summary>
public sealed class DataAssetDocument
{
    public int Version { get; set; }
    public Guid Id { get; set; }
    public string? TypeId { get; set; }
    public Dictionary<string, object?>? Values { get; set; }
}