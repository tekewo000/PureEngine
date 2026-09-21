using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PureEngine.Core;

/// <summary>Version 1 YAML scenes. Restoring never mutates the caller's current scene.</summary>
public sealed class SceneSerializer(ComponentRegistry registry)
{
    private static readonly ConcurrentDictionary<Type, MemberInfo[]> InspectorMembers = new();
    private readonly Lazy<ISerializer> _writer = new(static () => new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithQuotingNecessaryStrings().DisableAliases().Build());
    private readonly Lazy<IDeserializer> _reader = new(static () => new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithDuplicateKeyChecking().Build());

    public string Serialize(Scene scene) => _writer.Value.Serialize(Capture(scene));

    /// <summary>Copies current authoring data without YAML or file I/O. Unmarked members keep their initializers.</summary>
    public Scene Clone(Scene scene) => Restore(Capture(scene));

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
                saved.Components.Add(new ComponentDocument { TypeId = registry.GetId(type), Values = values });
            }
            document.Objects.Add(saved);
        }
        return document;
    }

    public Scene Deserialize(string yaml)
    {
        return Restore(_reader.Value.Deserialize<SceneDocument>(yaml)
            ?? throw new InvalidDataException("The scene document is empty."));
    }

    private Scene Restore(SceneDocument document)
    {
        if (document.Version != 1)
            throw new InvalidDataException($"Unsupported scene version: {document.Version}");
        if (document.Objects is null) throw new InvalidDataException("objects is required.");

        var scene = new Scene();
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
                var component = Activator.CreateInstance(type)!;
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
            }
        }
        return scene;
    }

    private static MemberInfo[] Members(Type type) => InspectorMembers.GetOrAdd(type, static type =>
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
}
