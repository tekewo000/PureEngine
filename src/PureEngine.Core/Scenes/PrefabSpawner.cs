namespace PureEngine.Core;

/// <summary>
/// Spawns catalog prefabs into the bound run scene. Game code receives it through constructor
/// injection and spawns by prefab ID. Bound once per run by the host after the run scene exists;
/// use it from Start or later, never from the constructor.
/// </summary>
public sealed class PrefabSpawner
{
    private Scene? _scene;
    private ComponentRegistry? _registry;
    private Func<Type, object>? _factory;
    private PrefabCatalog _catalog = PrefabCatalog.Empty;

    /// <summary>Whether a run scene is bound. Unbound spawners reject Spawn calls.</summary>
    public bool IsBound => _scene is not null;

    /// <summary>Binds the run scene, registry, component factory, and prefab catalog. Called once per run by the host.</summary>
    public void Bind(Scene scene, ComponentRegistry registry, Func<Type, object>? factory, PrefabCatalog? catalog)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(registry);
        _scene = scene;
        _registry = registry;
        _factory = factory;
        _catalog = catalog ?? PrefabCatalog.Empty;
    }

    /// <summary>Instantiates a prefab assigned to a SceneObject or component field and returns the corresponding copy.</summary>
    public T Instantiate<T>(T original, SceneObject? parent = null) where T : class
    {
        ArgumentNullException.ThrowIfNull(original);
        if (_scene is null || _registry is null)
            throw new InvalidOperationException("PrefabSpawner is not bound to a run.");
        if (!PrefabReferenceStore.TryGetIdentity(original, out var identity))
            throw new InvalidOperationException("Instantiate requires a prefab asset reference.");
        var document = _catalog.Find(identity!.PrefabId) ?? _scene.Prefabs.Catalog.Find(identity.PrefabId)
            ?? throw new InvalidDataException($"Prefab {identity.PrefabId:D} was not found in the catalog.");
        var targetType = document.Objects!.Any(item => item.Id == identity.TargetId) ? typeof(SceneObject)
            : document.Objects!.SelectMany(item => item.Components!).Where(item => item.Id == identity.TargetId)
                .Select(item => _registry.GetType(item.TypeId!)).SingleOrDefault();
        if (targetType is null || !typeof(T).IsAssignableFrom(targetType))
            throw new InvalidDataException("The prefab reference target is missing or its type has changed.");
        return (T)new PrefabSerializer(_registry).InstantiateReference(_scene, document, identity.TargetId, parent, _factory);
    }

    /// <summary>Copies the prefab into the bound scene as plain objects with fresh IDs. Returns the new root.</summary>
    /// <remarks>Placement appends at the end: as a root, or as the last child of <paramref name="parent"/>.</remarks>
    public SceneObject Spawn(Guid prefabId, SceneObject? parent = null)
    {
        if (_scene is null || _registry is null)
            throw new InvalidOperationException("PrefabSpawner is not bound to a run.");
        if (prefabId == Guid.Empty)
            throw new InvalidDataException("Prefab ID must not be empty.");
        var document = _catalog.Find(prefabId)
            ?? throw new InvalidDataException($"Prefab {prefabId:D} was not found in the catalog.");
        return new PrefabSerializer(_registry).Instantiate(_scene, document, parent, _factory);
    }
}
