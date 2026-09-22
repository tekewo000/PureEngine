using System.Reflection;
using System.Runtime.CompilerServices;

namespace PureEngine.Core;

/// <summary>Reads and validates attribute markers on plain C# components.</summary>
public static class ComponentSchema
{
    internal sealed record LifecycleMethods(MethodInfo? Start, MethodInfo? Update, MethodInfo? Destroy);
    private static readonly ConditionalWeakTable<Type, LifecycleMethods> Lifecycles = [];

    /// <summary>Validates the lifecycle declarations and returns the Start method, if present.</summary>
    public static MethodInfo? GetStartMethod(Type type) => GetLifecycle(type).Start;

    /// <summary>Validates the lifecycle declarations and returns the Update method, if present.</summary>
    public static MethodInfo? GetUpdateMethod(Type type) => GetLifecycle(type).Update;

    /// <summary>Validates the lifecycle declarations and returns the Destroy method, if present.</summary>
    public static MethodInfo? GetDestroyMethod(Type type) => GetLifecycle(type).Destroy;

    internal static LifecycleMethods GetLifecycle(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Lifecycles.GetValue(type, static type => new(
            FindLifecycle(type, typeof(StartAttribute)),
            FindLifecycle(type, typeof(UpdateAttribute)),
            FindLifecycle(type, typeof(DestroyAttribute))));
    }

    private static MethodInfo? FindLifecycle(Type type, Type attribute)
    {
        MethodInfo? found = null;
        var overrides = new HashSet<MethodInfo>();
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var method in current.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance
                         | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                // Visit the most derived implementation once, including inherited markers.
                if (method.IsVirtual && !overrides.Add(method.GetBaseDefinition())) continue;
                if (!method.IsDefined(attribute, inherit: true)) continue;
                var parameters = method.GetParameters();
                var validParameters = parameters.Length == 0 || (attribute == typeof(UpdateAttribute)
                    && parameters.Length == 1 && parameters[0].ParameterType == typeof(float));
                if (method.IsStatic || method.IsAbstract || method.ContainsGenericParameters
                    || method.ReturnType != typeof(void)
                    || method.IsDefined(typeof(AsyncStateMachineAttribute), false) || !validParameters)
                    throw new InvalidOperationException($"{type.FullName}: {method.DeclaringType!.FullName}.{method.Name} "
                        + $"[{attribute.Name}]: requires a non-static, non-generic, synchronous void method "
                        + (attribute == typeof(UpdateAttribute) ? "with no parameters or one float parameter." : "with no parameters."));
                if (found is not null)
                    throw new InvalidOperationException($"{type.FullName}: multiple [{attribute.Name}] methods: "
                        + $"{found.DeclaringType!.FullName}.{found.Name}, {method.DeclaringType!.FullName}.{method.Name}.");
                found = method;
            }
        }
        return found;
    }

    /// <summary>
    /// Lists public readable/writable fields and properties marked with <see cref="InspectorAttribute"/>.
    /// Non-public members, readonly fields, getter-only properties, statics, and indexers are excluded.
    /// </summary>
    public static IReadOnlyList<MemberInfo> GetInspectorMembers(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        List<MemberInfo> found = [];

        foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
        {
            if (f.IsStatic || f.IsInitOnly || f.IsLiteral)
                continue;
            if (!f.IsDefined(typeof(InspectorAttribute), inherit: true))
                continue;
            found.Add(f);
        }

        foreach (var p in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!p.CanRead || !p.CanWrite)
                continue;
            if (p.GetIndexParameters().Length != 0)
                continue;
            var getter = p.GetMethod;
            var setter = p.SetMethod;
            if (getter is null || setter is null || getter.IsStatic || !getter.IsPublic || !setter.IsPublic)
                continue;
            if (!p.IsDefined(typeof(InspectorAttribute), inherit: true))
                continue;
            found.Add(p);
        }

        found.Sort((a, b) => a.MetadataToken.CompareTo(b.MetadataToken));
        return found;
    }

    internal static Dictionary<string, MemberInfo> GetInspectorMemberNames(Type type)
    {
        var members = GetInspectorMembers(type);
        Dictionary<string, MemberInfo> names = [with(StringComparer.Ordinal)];
        foreach (var member in members)
            if (!names.TryAdd(member.Name, member))
                throw new InvalidDataException($"Ambiguous Inspector member name in {type.FullName}: {member.Name}.");
        foreach (var member in members)
        foreach (var former in member.GetCustomAttributes<FormerlySerializedAsAttribute>(inherit: true))
        {
            var name = former.OldName;
            if (string.IsNullOrWhiteSpace(name) || name != name.Trim())
                throw new InvalidDataException($"{type.FullName}.{member.Name}: former Inspector name must be non-empty without surrounding whitespace.");
            if (!names.TryAdd(name, member) && !Equals(names[name], member))
                throw new InvalidDataException($"{type.FullName}: Inspector name '{name}' refers to multiple members.");
        }
        return names;
    }
}
