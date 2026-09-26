using Microsoft.Extensions.DependencyInjection;
using PureEngine.Core;
using PureEngine.Editor.Samples;

namespace PureEngine.Editor;

/// <summary>
/// Editor-side game service registration (sample). Editing and play share the same registration,
/// passed to <see cref="Runtime.GameSession"/>.Create and <see cref="Runtime.PlaySession"/>.Prepare.
/// Components themselves need no DI registration. Type registration into the existing ComponentRegistry remains a separate role.
/// ForProject and ForUserCode combine the built-in registrations with the registrations for the project.
/// </summary>
public static class GameServices
{
    public static Action<IServiceCollection> ForProject(ProjectComponents components) =>
        ForUserCode(components.ActiveUserCode);

    public static Action<IServiceCollection> ForUserCode(UserCodeCompileResult? userCode) => services =>
    {
        Configure(services);
        ProjectGameServices.Apply(userCode, services);
    };

    public static void Configure(IServiceCollection services)
    {
        services.AddScoped<IRandomService, RandomService>();
        services.AddScoped<BattleSession>();
        services.AddScoped<PrefabSpawner>();
        services.AddScoped<LocalizationService>();
    }
}
