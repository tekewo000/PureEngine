using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace PureEngine.Editor;

/// <summary>Shared discovery and invocation of the game's service registration entry point.</summary>
public static class GameServiceRegistration
{
    public const string RegistrarName = "ConfigureGameServices";

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

    /// <summary>Returns the unique valid registration, or null when none is present.</summary>
    public static MethodInfo? FindRegistrar(Assembly? assembly)
    {
        if (assembly is null) return null;
        Type[] types;
        try { types = assembly.GetTypes(); }
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
                if (method.Name == RegistrarName) named.Add(method);
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
        if (valid.Length == 1)
        {
            var owners = string.Join(", ", named.Select(m =>
                $"{m.DeclaringType?.FullName ?? "(unknown)"}{SignatureOf(m)}"));
            throw new InvalidOperationException(
                $"Ambiguous project service registration {RegistrarName} definition. Keep only one correct definition ({owners}). " +
                $"Expected: public static void {RegistrarName}(IServiceCollection services).");
        }
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
