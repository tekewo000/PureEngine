using System.Collections;
using System.Reflection;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PureEngine.Core;

/// <summary>
/// Copy-only prefab files: one scene-object subtree saved for duplication.
/// Placement copies the content with fresh IDs. References inside the
/// subtree are remapped to the copies; outside, image, and data asset references are preserved.
/// No live link, override, or variant support: the placed root keeps its source prefab ID for display only.
/// </summary>
public sealed class PrefabSerializer(ComponentRegistry registry)
{
    public const int CurrentVersion = 1;
    public const string FileExtension = ".pure.prefab.yaml";
    // Prefab files have no legacy versions: values decode in the strict scene mode that rejects inline values.
    private const int StrictReferenceVersion = 3;

    private static readonly Lazy<ISerializer> Writer = new(static () => new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithQuotingNecessaryStrings().DisableAliases()
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull).Build());
    private static readonly Lazy<IDeserializer> Reader = new(static () => new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithDuplicateKeyChecking().Build());

    /// <summary>Captures the root and its current descendants without file I/O. The document holds the source IDs until placement remaps them.</summary>
    public PrefabDocument Capture(Scene scene, SceneObject root)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(root);
        if (!scene.Objects.Contains(root))
            throw new InvalidDataException($"{root.Name}: prefab roots must belong to the captured scene.");
        var subtree = new HashSet<SceneObject>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<SceneObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!subtree.Add(current)) continue;
            foreach (var child in current.Children) pending.Push(child);
        }
        var componentToId = new Dictionary<object, Guid>(ReferenceEqualityComparer.Instance);
        foreach (var item in scene.Objects)
        {
            foreach (var component in item.Components)
            {
                if (!item.TryGetComponentId(component, out var componentId))
                    throw new InvalidDataException($"{item.Name}: component {component.GetType().Name} is missing its engine ID.");
                if (!componentToId.TryAdd(component, componentId))
                    throw new InvalidDataException($"{item.Name}: duplicate component instance.");
            }
        }
        var document = new PrefabDocument { Version = CurrentVersion, Objects = [] };
        foreach (var item in scene.Objects)
        {
            if (!subtree.Contains(item)) continue;
            Guid? parentId;
            int siblingIndex;
            if (ReferenceEquals(item, root))
            {
                parentId = null;
                siblingIndex = 0;
            }
            else
            {
                var parent = item.Parent ?? throw new InvalidDataException($"{item.Name}: prefab children must stay inside the subtree.");
                parentId = parent.Id;
                siblingIndex = IndexOfReference(parent.Children, item);
                if (siblingIndex < 0)
                    throw new InvalidDataException($"{item.Name}: sibling order is broken.");
            }
            var saved = new SceneObjectDocument
            {
                Id = item.Id,
                Name = item.Name,
                ParentId = parentId,
                SiblingIndex = siblingIndex,
                PrefabId = item.PrefabId,
                Components = [],
            };
            foreach (var component in item.Components)
            {
                var type = component.GetType();
                var ownerId = componentToId[component];
                var values = new Dictionary<string, object?>();
                foreach (var member in SceneSerializer.Members(type))
                {
                    var value = member is FieldInfo field ? field.GetValue(component) : ((PropertyInfo)member).GetValue(component);
                    var memberType = SceneSerializer.MemberType(member);
                    if (SceneReferenceTypes.ContainsReference(memberType, registry))
                    {
                        if (!SceneReferenceTypes.IsSupportedInspectorType(memberType, registry))
                            throw new InvalidDataException($"{type.Name}.{member.Name}: unsupported Inspector value type {memberType.FullName}.");
                        values.Add(member.Name, SceneReferenceCodec.Encode(value, memberType, registry, ownerId, member.Name, componentToId, scene, forSave: true));
                    }
                    else
                    {
                        InspectorValueTypes.ValidateType(memberType);
                        values.Add(member.Name, InspectorValueTypes.ToStorable(value, memberType));
                    }
                }
                saved.Components.Add(new ComponentDocument
                {
                    Id = ownerId,
                    TypeId = registry.GetId(type),
                    Values = values,
                    Priorities = SceneSerializer.CapturePriorities(item, component),
                });
            }
            document.Objects.Add(saved);
        }
        return document;
    }

    /// <summary>Captures and writes the prefab file content with the given prefab identity.</summary>
    public string Serialize(Scene scene, SceneObject root, Guid prefabId)
    {
        if (prefabId == Guid.Empty) throw new InvalidDataException("Prefab ID must not be empty.");
        var document = Capture(scene, root);
        document.Id = prefabId;
        document.Objects!.Single(item => item!.ParentId is null)!.PrefabId = prefabId;
        return Writer.Value.Serialize(document);
    }

    /// <summary>Writes an already captured document after structural validation.</summary>
    public static string Serialize(PrefabDocument prefab)
    {
        ArgumentNullException.ThrowIfNull(prefab);
        ValidateStructure(prefab);
        return Writer.Value.Serialize(prefab);
    }

    /// <summary>Reads a prefab file and validates its structure. Member mapping happens at placement.</summary>
    public static PrefabDocument Deserialize(string yaml) => Parse(yaml);

    /// <summary>Reads a prefab file without a registry. Used by catalog scans and game-side loading.</summary>
    public static PrefabDocument Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var document = Reader.Value.Deserialize<PrefabDocument>(yaml)
            ?? throw new InvalidDataException("The prefab document is empty.");
        ValidateStructure(document);
        return document;
    }

    /// <summary>Restores an isolated authoring scene while preserving the prefab's object and component IDs.</summary>
    public Scene RestoreForEditing(PrefabDocument document, out bool membersChanged,
        DataAssetStore? assets = null, PrefabCatalog? prefabs = null, Func<Type, object>? factory = null, LocalizationStore? localization = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateStructure(document);
        return new SceneSerializer(registry, assets, prefabs, localization).Restore(
            new SceneDocument { Version = StrictReferenceVersion, Objects = document.Objects }, factory, out membersChanged);
    }

    /// <summary>Copies the prefab into the destination scene as plain objects with fresh IDs. Returns the new root.</summary>
    /// <remarks>Placement appends at the end: as a root, or as the last child of <paramref name="parent"/>.</remarks>
    public SceneObject Instantiate(Scene target, PrefabDocument prefab, SceneObject? parent = null, Func<Type, object>? factory = null) =>
        Instantiate(target, prefab, out _, parent, factory);

    /// <summary>Copies the prefab and reports Inspector names added, renamed or discarded so editors can request a save.</summary>
    public SceneObject Instantiate(Scene target, PrefabDocument prefab, out bool membersChanged, SceneObject? parent = null, Func<Type, object>? factory = null)
        => InstantiateCore(target, prefab, out membersChanged, parent, factory, null, out _);

    internal object InstantiateReference(Scene target, PrefabDocument prefab, Guid targetId, SceneObject? parent, Func<Type, object>? factory)
    {
        InstantiateCore(target, prefab, out _, parent, factory, targetId, out var reference);
        return reference!;
    }

    private SceneObject InstantiateCore(Scene target, PrefabDocument prefab, out bool membersChanged, SceneObject? parent,
        Func<Type, object>? factory, Guid? targetId, out object? reference)
    {
        reference = null;
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(prefab);
        ValidateStructure(prefab);
        if (parent is not null && !target.Objects.Contains(parent))
            throw new InvalidDataException($"{parent.Name}: prefab parents must belong to the destination scene.");
        var plan = BuildPlan(prefab, out var localChanged);
        var createdObjects = new List<SceneObject>();
        var attachedComponents = new List<(SceneObject Owner, object Component, Guid NewId)>();
        var unattachedComponents = new List<object>();
        var newByOld = new Dictionary<Guid, SceneObject>();
        var idMap = new Dictionary<Guid, Guid>();
        try
        {
            foreach (var saved in prefab.Objects!)
            {
                // AddEmpty keeps one runtime-aware creation path for editing and Play.
                // Names are preserved exactly: scenes allow duplicates, so no suffix is added.
                var item = target.AddEmpty();
                createdObjects.Add(item);
                item.Rename(saved.Name!);
                item.PrefabId = saved.PrefabId;
                newByOld.Add(saved.Id, item);
                idMap.Add(saved.Id, item.Id);
            }
            var decoded = new List<PendingValue>();
            foreach (var step in plan)
            {
                var item = newByOld[step.SavedId];
                var component = SceneSerializer.CreateComponent(step.Type, factory, step.TypeId, step.ObjectName);
                try
                {
                    item.Attach(component);
                }
                catch
                {
                    unattachedComponents.Add(component);
                    throw;
                }
                if (!item.TryGetComponentId(component, out var newId))
                    throw new InvalidDataException($"{step.ObjectName}: placed component is missing its engine ID.");
                attachedComponents.Add((item, component, newId));
                idMap.Add(step.ComponentId, newId);
                if (target.Runtime is null)
                    item.RestorePriorities(component, step.Start, step.Update, step.Destroy);
                else
                {
                    // Attachments join the next Step, so pre-Start priorities are still accepted here.
                    if (step.Start != 0) item.SetStartPriority(component, step.Start);
                    if (step.Update != 0) item.SetUpdatePriority(component, step.Update);
                    if (step.Destroy != 0) item.SetDestroyPriority(component, step.Destroy);
                }
                foreach (var (member, raw) in step.Values)
                    decoded.Add(new PendingValue(item, component, newId, member, raw, $"{step.TypeId}.{member.Name}"));
            }
            var objectsById = target.Objects.ToDictionary(item => item.Id);
            var componentsById = new Dictionary<Guid, object>();
            foreach (var item in target.Objects)
            {
                foreach (var component in item.Components)
                {
                    if (!item.TryGetComponentId(component, out var current))
                        throw new InvalidDataException($"{item.Name}: component is missing its engine ID.");
                    if (!componentsById.TryAdd(current, component))
                        throw new InvalidDataException($"{item.Name}: duplicate component ID {current:D}.");
                }
            }
            foreach (var pending in decoded)
            {
                var memberType = SceneSerializer.MemberType(pending.Member);
                object? resolved;
                if (SceneReferenceTypes.ContainsReference(memberType, registry))
                {
                    resolved = SceneReferenceCodec.Decode(
                        RemapRefs(pending.Raw, idMap), memberType, registry,
                        pending.NewId, pending.Member.Name,
                        objectsById, componentsById, target,
                        StrictReferenceVersion, pending.DisplayPath, ref localChanged);
                }
                else
                {
                    resolved = InspectorValueTypes.FromStorable(pending.Raw, memberType, pending.DisplayPath);
                }
                if (pending.Member is FieldInfo field) field.SetValue(pending.Component, resolved);
                else ((PropertyInfo)pending.Member).SetValue(pending.Component, resolved);
            }
            foreach (var group in prefab.Objects!.GroupBy(saved => saved!.ParentId?.ToString("D") ?? string.Empty))
            {
                foreach (var saved in group.OrderBy(member => member!.SiblingIndex!.Value))
                    if (saved!.ParentId is { } parentId)
                        newByOld[saved.Id].SetParent(newByOld[parentId]);
            }
            var newRoot = newByOld[prefab.Objects!.Single(saved => saved!.ParentId is null)!.Id];
            newRoot.PrefabId = prefab.Id;
            if (parent is not null) newRoot.SetParent(parent);
            if (targetId is { } requested)
                reference = newByOld.TryGetValue(requested, out var referencedObject)
                    ? referencedObject : attachedComponents.Single(item => item.NewId == idMap[requested]).Component;
            membersChanged = localChanged;
            return newRoot;
        }
        catch (Exception error)
        {
            Rollback(target, createdObjects, attachedComponents, unattachedComponents, error);
            throw;
        }
    }

    /// <summary>Copies prefab file content into the destination scene. Parses and validates before placement.</summary>
    public SceneObject Instantiate(Scene target, string yaml, out bool membersChanged, SceneObject? parent = null, Func<Type, object>? factory = null) =>
        Instantiate(target, Parse(yaml), out membersChanged, parent, factory);

    private sealed record PlanStep(Guid SavedId, Guid ComponentId, string ObjectName, string TypeId, Type Type,
        Dictionary<MemberInfo, object?> Values, int Start, int Update, int Destroy);

    private sealed record PendingValue(SceneObject Owner, object Component, Guid NewId, MemberInfo Member, object? Raw, string DisplayPath);

    // Validates types, member mapping, and priorities before mutating the destination scene.
    private List<PlanStep> BuildPlan(PrefabDocument prefab, out bool membersChanged)
    {
        var localChanged = false;
        var plan = new List<PlanStep>();
        foreach (var saved in prefab.Objects!)
        {
            foreach (var data in saved.Components!)
            {
                var type = registry.GetType(data.TypeId!);
                var names = ComponentSchema.GetInspectorMemberNames(type);
                Dictionary<MemberInfo, object?> values = [];
                foreach (var (name, raw) in data.Values!)
                {
                    if (!names.TryGetValue(name, out var member))
                    {
                        localChanged = true;
                        continue;
                    }
                    if (name != member.Name) localChanged = true;
                    if (!values.TryAdd(member, raw))
                        throw new InvalidDataException($"{data.TypeId}.{member.Name}: multiple saved names refer to the same Inspector member.");
                }
                var (start, update, destroy) = SceneSerializer.ReadPriorities(data, type);
                foreach (var member in SceneSerializer.Members(type))
                {
                    var memberType = SceneSerializer.MemberType(member);
                    if (SceneReferenceTypes.ContainsReference(memberType, registry))
                    {
                        if (!SceneReferenceTypes.IsSupportedInspectorType(memberType, registry))
                            throw new InvalidDataException($"{data.TypeId}.{member.Name}: unsupported Inspector value type {memberType.FullName}.");
                    }
                    else
                    {
                        InspectorValueTypes.ValidateType(memberType);
                    }
                    if (!values.ContainsKey(member)) localChanged = true;
                }
                plan.Add(new PlanStep(saved.Id, data.Id, saved.Name!, data.TypeId!, type, values, start, update, destroy));
            }
        }
        membersChanged = localChanged;
        return plan;
    }

    internal static void ValidateStructure(PrefabDocument prefab)
    {
        if (prefab.Version != CurrentVersion)
            throw new InvalidDataException($"Unsupported prefab version: {prefab.Version}");
        if (prefab.Id == Guid.Empty)
            throw new InvalidDataException("Prefab ID must not be empty.");
        if (prefab.Objects is null || prefab.Objects.Count == 0)
            throw new InvalidDataException("Prefab objects are required.");
        var byId = new Dictionary<Guid, SceneObjectDocument>();
        foreach (var saved in prefab.Objects)
        {
            if (saved is null || saved.Id == Guid.Empty || string.IsNullOrWhiteSpace(saved.Name) || saved.Components is null)
                throw new InvalidDataException("Each prefab object requires a non-empty id, name and components list.");
            if (saved.SiblingIndex is null)
                throw new InvalidDataException($"{saved.Name}: siblingIndex is required.");
            if (saved.PrefabId == Guid.Empty)
                throw new InvalidDataException($"{saved.Name}: prefab ID must not be empty.");
            if (!byId.TryAdd(saved.Id, saved))
                throw new InvalidDataException($"{saved.Name}: duplicate object ID {saved.Id:D}.");
        }
        var roots = prefab.Objects.Where(saved => saved!.ParentId is null).ToList();
        if (roots.Count != 1)
            throw new InvalidDataException("A prefab must contain a single root object.");
        if (roots[0]!.SiblingIndex != 0)
            throw new InvalidDataException($"{roots[0]!.Name}: the prefab root siblingIndex must be 0.");
        foreach (var saved in prefab.Objects)
        {
            if (saved!.ParentId is { } parentId)
            {
                if (parentId == Guid.Empty)
                    throw new InvalidDataException($"{saved.Name}: parentId must not be empty.");
                if (parentId == saved.Id)
                    throw new InvalidDataException($"{saved.Name}: an object cannot be its own parent.");
                if (!byId.ContainsKey(parentId))
                    throw new InvalidDataException($"{saved.Name}: parent {parentId:D} is missing.");
            }
            if (saved.SiblingIndex is not { } sibling || sibling < 0)
                throw new InvalidDataException($"{saved.Name}: siblingIndex must be a non-negative integer.");
        }
        foreach (var saved in prefab.Objects)
        {
            var chain = new HashSet<Guid> { saved!.Id };
            for (var current = saved; current?.ParentId is { } parentId; current = byId[parentId])
            {
                if (!chain.Add(parentId))
                    throw new InvalidDataException($"{saved.Name}: parent cycle is not allowed.");
            }
        }
        foreach (var group in prefab.Objects.GroupBy(saved => saved!.ParentId?.ToString("D") ?? string.Empty))
        {
            var members = group.ToList();
            var indexes = members.Select(member => member!.SiblingIndex!.Value).ToList();
            if (indexes.Distinct().Count() != members.Count
                || indexes.Min() != 0 || indexes.Max() != members.Count - 1)
                throw new InvalidDataException("Prefab sibling indexes must be unique continuous values from 0.");
        }
        var seenComponentIds = new HashSet<Guid>();
        foreach (var saved in prefab.Objects)
        {
            foreach (var data in saved!.Components!)
            {
                if (data is null || string.IsNullOrWhiteSpace(data.TypeId) || data.Values is null)
                    throw new InvalidDataException($"{saved.Name}: component typeId and values are required.");
                if (data.Id == Guid.Empty)
                    throw new InvalidDataException($"{saved.Name}: component ID must not be empty.");
                if (!seenComponentIds.Add(data.Id))
                    throw new InvalidDataException($"{saved.Name}: duplicate component ID {data.Id:D}.");
                if (byId.ContainsKey(data.Id))
                    throw new InvalidDataException($"{saved.Name}: component ID {data.Id:D} collides with an object ID.");
                if (data.Priorities is not null)
                {
                    foreach (var key in data.Priorities.Keys)
                        if (key is not ("start" or "update" or "destroy"))
                            throw new InvalidDataException($"{data.TypeId}.{key}: unknown priority.");
                }
            }
        }
    }

    // Rewrites prefab-internal { ref: <id> } entries to fresh destination IDs on a copy.
    // Outside IDs pass through so the destination scene resolves or misses them with its own rules.
    private static object? RemapRefs(object? raw, Dictionary<Guid, Guid> idMap)
    {
        if (raw is IDictionary mapping)
        {
            if (mapping.Count == 1)
            {
                DictionaryEntry? single = null;
                foreach (DictionaryEntry entry in mapping) single = entry;
                if (single is { Key: "ref", Value: string text }
                    && Guid.TryParse(text, out var target)
                    && idMap.TryGetValue(target, out var remapped))
                    return new Dictionary<string, object?>(StringComparer.Ordinal) { ["ref"] = remapped.ToString("D") };
            }
            var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in mapping)
            {
                if (entry.Key is not string key) return raw;
                copy.Add(key, RemapRefs(entry.Value, idMap));
            }
            return copy;
        }
        if (raw is null || raw is string) return raw;
        if (raw is IEnumerable sequence)
        {
            List<object?> copy = [];
            foreach (var element in sequence) copy.Add(RemapRefs(element, idMap));
            return copy;
        }
        return raw;
    }

    private static int IndexOfReference(IReadOnlyList<SceneObject> items, SceneObject item)
    {
        for (var i = 0; i < items.Count; i++)
            if (ReferenceEquals(items[i], item)) return i;
        return -1;
    }

    // Removes partially placed objects and releases instances the engine never adopted.
    // Runtime-owned attachments are only reserved for frame-end shutdown, never disposed here.
    private static void Rollback(
        Scene target,
        List<SceneObject> createdObjects,
        List<(SceneObject Owner, object Component, Guid NewId)> attachedComponents,
        List<object> unattachedComponents,
        Exception error)
    {
        var errors = new List<Exception> { error };
        foreach (var (_, _, newId) in attachedComponents)
            target.References.RemoveOwner(newId);
        if (target.Runtime is null)
        {
            for (var i = createdObjects.Count - 1; i >= 0; i--)
            {
                try { target.RemoveImmediately(createdObjects[i]); }
                catch (Exception cleanupError) { errors.Add(cleanupError); }
            }
            for (var i = attachedComponents.Count - 1; i >= 0; i--)
            {
                if (attachedComponents[i].Component is IDisposable disposable)
                {
                    try { disposable.Dispose(); }
                    catch (Exception cleanupError) { errors.Add(cleanupError); }
                }
            }
        }
        else
        {
            for (var i = createdObjects.Count - 1; i >= 0; i--)
            {
                try { target.Remove(createdObjects[i]); }
                catch (Exception cleanupError) { errors.Add(cleanupError); }
            }
        }
        foreach (var component in unattachedComponents)
        {
            if (component is IDisposable disposable)
            {
                try { disposable.Dispose(); }
                catch (Exception cleanupError) { errors.Add(cleanupError); }
            }
        }
        if (errors.Count > 1)
            throw new AggregateException("Prefab placement and cleanup failed.", errors);
    }
}
