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

    /// <summary>Live read-only view of objects, in insertion order.</summary>
    public ReadOnlyObservableCollection<SceneObject> Objects { get; }

    /// <summary>Root objects in sibling order. Children hold their own sibling order.</summary>
    public IReadOnlyList<SceneObject> RootObjects => [.. _objects.Where(item => item.Parent is null)];

    /// <summary>Moves a root object within the root sibling order. Children keep their own order.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Index is outside the root order.</exception>
    /// <exception cref="InvalidOperationException">Item is not a root object of this scene.</exception>
    public void SetRootSiblingIndex(SceneObject item, int index)
    {
        ArgumentNullException.ThrowIfNull(item);
        Runtime?.EnsureHierarchyMutationAllowed(item);
        if (item.Parent is not null || !_objects.Contains(item))
            throw new InvalidOperationException("Only root objects of this scene can be reordered by the Scene.");
        var roots = RootObjects;
        if (index < 0 || index >= roots.Count)
            throw new ArgumentOutOfRangeException(nameof(index), "Root sibling index is out of range.");
        if (IndexOfReference(roots, item) == index) return;
        _objects.Remove(item);
        var remaining = RootObjects;
        if (index >= remaining.Count) _objects.Add(item);
        else _objects.Insert(_objects.IndexOf(remaining[index]), item);
    }

    /// <summary>Creates an empty object with a non-colliding default name ("Empty", "Empty (1)", ...).</summary>
    public SceneObject AddEmpty() => AddNamed("Empty");

    /// <summary>
    /// Creates an object with a non-colliding name based on <paramref name="baseName"/>
    /// ("Image", "Image (1)", ...). Used by the Stuffs context menu (Empty, UI/Image, UI/Button).
    /// </summary>
    public SceneObject AddNamed(string baseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);
        Runtime?.EnsureMutationAllowed();
        baseName = baseName.Trim();
        var name = baseName;
        for (var suffix = 1; _objects.Any(item => item.Name == name); suffix++)
            name = $"{baseName} ({suffix})";

        var item = new SceneObject(name) { Runtime = Runtime, OwnerScene = this };
        Runtime?.RegisterObject(item);
        _ids.Add(item.Id);
        _objects.Add(item);
        return item;
    }

    /// <summary>Removes the exact instance, deferred to frame end during execution. Returns false when absent/already reserved.</summary>
    /// <remarks>Editing removal deletes the object with its current descendants at once.</remarks>
    public bool Remove(SceneObject item) => Runtime is null ? RemoveImmediately(item) : Runtime.Remove(item);

    internal bool RemoveImmediately(SceneObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!_objects.Contains(item)) return false;
        var targets = CollectSubtree(item);
        item.DetachParentLink();
        foreach (var target in targets)
        {
            _objects.Remove(target);
            _ids.Remove(target.Id);
            target.OwnerScene = null;
        }
        return true;
    }

    /// <summary>Runtime has already captured and disposed its removal set; never discover new descendants here.</summary>
    internal void RemoveObjectImmediately(SceneObject item)
    {
        if (!_objects.Remove(item)) return;
        _ids.Remove(item.Id);
        item.DetachParentLink();
        item.OwnerScene = null;
    }

    private static List<SceneObject> CollectSubtree(SceneObject root)
    {
        List<SceneObject> collected = [root];
        for (var i = 0; i < collected.Count; i++)
            collected.AddRange(collected[i].Children);
        return collected;
    }

    private static int IndexOfReference(IReadOnlyList<SceneObject> items, SceneObject item)
    {
        for (var i = 0; i < items.Count; i++)
            if (ReferenceEquals(items[i], item)) return i;
        return -1;
    }

    internal SceneObject RestoreObject(Guid id, string name)
    {
        if (_ids.Contains(id))
            throw new InvalidDataException($"Duplicate object ID: {id}");
        var item = new SceneObject(id, name) { OwnerScene = this };
        _ids.Add(id);
        _objects.Add(item);
        return item;
    }
}
