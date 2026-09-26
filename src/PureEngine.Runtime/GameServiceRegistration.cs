using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace PureEngine.Editor;

/// <summary>
/// Project-side game service registration entry point. Finds and applies exactly one
/// <c>public static void ConfigureGameServices(IServiceCollection services)</c> in user code.
/// Existing projects without a registration keep running on built-in registrations only.
/// Ambiguous or invalid entry points are reported with a reason and never adopted.
/// Core still accepts only Func(Type, object) and does not depend on MS DI.
/// </summary>
public static class GameServiceRegistration
{
    public const string RegistrarName = "ConfigureGameServices";

    /// <summary>
    /// Applies the user-code registration to services. Does nothing when there is no registration.
    /// Reports ambiguous, invalid, and in-registration exceptions as InvalidOperationException.
    /// </summary>
    public static void Apply(Assembly? assembly, IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var registrar = FindRegistrar(assembly);
        if (registrar is null) return;
        try
        {
            registrar.Invoke(null, [services]);
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            throw new InvalidOperationException(
                $"Failed to apply project service registration {registrar.DeclaringType?.FullName}.{registrar.Name}: {error.InnerException.GetBaseException().Message}",
                error.InnerException);
        }
        catch (Exception error) when (error is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Failed to apply project service registration {registrar.DeclaringType?.FullName}.{registrar.Name}: {error.GetBaseException().Message}",
                error);
        }
    }

    /// <summary>
    /// Finds the registration method. Returns null when absent. Throws with a reason when ambiguous or invalid.
    /// Also returns null when no game assembly is present.
    /// </summary>
    public static MethodInfo? FindRegistrar(Assembly? assembly)
    {
        if (assembly is null) return null;

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException error)
        {
            throw new InvalidOperationException(
                $"Failed to get project type list: {error.GetBaseException().Message}", error);
        }

        var named = new List<MethodInfo>();
        foreach (var type in types)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            foreach (var method in methods)
            {
                if (method.Name == RegistrarName)
                    named.Add(method);
            }
        }

        if (named.Count == 0) return null;

        static bool IsValid(MethodInfo method) =>
            method.IsStatic && method.IsPublic
            && method.ReturnType == typeof(void)
            && method.GetParameters() is { Length: 1 } parameters
            && parameters[0].ParameterType == typeof(IServiceCollection)
            && !method.ContainsGenericParameters
            && !method.IsDefined(typeof(System.Runtime.CompilerServices.AsyncStateMachineAttribute), false)
            && method.DeclaringType is { ContainsGenericParameters: false };

        var valid = (MethodInfo[])[.. named.Where(IsValid)];
        if (valid.Length == 1 && named.Count == 1) return valid[0];

        if (valid.Length > 1)
        {
            var owners = string.Join(", ", valid.Select(m => m.DeclaringType?.FullName ?? "(unknown)"));
            throw new InvalidOperationException(
                $"Multiple project service registrations {RegistrarName} found ({owners}). Define only one per project.");
        }

        // Even with one valid match, multiple same-named methods count as ambiguous.
        if (valid.Length == 1)
        {
            var owners = string.Join(", ", named.Select(m =>
                $"{m.DeclaringType?.FullName ?? "(unknown)"}{SignatureOf(m)}"));
            throw new InvalidOperationException(
                $"Ambiguous project service registration {RegistrarName} definition. Keep only one correct definition ({owners}). " +
                $"Expected: public static void {RegistrarName}(IServiceCollection services).");
        }

        // Same-named methods exist but none is a correct definition. Reports the expected and actual formats as invalid.
        var found = string.Join(", ", named.Select(m =>
            $"{m.DeclaringType?.FullName ?? "(unknown)"}{SignatureOf(m)}"));
        throw new InvalidOperationException(
            $"Invalid project service registration {RegistrarName} format ({found}). " +
            $"Expected: public static void {RegistrarName}(IServiceCollection services).");
    }

    private static string SignatureOf(MethodInfo method)
    {
        var visibility = method.IsPublic ? "public" : "non-public";
        var staticality = method.IsStatic ? "static" : "instance";
        var parameters = string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name));
        return $" [{visibility} {staticality} {method.ReturnType.Name}({parameters})]";
    }
}
