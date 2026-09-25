namespace PureEngine.Core;

// Defaults intentionally leave required fields missing so malformed files cannot become empty prefabs.
// Object and component entries reuse the scene document shapes; the prefab adds its own identity and version.
// Copy-only: placement duplicates this content with fresh IDs and marks the placed root with the source ID for display. No live link is stored.
public sealed class PrefabDocument
{
    public int Version { get; set; }
    public Guid Id { get; set; }
    public List<SceneObjectDocument>? Objects { get; set; }
}
