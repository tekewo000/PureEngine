using System.Collections;
using System.Reflection;
using PureEngine.Core;

namespace PureEngine.Editor;

/// <summary>An editable asset document, independent of its Inspector or table view.</summary>
public sealed class DataAssetEditState(string path, object instance, Guid id, string typeId)
{
    public string Path { get; } = path;
    public object Instance { get; set; } = instance;
    public Guid Id { get; set; } = id;
    public string TypeId { get; } = typeId;
    public bool Dirty { get; set; }

    public string Serialize(ComponentRegistry registry) => new DataAssetSerializer(registry).Serialize(Instance, Id);

    public void Save(string yaml, ProjectFile? project)
    {
        project?.ValidateDataAssetPath(Path);
        SceneFile.Write(Path, yaml);
        Dirty = false;
    }

    public DataAssetEditState Migrate(ComponentRegistry previous, ComponentRegistry next)
    {
        var (instance, id) = new DataAssetSerializer(next).Deserialize(Serialize(previous), out var typeId, out var changed);
        return new(Path, instance, id, typeId) { Dirty = Dirty || changed };
    }

    /// <summary>Collects Inspector-owned objects, including cycles, for routing nested edits.</summary>
    public static void CollectObjects(object? value, HashSet<object> into)
    {
        if (value is null || value is string || value.GetType().IsValueType || !into.Add(value)) return;
        if (value is Array array)
        {
            foreach (var element in array) CollectObjects(element, into);
            return;
        }
        if (value is IDictionary dictionary)
        {
            foreach (var entry in dictionary.Values) CollectObjects(entry, into);
            return;
        }
        if (value is IEnumerable sequence && value.GetType() is { IsGenericType: true } type
            && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            foreach (var element in sequence) CollectObjects(element, into);
            return;
        }
        foreach (var member in ComponentSchema.GetInspectorMembers(value.GetType()))
            CollectObjects(member switch
            {
                FieldInfo field => field.GetValue(value),
                PropertyInfo property => property.GetValue(value),
                _ => throw new NotSupportedException($"Unsupported member: {member.Name}"),
            }, into);
    }
}
