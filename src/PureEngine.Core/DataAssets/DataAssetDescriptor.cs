using System.Reflection;

namespace PureEngine.Core;

/// <summary>Validated data asset type with its registry ID and creation menu location.</summary>
public sealed record DataAssetDescriptor(Type Type, string TypeId, string MenuPath, string DisplayName)
{
    /// <summary>Whether the type can be stored as a data asset. Requires a public parameterless constructor and supported Inspector members.</summary>
    public static bool IsDataAssetType(Type? type)
    {
        if (type is null || !IsAssetShape(type)) return false;
        if (type.GetCustomAttribute<DataAssetAttribute>(inherit: false) is null) return false;
        return HasSupportedMembers(type);
    }

    /// <summary>Builds a descriptor for a registered data asset type. Returns false with a reason when the type is not usable.</summary>
    public static bool TryCreate(Type? type, ComponentRegistry? registry, out DataAssetDescriptor? descriptor, out string? error)
    {
        descriptor = null;
        error = null;
        if (type is null)
        {
            error = "Data asset type is null.";
            return false;
        }
        if (registry is null)
        {
            error = $"{type.FullName}: component registry is required.";
            return false;
        }
        if (!IsAssetShape(type))
        {
            error = $"{type.FullName}: data asset must be a public, non-abstract, non-generic class with a public parameterless constructor.";
            return false;
        }
        var attribute = type.GetCustomAttribute<DataAssetAttribute>(inherit: false);
        if (attribute is null)
        {
            error = $"{type.FullName}: missing [DataAsset].";
            return false;
        }
        if (!HasSupportedMembers(type))
        {
            error = $"{type.FullName}: every [Inspector] member must be a supported value type without scene references.";
            return false;
        }
        foreach (var member in ComponentSchema.GetInspectorMembers(type))
        {
            var memberType = member is FieldInfo field ? field.FieldType : ((PropertyInfo)member).PropertyType;
            if (!SceneReferenceTypes.ContainsReference(memberType, registry)) continue;
            error = $"{type.FullName}.{member.Name}: scene references cannot be stored in data assets.";
            return false;
        }
        if (!TryNormalizeMenuPath(attribute.MenuPath, type.Name, out var menuPath, out var displayName, out var menuError))
        {
            error = $"{type.FullName}: {menuError}";
            return false;
        }
        string typeId;
        try
        {
            typeId = registry.GetId(type);
        }
        catch (Exception ex)
        {
            error = $"{type.FullName}: unregistered data asset type ({ex.GetBaseException().Message}).";
            return false;
        }
        descriptor = new DataAssetDescriptor(type, typeId, menuPath, displayName);
        return true;
    }

    /// <summary>Lists descriptors for registered data asset types. Unusable types are reported as diagnostics instead of throwing.</summary>
    public static IReadOnlyList<DataAssetDescriptor> DescribeAll(ComponentRegistry registry, out IReadOnlyList<string> diagnostics,
        IEnumerable<Type>? declaredTypes = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        List<DataAssetDescriptor> found = [];
        List<string> problems = [];
        var seenMenus = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in registry.Types.Concat(declaredTypes ?? []).Distinct().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            if (type.GetCustomAttribute<DataAssetAttribute>(inherit: false) is null) continue;
            if (!TryCreate(type, registry, out var descriptor, out var error) || descriptor is null)
            {
                problems.Add(error ?? $"{type.FullName}: invalid data asset type.");
                continue;
            }
            if (!seenMenus.Add(descriptor.MenuPath))
            {
                problems.Add($"{type.FullName}: duplicate data asset menu '{descriptor.MenuPath}'. Use [DataAsset(\"Unique/Path\")].");
                continue;
            }
            found.Add(descriptor);
        }
        diagnostics = problems;
        return found;
    }

    private static bool IsAssetShape(Type type)
    {
        if (!type.IsClass || type.IsAbstract) return false;
        if (type.ContainsGenericParameters || type.IsGenericType) return false;
        if (typeof(Delegate).IsAssignableFrom(type)) return false;
        if (type.Name.Contains('<', StringComparison.Ordinal)) return false;
        if (!type.IsVisible) return false;
        return type.GetConstructor(Type.EmptyTypes) is not null;
    }

    private static bool HasSupportedMembers(Type type)
    {
        foreach (var member in ComponentSchema.GetInspectorMembers(type))
        {
            var memberType = member is FieldInfo field ? field.FieldType : ((PropertyInfo)member).PropertyType;
            if (!InspectorValueTypes.IsSupportedType(memberType)) return false;
        }
        return true;
    }

    private static bool TryNormalizeMenuPath(string? raw, string fallback, out string menuPath, out string displayName, out string? error)
    {
        menuPath = "";
        displayName = "";
        error = null;
        var source = string.IsNullOrWhiteSpace(raw) ? fallback : raw!;
        var segments = source.Replace('\\', '/').Split('/', StringSplitOptions.None);
        List<string> cleaned = [];
        foreach (var segment in segments)
        {
            var name = segment.Trim();
            if (name.Length == 0)
            {
                error = $"invalid menu path '{raw ?? fallback}': use 'Folder/Name' without empty parts.";
                return false;
            }
            cleaned.Add(name);
        }
        if (cleaned.Count == 0)
        {
            error = "menu path must not be empty.";
            return false;
        }
        menuPath = string.Join('/', cleaned);
        displayName = cleaned[^1];
        return true;
    }
}