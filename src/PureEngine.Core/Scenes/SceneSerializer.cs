using System.Runtime.CompilerServices;
using System.Globalization;
using System.Reflection;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PureEngine.Core;

/// <summary>Version 1 YAML scenes. Restoring never mutates the caller's current scene.</summary>
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
    public Scene Clone(Scene scene, Func<Type, object>? factory = null) => Restore(Capture(scene), factory);

    private SceneDocument Capture(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var document = new SceneDocument { Version = 1, Objects = [] };
        foreach (var item in scene.Objects)
        {
            var saved = new SceneObjectDocument { Id = item.Id, Name = item.Name, Components = [] };
            foreach (var component in item.Components)
            {
                var type = component.GetType();
                var values = new Dictionary<string, object?>();
                foreach (var member in Members(type))
                {
                    var value = member is FieldInfo field ? field.GetValue(component) : ((PropertyInfo)member).GetValue(component);
                    ValidateType(MemberType(member));
                    if (value is float number && !float.IsFinite(number))
                        throw new InvalidDataException($"{type.Name}.{member.Name}: a finite number is required.");
                    values.Add(member.Name, value);
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

    public Scene Deserialize(string yaml, Func<Type, object>? factory = null) => Restore(_reader.Value.Deserialize<SceneDocument>(yaml)
            ?? throw new InvalidDataException("The scene document is empty."), factory);

    private Scene Restore(SceneDocument document, Func<Type, object>? factory = null)
    {
        if (document.Version != 1)
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
                var item = scene.RestoreObject(saved.Id, saved.Name);
                foreach (var data in saved.Components)
                {
                    if (data is null || string.IsNullOrWhiteSpace(data.TypeId) || data.Values is null)
                        throw new InvalidDataException($"{saved.Name}: component typeId and values are required.");
                    var type = registry.GetType(data.TypeId);
                    var members = Members(type).ToDictionary(member => member.Name, StringComparer.Ordinal);
                    foreach (var name in data.Values.Keys)
                        if (!members.ContainsKey(name))
                            throw new InvalidDataException($"{data.TypeId}.{name}: unknown Inspector member.");
                    var (start, update, destroy) = ReadPriorities(data, type);
                    var component = CreateComponent(type, factory, data.TypeId, saved.Name);
                    created.Add(component);
                    foreach (var member in members.Values)
                    {
                        ValidateType(MemberType(member));
                        // A newly added member keeps its class initializer when absent from older scenes.
                        if (!data.Values.TryGetValue(member.Name, out var raw)) continue;
                        var value = ReadValue(raw, MemberType(member), $"{data.TypeId}.{member.Name}");
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

    /// <summary>
    /// Component 生成箇所 (復元/Clone): 型から新しい実行用・編集用インスタンスを作る。
    /// factory 未指定時は従来のパラメータレス生成を使い、指定時は factory の結果を使う。
    /// factory の失敗時はそのエラーを報告し、パラメータレス生成で再試行して隠さない。
    /// factory は null でない要求どおりの exact type の新しいインスタンスを返す契約とし、
    /// 共有したいものは注入するサービス側に置く。
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
    {
        var members = ComponentSchema.GetInspectorMembers(type).OrderBy(member => member.Name, StringComparer.Ordinal).ToArray();
        if (members.Select(member => member.Name).Distinct(StringComparer.Ordinal).Count() != members.Length)
            throw new InvalidDataException($"Ambiguous Inspector member names in {type.FullName}.");
        return members;
    });

    private static Type MemberType(MemberInfo member) => member is FieldInfo field
        ? field.FieldType : ((PropertyInfo)member).PropertyType;

    private static void ValidateType(Type type)
    {
        if (type != typeof(string) && type != typeof(int) && type != typeof(float) && type != typeof(bool))
            throw new InvalidDataException($"Unsupported Inspector value type: {type.FullName}");
    }

    private static object? ReadValue(object? raw, Type type, string path)
    {
        // Capture supplies typed scalars; the YAML reader supplies strings.
        if (raw is not null && raw.GetType() == type && type != typeof(string))
        {
            if (raw is float number && !float.IsFinite(number))
                throw new InvalidDataException($"{path}: a finite number is required.");
            return raw;
        }
        // Untyped YAML scalars are strings; collections are never coerced into a scalar.
        if (type == typeof(string) && (raw is null || raw is string)) return raw;
        if (raw is string text)
        {
            if (type == typeof(int) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) return integer;
            if (type == typeof(float) && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && float.IsFinite(number)) return number;
            if (type == typeof(bool) && bool.TryParse(text, out var boolean)) return boolean;
        }
        throw new InvalidDataException($"{path}: invalid {type.Name} value.");
    }

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
