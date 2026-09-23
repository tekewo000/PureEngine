using System.Reflection;

namespace PureEngine.Core;

/// <summary>Rebuilds authoring instances, retaining matching values and allowing removed or renamed members.</summary>
public static class SceneCodeMigrator
{
    public static Scene Migrate(Scene source, ComponentRegistry oldRegistry,
        ComponentRegistry newRegistry, Func<Type, object>? factory = null) => Migrate(source, oldRegistry, newRegistry, out _, factory);

    public static Scene Migrate(Scene source, ComponentRegistry oldRegistry,
        ComponentRegistry newRegistry, out bool membersChanged, Func<Type, object>? factory = null)
    {
        // Numeric conversion must not hide a schema change.
        foreach (var item in source.Objects)
        foreach (var component in item.Components)
        {
            var oldType = component.GetType();
            var id = oldRegistry.GetId(oldType);
            var newType = newRegistry.GetType(id);
            var members = ComponentSchema.GetInspectorMemberNames(newType);
            foreach (var oldMember in ComponentSchema.GetInspectorMembers(oldType))
            {
                if (!members.TryGetValue(oldMember.Name, out var member)) continue;
                if (!CompatibleType(MemberType(oldMember), MemberType(member), oldRegistry, newRegistry))
                    throw new InvalidDataException(
                        $"{item.Name}/{id}.{oldMember.Name}: Cannot carry over the value due to a type change. Restore the original definition and save again. The edit data is preserved.");
            }
            ComponentSchema.GetLifecycle(newType);
        }

        // Reuse persistence validation, identity/Priority restoration and failure cleanup.
        var yaml = new SceneSerializer(oldRegistry).Serialize(source);
        return new SceneSerializer(newRegistry).Deserialize(yaml, out membersChanged, factory);
    }

    private static Type MemberType(MemberInfo member) => member is FieldInfo field
        ? field.FieldType : ((PropertyInfo)member).PropertyType;

    private static bool CompatibleType(Type before, Type after, ComponentRegistry oldRegistry, ComponentRegistry newRegistry)
    {
        if (before == after) return true;
        var beforeIsRef = before == typeof(SceneObject) || SceneReferenceTypes.IsSingleReference(before, oldRegistry);
        var afterIsRef = after == typeof(SceneObject) || SceneReferenceTypes.IsSingleReference(after, newRegistry);
        if (beforeIsRef || afterIsRef)
            return beforeIsRef && afterIsRef && before.FullName == after.FullName;
        if (before.IsArray && after.IsArray)
            return before.IsSZArray == after.IsSZArray && CompatibleType(before.GetElementType()!, after.GetElementType()!, oldRegistry, newRegistry);
        if (before.IsGenericType && after.IsGenericType
            && before.GetGenericTypeDefinition() == after.GetGenericTypeDefinition()
            && (before.GetGenericTypeDefinition() == typeof(List<>)
                || (before.GetGenericTypeDefinition() == typeof(Dictionary<,>) && before.GetGenericArguments()[0] == typeof(string))))
        {
            return before.GetGenericArguments().Zip(after.GetGenericArguments()).All(pair => CompatibleType(pair.First, pair.Second, oldRegistry, newRegistry));
        }
        if (!IsSupportedForMigration(before, oldRegistry) || !IsSupportedForMigration(after, newRegistry)) return false;
        // Recompiled enums have new Type identities. Preserve existing names and numeric meanings.
        if (before.IsEnum && after.IsEnum)
            return before.FullName == after.FullName
                && Enum.GetUnderlyingType(before) == Enum.GetUnderlyingType(after)
                && before.IsDefined(typeof(FlagsAttribute), false) == after.IsDefined(typeof(FlagsAttribute), false)
                && before.GetFields(BindingFlags.Public | BindingFlags.Static).All(field =>
                    Equals(field.GetRawConstantValue(), after.GetField(field.Name)?.GetRawConstantValue()));
        if (InspectorValueTypes.IsCustomInspectorObject(before) && InspectorValueTypes.IsCustomInspectorObject(after))
        {
            var members = ComponentSchema.GetInspectorMemberNames(after);
            return before.FullName == after.FullName
                && ComponentSchema.GetInspectorMembers(before).All(oldMember =>
                    !members.TryGetValue(oldMember.Name, out var member)
                    || CompatibleType(MemberType(oldMember), MemberType(member), oldRegistry, newRegistry));
        }
        if (SceneReferenceTypes.ContainsReference(before, oldRegistry) || SceneReferenceTypes.ContainsReference(after, newRegistry))
        {
            if (!SceneReferenceTypes.ContainsReference(before, oldRegistry) || !SceneReferenceTypes.ContainsReference(after, newRegistry))
                return false;
            if (before.FullName != after.FullName)
                return false;
            var afterMembers = ComponentSchema.GetInspectorMemberNames(after);
            return ComponentSchema.GetInspectorMembers(before).All(oldMember =>
                !afterMembers.TryGetValue(oldMember.Name, out var member)
                || CompatibleType(MemberType(oldMember), MemberType(member), oldRegistry, newRegistry));
        }
        return before.IsGenericType && after.IsGenericType
            && before.GetGenericTypeDefinition() == after.GetGenericTypeDefinition()
            && before.GetGenericArguments().Zip(after.GetGenericArguments()).All(pair => CompatibleType(pair.First, pair.Second, oldRegistry, newRegistry));
    }

    private static bool IsSupportedForMigration(Type type, ComponentRegistry registry) =>
        InspectorValueTypes.IsSupportedType(type) || SceneReferenceTypes.IsSupportedInspectorType(type, registry);
}
