using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace PureEngine.Editor;

/// <summary>Adapts successful editor compilations to the shared game service registration policy.</summary>
public static class ProjectGameServices
{
    public const string RegistrarName = GameServiceRegistration.RegistrarName;

    public static void Apply(UserCodeCompileResult? userCode, IServiceCollection services) =>
        GameServiceRegistration.Apply(GetAssembly(userCode), services);

    public static MethodInfo? FindRegistrar(UserCodeCompileResult? userCode) =>
        GameServiceRegistration.FindRegistrar(GetAssembly(userCode));

    private static Assembly? GetAssembly(UserCodeCompileResult? userCode)
    {
        if (userCode is { Success: false })
            throw new InvalidOperationException("Cannot apply service registration from a failed compilation.");
        return userCode?.LoadedAssembly;
    }
}
