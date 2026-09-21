using System.Collections.ObjectModel;

namespace PureEngine.Core;

/// <summary>
/// A container of <see cref="SceneObject"/>s. Owns creation and removal; identity is reference-based.
/// </summary>
public sealed class Scene
{
    private readonly ObservableCollection<SceneObject> _objects = [];

    public Scene() => Objects = new ReadOnlyObservableCollection<SceneObject>(_objects);

    /// <summary>Live read-only view of objects, in insertion order. Bound directly by the Editor.</summary>
    public ReadOnlyObservableCollection<SceneObject> Objects { get; }

    /// <summary>Creates an empty object with a non-colliding default name ("Empty", "Empty (1)", ...).</summary>
    public SceneObject AddEmpty()
    {
        var name = "Empty";
        for (var suffix = 1; _objects.Any(item => item.Name == name); suffix++)
            name = $"Empty ({suffix})";

        var item = new SceneObject(name);
        _objects.Add(item);
        return item;
    }

    /// <summary>Removes the exact instance. Returns false when absent; names are not used for matching.</summary>
    public bool Remove(SceneObject item) => _objects.Remove(item);

    internal SceneObject RestoreObject(Guid id, string name)
    {
        if (_objects.Any(item => item.Id == id))
            throw new InvalidDataException($"Duplicate object ID: {id}");
        var item = new SceneObject(id, name);
        _objects.Add(item);
        return item;
    }
}
