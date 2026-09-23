using System.Reflection;

namespace PureEngine.Core;

/// <summary>
/// SceneObject・登録Componentへの直接参照の型区分。保存はID、実行は解決済みの通常C#参照を使う。
/// ObjectRef案は旧案とし、こちらの区分を正本とする。
/// </summary>
public static class SceneReferenceTypes
{
    public static bool IsSceneObjectReference(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type == typeof(SceneObject);
    }

    public static bool IsComponentReference(Type type, ComponentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(registry);
        if (type == typeof(string) || type == typeof(object) || type == typeof(Sprite))
            return false;
        if (!type.IsClass || type.IsAbstract || type.IsArray || type.IsGenericType)
            return false;
        return registry.Types.Contains(type);
    }

    public static bool IsSingleReference(Type type, ComponentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(registry);
        if (IsSceneObjectReference(type))
            return true;
        return IsComponentReference(type, registry);
    }

    public static bool ContainsReference(Type type, ComponentRegistry registry) =>
        ContainsReferenceCore(type, registry, []);

    private static bool ContainsReferenceCore(Type type, ComponentRegistry registry, HashSet<Type> chain)
    {
        if (IsSingleReference(type, registry))
            return true;
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
            return ContainsReferenceCore(underlying, registry, chain);
        if (type.IsArray)
            return type.GetArrayRank() == 1 && ContainsReferenceCore(type.GetElementType()!, registry, chain);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            return ContainsReferenceCore(type.GetGenericArguments()[0], registry, chain);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            var args = type.GetGenericArguments();
            return args[0] == typeof(string) && ContainsReferenceCore(args[1], registry, chain);
        }
        if (!IsCustomShape(type))
            return false;
        if (!chain.Add(type))
            return false;
        try
        {
            foreach (var member in ComponentSchema.GetInspectorMembers(type))
            {
                var memberType = member is FieldInfo field ? field.FieldType : ((PropertyInfo)member).PropertyType;
                if (ContainsReferenceCore(memberType, registry, chain))
                    return true;
            }
            return false;
        }
        finally
        {
            chain.Remove(type);
        }
    }

    private static bool IsCustomShape(Type type)
    {
        if (type == typeof(object) || type == typeof(string))
            return false;
        if (!type.IsClass || type.IsAbstract || type.IsValueType || type.IsEnum)
            return false;
        if (type.IsArray || type.IsGenericType)
            return false;
        if (type == typeof(Transform) || type == typeof(Sprite) || type == typeof(SceneObject))
            return false;
        return type.GetConstructor(Type.EmptyTypes) is not null;
    }

    /// <summary>Inspector・保存対象として扱えるか。値型・参照・参照を含む入れ子のいずれか。</summary>
    public static bool IsSupportedInspectorType(Type type, ComponentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(registry);
        return IsSupportedType(type, registry, [], allowContainer: true);
    }

    private static bool IsSupportedType(Type type, ComponentRegistry registry, HashSet<Type> chain, bool allowContainer)
    {
        if (IsSingleReference(type, registry))
            return true;
        if (type.IsArray)
            return allowContainer && type.IsSZArray && IsSupportedType(type.GetElementType()!, registry, chain, allowContainer: false);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            return allowContainer && IsSupportedType(type.GetGenericArguments()[0], registry, chain, allowContainer: false);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            var args = type.GetGenericArguments();
            return allowContainer && args[0] == typeof(string) && IsSupportedType(args[1], registry, chain, allowContainer: false);
        }
        if (IsCustomShape(type))
        {
            if (!chain.Add(type)) return false;
            try
            {
                return ComponentSchema.GetInspectorMembers(type).All(member =>
                    IsSupportedType(member is FieldInfo field ? field.FieldType : ((PropertyInfo)member).PropertyType,
                        registry, chain, allowContainer: true));
            }
            finally { chain.Remove(type); }
        }
        if (InspectorValueTypes.IsSupportedType(type))
            return allowContainer || type != typeof(Transform);
        return false;
    }
}
