using System.Collections.ObjectModel;

namespace PureEngine.Core;

/// <summary>
/// A container of <see cref="SceneObject"/>s. Owns creation and removal; identity is reference-based.
/// </summary>
public sealed class Scene
{
    private readonly ObservableCollection<SceneObject> _objects = [];
    private readonly HashSet<Guid> _ids = [];
    internal SceneRuntime? Runtime { get; set; }

    public Scene() => Objects = new ReadOnlyObservableCollection<SceneObject>(_objects);

    /// <summary>Live read-only view of objects, in insertion order. Bound directly by the Editor.</summary>
    public ReadOnlyObservableCollection<SceneObject> Objects { get; }

    /// <summary>Creates an empty object with a non-colliding default name ("Empty", "Empty (1)", ...).</summary>
    public SceneObject AddEmpty()
    {
        Runtime?.EnsureMutationAllowed();
        var name = "Empty";
        for (var suffix = 1; _objects.Any(item => item.Name == name); suffix++)
            name = $"Empty ({suffix})";

        var item = new SceneObject(name) { Runtime = Runtime };
        Runtime?.RegisterObject(item);
        _ids.Add(item.Id);
        _objects.Add(item);
        return item;
    }

    /// <summary>Removes the exact instance, deferred to frame end during execution. Returns false when absent/already reserved.</summary>
    public bool Remove(SceneObject item) => Runtime is null ? RemoveImmediately(item) : Runtime.Remove(item);

    internal bool RemoveImmediately(SceneObject item)
    {
        if (!_objects.Remove(item)) return false;
        _ids.Remove(item.Id);
        return true;
    }

    internal SceneObject RestoreObject(Guid id, string name)
    {
        if (_ids.Contains(id))
            throw new InvalidDataException($"Duplicate object ID: {id}");
        var item = new SceneObject(id, name);
        _ids.Add(id);
        _objects.Add(item);
        return item;
    }
}
