using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PureEngine.Core;

/// <summary>Version 1 reads as roots, version 2 saves parentId and siblingIndex. Restoring never mutates the caller's current scene.</summary>
public sealed class SceneSerializer(ComponentRegistry registry)
{
    private static readonly ConditionalWeakTable<Type, MemberInfo[]> InspectorMembers = [];
    private readonly Lazy<ISerializer> _writer = new(static () => new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithQuotingNecessaryStrings().DisableAliases()
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull).Build());
    private readonly Lazy<IDeserializer> _reader = new(static () => new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithDuplicateKeyChecking().Build());

    public string Serialize(Scene scene) => _writer.Value.Serialize(Capture(scene));

    /// <summary>Copies current authoring data without YAML or file I/O. Unmarked members keep their initializers.</summary>
    public Scene Clone(Scene scene, Func<Type, object>? factory = null) => Restore(Capture(scene), factory, out _);

    private SceneDocument Capture(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var document = new SceneDocument { Version = 2, Objects = [] };
        var roots = scene.RootObjects;
        var rootIndex = new Dictionary<Guid, int>();
        for (var i = 0; i < roots.Count; i++)
            rootIndex.Add(roots[i].Id, i);
        foreach (var item in scene.Objects)
        {
            var parent = item.Parent;
            if (parent is not null && !scene.Objects.Contains(parent))
                throw new InvalidDataException($"{item.Name}: parent is outside the scene.");
            int siblingIndex;
            Guid? parentId;
            if (parent is null)
            {
                parentId = null;
                siblingIndex = rootIndex[item.Id];
            }
            else
            {
                parentId = parent.Id;
                siblingIndex = parent.Children.ToList().FindIndex(candidate => ReferenceEquals(candidate, item));
                if (siblingIndex < 0)
                    throw new InvalidDataException($"{item.Name}: sibling order is broken.");
            }
            var saved = new SceneObjectDocument
            {
                Id = item.Id,
                Name = item.Name,
                ParentId = parentId,
                SiblingIndex = siblingIndex,
                Components = [],
            };
            foreach (var component in item.Components)
            {
                var type = component.GetType();
                var values = new Dictionary<string, object?>();
                foreach (var member in Members(type))
                {
                    var value = member is FieldInfo field ? field.GetValue(component) : ((PropertyInfo)member).GetValue(component);
                    var memberType = MemberType(member);
                    InspectorValueTypes.ValidateType(memberType);
                    values.Add(member.Name, InspectorValueTypes.ToStorable(value, memberType));
                }
                saved.Components.Add(new ComponentDocument
                {
                    TypeId = registry.GetId(type),
                    Values = values,
                    Priorities = CapturePriorities(item, component),
                });
            }
            document.Objects.Add(saved);
        }
        return document;
    }

    private static Dictionary<string, object?>? CapturePriorities(SceneObject item, object component)
    {
        var (start, update, destroy) = item.ReadAttachedPriorities(component);
        if (start == 0 && update == 0 && destroy == 0) return null;
        var saved = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (start != 0) saved.Add("start", start);
        if (update != 0) saved.Add("update", update);
        if (destroy != 0) saved.Add("destroy", destroy);
        return saved;
    }

    public Scene Deserialize(string yaml, Func<Type, object>? factory = null) => Deserialize(yaml, out _, factory);

    /// <summary>Reports Inspector names added, renamed or discarded so editors can request a save.</summary>
    public Scene Deserialize(string yaml, out bool membersChanged, Func<Type, object>? factory = null) =>
        Restore(_reader.Value.Deserialize<SceneDocument>(yaml)
            ?? throw new InvalidDataException("The scene document is empty."), factory, out membersChanged);

    private Scene Restore(SceneDocument document, Func<Type, object>? factory, out bool membersChanged)
    {
        membersChanged = false;
        if (document.Version is not (1 or 2))
            throw new InvalidDataException($"Unsupported scene version: {document.Version}");
        if (document.Objects is null) throw new InvalidDataException("objects is required.");

        var scene = new Scene();
        // Preparation release tracking: components created below are owned by this restore
        // until it succeeds. On failure they are disposed reverse-creation without any Start/Destroy.
        var created = new List<object>();
        try
        {
            foreach (var saved in document.Objects)
            {
                if (saved is null || saved.Id == Guid.Empty || string.IsNullOrWhiteSpace(saved.Name) || saved.Components is null)
                    throw new InvalidDataException("Each object requires a non-empty id, name and components list.");
                if (document.Version == 1 && (saved.ParentId is not null || saved.SiblingIndex is not null))
                    throw new InvalidDataException($"{saved.Name}: version 1 must not contain parentId or siblingIndex.");
                if (document.Version == 2 && saved.SiblingIndex is null)
                    throw new InvalidDataException($"{saved.Name}: version 2 requires siblingIndex.");
                _ = scene.RestoreObject(saved.Id, saved.Name);
            }
            RestoreParentLinks(document, scene);
            foreach (var saved in document.Objects)
            {
                if (saved is null || saved.Components is null)
                    throw new InvalidDataException("Each object requires a non-empty id, name and components list.");
                var item = scene.Objects.First(candidate => candidate.Id == saved.Id);
                foreach (var data in saved.Components)
                {
                    if (data is null || string.IsNullOrWhiteSpace(data.TypeId) || data.Values is null)
                        throw new InvalidDataException($"{saved.Name}: component typeId and values are required.");
                    var type = registry.GetType(data.TypeId);
                    var names = ComponentSchema.GetInspectorMemberNames(type);
                    Dictionary<MemberInfo, object?> values = [];
                    foreach (var (name, raw) in data.Values)
                    {
                        if (!names.TryGetValue(name, out var member))
                        {
                            membersChanged = true;
                            continue; // Removed or renamed Inspector members are discarded on the next save.
                        }
                        if (name != member.Name) membersChanged = true;
                        if (!values.TryAdd(member, raw))
                            throw new InvalidDataException($"{data.TypeId}.{member.Name}: multiple saved names refer to the same Inspector member.");
                    }
                    var (start, update, destroy) = ReadPriorities(data, type);
                    var component = CreateComponent(type, factory, data.TypeId, saved.Name!);
                    created.Add(component);
                    foreach (var member in Members(type))
                    {
                        InspectorValueTypes.ValidateType(MemberType(member));
                        // A newly added member keeps its class initializer when absent from older scenes.
                        if (!values.TryGetValue(member, out var raw))
                        {
                            membersChanged = true;
                            continue;
                        }
                        var value = InspectorValueTypes.FromStorable(raw, MemberType(member), $"{data.TypeId}.{member.Name}");
                        if (member is FieldInfo field) field.SetValue(component, value);
                        else ((PropertyInfo)member).SetValue(component, value);
                    }
                    item.Attach(component);
                    item.RestorePriorities(component, start, update, destroy);
                }
            }
        }
        catch (Exception error)
        {
            var errors = new List<Exception> { error };
            for (var i = created.Count - 1; i >= 0; i--)
            {
                if (created[i] is IDisposable disposable)
                {
                    try { disposable.Dispose(); }
                    catch (Exception cleanupError) { errors.Add(cleanupError); }
                }
            }
            if (errors.Count > 1)
                throw new AggregateException("Scene restoration and cleanup failed.", errors);
            throw;
        }
        return scene;
    }

    private static void RestoreParentLinks(SceneDocument document, Scene scene)
    {
        if (document.Version == 1) return;
        var byId = document.Objects!.ToDictionary(saved => saved!.Id);
        foreach (var saved in document.Objects!)
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
        foreach (var saved in document.Objects!)
        {
            var chain = new HashSet<Guid> { saved!.Id };
            for (var current = saved; current?.ParentId is { } parentId; current = byId[parentId])
            {
                if (!chain.Add(parentId))
                    throw new InvalidDataException($"{saved.Name}: parent cycle is not allowed.");
            }
        }
        foreach (var group in document.Objects!.GroupBy(saved => saved!.ParentId?.ToString("D") ?? string.Empty))
        {
            var members = group.ToList();
            var indexes = members.Select(member => member!.SiblingIndex!.Value).ToList();
            if (indexes.Distinct().Count() != members.Count
                || indexes.Min() != 0 || indexes.Max() != members.Count - 1)
                throw new InvalidDataException("Sibling indexes must be unique continuous values from 0.");
        }
        var rootsInArrayOrder = document.Objects!.Where(saved => saved!.ParentId is null).ToList();
        for (var i = 0; i < rootsInArrayOrder.Count; i++)
        {
            if (rootsInArrayOrder[i]!.SiblingIndex != i)
                throw new InvalidDataException("Root sibling order must follow the document order.");
        }
        var items = scene.Objects.ToDictionary(item => item.Id);
        foreach (var group in document.Objects!.GroupBy(saved => saved!.ParentId?.ToString("D") ?? string.Empty))
        {
            foreach (var saved in group.OrderBy(member => member!.SiblingIndex!.Value))
                if (saved!.ParentId is { } parentId)
                    items[saved.Id].SetParent(items[parentId]);
        }
    }

    /// <summary>
    /// Component creation point (restore/Clone): creates new runtime and authoring instances from the type.
    /// Without a factory, uses the legacy parameterless creation; with a factory, uses the factory result.
    /// A factory failure is reported as-is without retrying parameterless creation to hide it.
    /// The factory contract is to return a new non-null instance of the exact requested type;
    /// shared dependencies belong on the injected service side.
    /// </summary>
    private static object CreateComponent(Type type, Func<Type, object>? factory, string typeId, string objectName)
    {
        if (factory is null) return Activator.CreateInstance(type)!;
        object? created;
        try
        {
            created = factory(type);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                $"Failed to create component {typeId} ({type.FullName}) for object '{objectName}'.", error);
        }
        if (created is null)
            throw new InvalidOperationException(
                $"Component factory returned null for {typeId} ({type.FullName}) on object '{objectName}'.");
        if (created.GetType() != type)
            throw new InvalidOperationException(
                $"Component factory returned {created.GetType().FullName} instead of {type.FullName} for {typeId} on object '{objectName}'.");
        return created;
    }

    private static MemberInfo[] Members(Type type) => InspectorMembers.GetValue(type, static type =>
        [.. ComponentSchema.GetInspectorMemberNames(type).Values.Distinct().OrderBy(member => member.Name, StringComparer.Ordinal)]);

    private static Type MemberType(MemberInfo member) => member is FieldInfo field
        ? field.FieldType : ((PropertyInfo)member).PropertyType;

    private static (int Start, int Update, int Destroy) ReadPriorities(ComponentDocument data, Type type)
    {
        if (data.Priorities is null) return (0, 0, 0);
        var parsed = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, raw) in data.Priorities)
        {
            if (key is not ("start" or "update" or "destroy"))
                throw new InvalidDataException($"{data.TypeId}.{key}: unknown priority.");
            var path = $"{data.TypeId}.priorities.{key}";
            int value;
            if (raw is int integer) value = integer;
            else if (raw is string text
                && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedValue))
                value = parsedValue;
            else
                throw new InvalidDataException($"{path}: invalid int value.");
            if (parsed.ContainsKey(key))
                throw new InvalidDataException($"{path}: duplicate priority.");
            parsed.Add(key, value);
        }
        // Invalid declarations are rejected when the runtime starts; loading keeps the data so it can be fixed.
        try
        {
            var lifecycles = ComponentSchema.GetLifecycle(type);
            foreach (var (key, kind) in new[] { ("start", lifecycles.Start), ("update", lifecycles.Update), ("destroy", lifecycles.Destroy) })
            {
                if (parsed.ContainsKey(key) && kind is null)
                    throw new InvalidDataException($"{data.TypeId}.priorities.{key}: no [{key}] lifecycle to prioritize.");
            }
        }
        catch (InvalidOperationException error) when (error.Message.Contains("requires a", StringComparison.Ordinal)
            || error.Message.Contains("multiple", StringComparison.Ordinal))
        {
            // Defer invalid declarations to runtime validation; keep priorities for editing.
        }
        parsed.TryGetValue("start", out var start);
        parsed.TryGetValue("update", out var update);
        parsed.TryGetValue("destroy", out var destroy);
        return (start, update, destroy);
    }
}
