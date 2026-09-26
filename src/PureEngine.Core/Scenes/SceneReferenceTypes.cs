using System.Reflection;

namespace PureEngine.Core;

/// <summary>
/// Classifies direct scene, component, and data asset references.
/// Persistence stores IDs; running code uses resolved C# instances.
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
        if (DataAssetStore.IsAssetType(type))
            return true;
        return IsComponentReference(type, registry);
    }

    public static bool ContainsReference(Type type, ComponentRegistry registry) =>
        ContainsReferenceCore(type, registry, []);

    /// <summary>
    /// Whether the type holds a reference that cannot live inside a data asset file: SceneObject or
    /// data asset references, including inside containers and nested values. Registered plain component
    /// types count as nested values here because asset files own their data and have no scene to point at.
    /// </summary>
    public static bool ContainsAssetExternalReference(Type type, ComponentRegistry registry) =>
        ContainsAssetExternalReferenceCore(type, registry, []);

    private static bool ContainsAssetExternalReferenceCore(Type type, ComponentRegistry registry, HashSet<Type> chain)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(registry);
        if (IsSceneObjectReference(type))
            return true;
        if (DataAssetStore.IsAssetType(type))
            return true;
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
            return ContainsAssetExternalReferenceCore(underlying, registry, chain);
        if (type.IsArray)
            return ContainsAssetExternalReferenceCore(type.GetElementType()!, registry, chain);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            return ContainsAssetExternalReferenceCore(type.GetGenericArguments()[0], registry, chain);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            var args = type.GetGenericArguments();
            return args[0] == typeof(string) && ContainsAssetExternalReferenceCore(args[1], registry, chain);
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
                if (ContainsAssetExternalReferenceCore(memberType, registry, chain))
                    return true;
            }
            return false;
        }
        finally
        {
            chain.Remove(type);
        }
    }

    private static bool ContainsReferenceCore(Type type, ComponentRegistry registry, HashSet<Type> chain)
    {
        if (IsSingleReference(type, registry))
            return true;
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
            return ContainsReferenceCore(underlying, registry, chain);
        if (type.IsArray)
            return ContainsReferenceCore(type.GetElementType()!, registry, chain);
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

    private static bool IsCustomShape(Type type) =>
        type != typeof(SceneObject) && InspectorValueTypes.IsCustomObjectShape(type);

    /// <summary>Accepts values, references, and nested values containing references for editing and persistence.</summary>
    public static bool IsSupportedInspectorType(Type type, ComponentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(registry);
        return IsSupportedType(type, registry, []);
    }

    private static bool IsSupportedType(Type type, ComponentRegistry registry, HashSet<Type> chain)
    {
        if (IsSingleReference(type, registry))
            return true;
        if (Nullable.GetUnderlyingType(type) is { } underlying)
            return IsSupportedType(underlying, registry, chain);
        if (type.IsArray)
            return (type.IsSZArray || type.GetArrayRank() > 1) && IsSupportedType(type.GetElementType()!, registry, chain);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            return IsSupportedType(type.GetGenericArguments()[0], registry, chain);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            var args = type.GetGenericArguments();
            return args[0] == typeof(string) && IsSupportedType(args[1], registry, chain);
        }
        if (IsCustomShape(type))
        {
            if (!chain.Add(type)) return false;
            try
            {
                return ComponentSchema.GetInspectorMembers(type).All(member =>
                    IsSupportedType(member is FieldInfo field ? field.FieldType : ((PropertyInfo)member).PropertyType,
                        registry, chain));
            }
            finally { chain.Remove(type); }
        }
        if (InspectorValueTypes.IsSupportedType(type))
            return true;
        return false;
    }
}
