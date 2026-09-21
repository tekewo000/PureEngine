using System.Reflection;
using System.Runtime.CompilerServices;
using PureEngine.Core.Attributes;

namespace PureEngine.Core;

/// <summary>
/// Reads attribute markers on plain C# components (detection only, no invocation).
/// </summary>
public static class ComponentSchema
{
    /// <summary>
    /// Finds the single valid <see cref="StartAttribute"/> method, or null when absent.
    /// Valid: instance, void, non-async, with no parameters.
    /// </summary>
    /// <exception cref="InvalidOperationException">Multiple valid methods found.</exception>
    public static MethodInfo? GetStartMethod(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        List<MethodInfo> found = [];

        foreach (var m in methods)
        {
            if (!m.IsDefined(typeof(StartAttribute), inherit: true))
                continue;

            var ps = m.GetParameters();
            var hasValidParameters = ps.Length == 0;

            if (!m.IsStatic
                && m.ReturnType == typeof(void)
                && !m.IsDefined(typeof(AsyncStateMachineAttribute), false)
                && hasValidParameters)
                found.Add(m);
        }

        if (found.Count > 1)
            throw new InvalidOperationException("Multiple [Start] methods found.");
        if (found.Count == 1)
            return found[0];
        return null;
    }

    /// <summary>
    /// Finds the single valid <see cref="UpdateAttribute"/> method, or null when absent.
    /// Valid: instance, void, non-async, with zero or one float parameter.
    /// </summary>
    /// <exception cref="InvalidOperationException">Multiple valid methods found.</exception>
    public static MethodInfo? GetUpdateMethod(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        List<MethodInfo> found = [];

        foreach (var m in methods)
        {
            if (!m.IsDefined(typeof(UpdateAttribute), inherit: true))
                continue;

            var ps = m.GetParameters();
            var hasValidParameters = ps.Length == 0 || (ps.Length == 1 && ps[0].ParameterType == typeof(float));

            if (!m.IsStatic
                && m.ReturnType == typeof(void)
                && !m.IsDefined(typeof(AsyncStateMachineAttribute), false)
                && hasValidParameters)
                found.Add(m);
        }

        if (found.Count > 1)
            throw new InvalidOperationException("Multiple [Update] methods found.");
        if (found.Count == 1)
            return found[0];
        return null;
    }

    /// <summary>
    /// Finds the single valid <see cref="DestroyAttribute"/> method, or null when absent.
    /// Valid: instance, void, non-async, with no parameters.
    /// </summary>
    /// <exception cref="InvalidOperationException">Multiple valid methods found.</exception>
    public static MethodInfo? GetDestroyMethod(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        List<MethodInfo> found = [];

        foreach (var m in methods)
        {
            if (!m.IsDefined(typeof(DestroyAttribute), inherit: true))
                continue;

            var ps = m.GetParameters();
            var hasValidParameters = ps.Length == 0;

            if (!m.IsStatic
                && m.ReturnType == typeof(void)
                && !m.IsDefined(typeof(AsyncStateMachineAttribute), false)
                && hasValidParameters)
                found.Add(m);
        }

        if (found.Count > 1)
            throw new InvalidOperationException("Multiple [Destroy] methods found.");
        if (found.Count == 1)
            return found[0];
        return null;
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
}
