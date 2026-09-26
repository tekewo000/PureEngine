using PureEngine.Core;
using PureEngine.Runtime;

namespace PureEngine.Editor;

/// <summary>
/// Owner of the editing scene, path, dirty flag, and editing services. Consolidates the mutation paths
/// for editing state. EditorDocuments owns the stores; pane models and bindings present their values.
/// Has no Avalonia dependency. Returns the old scene to the caller for disposal.
/// The coordinator (adoption) or EditorDocuments (shutdown) guarantees teardown order from components to services.
/// </summary>
public sealed class EditSceneStore(Scene initial, string? path = null, bool dirty = false)
{
    public GameSession Services { get; private set; } = GameSession.Create(GameServices.Configure);

    public GameSession ReplaceServices(GameSession services)
    {
        var previous = Services;
        Services = services;
        return previous;
    }

    /// <summary>The scene being edited. A different instance from the copy held by the run session.</summary>
    public Scene Current { get; private set; } = initial ?? throw new ArgumentNullException(nameof(initial));

    /// <summary>Save destination of the scene being edited. Null for an unsaved new scene.</summary>
    public string? Path { get; private set; } = path;

    /// <summary>Whether unsaved changes exist.</summary>
    public bool IsDirty { get; private set; } = dirty;

    public void MarkChanged() => IsDirty = true;

    public void MarkSaved(string? path)
    {
        Path = path;
        IsDirty = false;
    }

    public void MarkClean() => IsDirty = false;

    /// <summary>Replaces only the save path without changing the scene itself. Used to rebind references on Explorer rename or move.</summary>
    public void SetPath(string? path) => Path = path;

    internal void SetDirtyForTest(bool dirty) => IsDirty = dirty;

    /// <summary>
    /// Adopts a new editing scene and returns the old scene. The caller disposes the old scene's components.
    /// The caller specifies the dirty flag. Reloads keep the old dirty flag, and changes to saved content set it to true.
    /// </summary>
    public Scene Replace(Scene next, string? path, bool dirty)
    {
        ArgumentNullException.ThrowIfNull(next);
        var previous = Current;
        Current = next;
        Path = path;
        IsDirty = dirty;
        return previous;
    }

    /// <summary>Clears the editing scene on shutdown and similar flows, returning the old scene. The caller disposes it.</summary>
    public Scene Reset()
    {
        var previous = Current;
        Current = new Scene();
        Path = null;
        IsDirty = false;
        return previous;
    }
}
