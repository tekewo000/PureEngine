using System.Reflection;

namespace PureEngine.Core;

/// <summary>Rebuilds authoring instances only when all saved data can be preserved.</summary>
public static class SceneCodeMigrator
{
    public static Scene Migrate(Scene source, ComponentRegistry oldRegistry,
        ComponentRegistry newRegistry, Func<Type, object>? factory = null)
    {
        // Numeric conversion must not hide a schema change.
        foreach (var item in source.Objects)
        foreach (var component in item.Components)
        {
            var oldType = component.GetType();
            var id = oldRegistry.GetId(oldType);
            var newType = newRegistry.GetType(id);
            var members = ComponentSchema.GetInspectorMembers(newType)
                .ToDictionary(member => member.Name, StringComparer.Ordinal);
            foreach (var oldMember in ComponentSchema.GetInspectorMembers(oldType))
            {
                if (!members.TryGetValue(oldMember.Name, out var member)
                    || MemberType(oldMember) != MemberType(member))
                    throw new InvalidDataException(
                        $"{item.Name}/{id}.{oldMember.Name}: 削除・型変更により値を引き継げません。元の定義を戻して再保存してください。編集データは保持しています。");
            }
            ComponentSchema.GetLifecycle(newType);
        }

        // Reuse persistence validation, identity/Priority restoration and failure cleanup.
        var yaml = new SceneSerializer(oldRegistry).Serialize(source);
        return new SceneSerializer(newRegistry).Deserialize(yaml, factory);
    }

    private static Type MemberType(MemberInfo member) => member is FieldInfo field
        ? field.FieldType : ((PropertyInfo)member).PropertyType;
}
