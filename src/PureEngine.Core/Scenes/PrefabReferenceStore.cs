using System.Runtime.CompilerServices;

namespace PureEngine.Core;

/// <summary>Scene-owned prefab templates, kept outside the live hierarchy and lifecycle.</summary>
public sealed class PrefabReferenceStore(PrefabCatalog? catalog = null, Func<Type, object>? factory = null)
{
    public sealed record Identity(Guid PrefabId, Guid TargetId);
    private static readonly ConditionalWeakTable<object, Identity> Identities = [];
    private readonly Dictionary<Guid, Scene> _templates = [];
    private readonly HashSet<Guid> _loading = [];
    internal PrefabCatalog Catalog { get; } = catalog?.Copy() ?? PrefabCatalog.Empty;
    internal Func<Type, object>? Factory { get; } = factory;

    internal IEnumerable<object> Components => _templates.Values.SelectMany(scene => scene.Objects).SelectMany(item => item.Components);
    public static bool TryGetIdentity(object value, out Identity? identity) => Identities.TryGetValue(value, out identity);

    /// <summary>Loads a typed template reference without adding objects to the editing scene.</summary>
    public object Assign(PrefabDocument document, Type type, ComponentRegistry registry, Func<Type, object>? factory = null, DataAssetStore? assets = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        PrefabSerializer.ValidateStructure(document);
        var targetId = type == typeof(SceneObject)
            ? document.Objects!.Single(item => item.ParentId is null).Id
            : document.Objects!.SelectMany(item => item.Components!)
                .Single(item => type.IsAssignableFrom(registry.GetType(item.TypeId!))).Id;
        Catalog.Add(document);
        return Resolve(document.Id, targetId, type, registry, assets, factory)
            ?? throw new InvalidDataException("The prefab reference target is missing.");
    }

    internal object? Resolve(Guid prefabId, Guid targetId, Type type, ComponentRegistry registry, DataAssetStore? assets, Func<Type, object>? factory = null)
    {
        if (!_templates.TryGetValue(prefabId, out var template))
        {
            var document = Catalog.Find(prefabId);
            if (document is null) return null;
            if (!_loading.Add(prefabId)) throw new InvalidDataException("Cyclic prefab asset references are not supported.");
            try
            {
                template = new SceneSerializer(registry, assets, Catalog).Restore(
                    new SceneDocument { Version = 3, Objects = document.Objects }, factory ?? Factory, out _, this);
                _templates.Add(prefabId, template);
                foreach (var item in template.Objects)
                {
                    Identities.Add(item, new Identity(prefabId, item.Id));
                    foreach (var component in item.Components)
                        Identities.Add(component, new Identity(prefabId, item.GetComponentId(component)));
                }
            }
            finally { _loading.Remove(prefabId); }
        }
        if (type == typeof(SceneObject))
            return template.Objects.FirstOrDefault(item => item.Id == targetId);
        if (!template.TryGetComponent(targetId, out _, out var target)) return null;
        if (!type.IsInstanceOfType(target)) throw new InvalidDataException($"Prefab reference type mismatch: expected {type.Name}.");
        return target;
    }
}
