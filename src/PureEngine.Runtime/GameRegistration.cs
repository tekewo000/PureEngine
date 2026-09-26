using Microsoft.Extensions.DependencyInjection;
using PureEngine.Core;
using PureEngine.Editor.Samples;

namespace PureEngine.Runtime;

/// <summary>The same built-in component identities and scoped services in editing and packaged games.</summary>
public static class GameRegistration
{
    public static void RegisterBuiltins(ComponentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register<Transform>("core.transform");
        registry.Register<UiElement>("core.ui-element");
        registry.Register<Image>("core.image");
        registry.Register<Button>("core.button");
        registry.Register<Text>("core.text");
        registry.Register<PlayerStats>("sample.player-stats");
        registry.Register<RoundSettings>("sample.round-settings");
        registry.Register<InjectedPlayer>("sample.injected-player");
    }

    public static void Configure(IServiceCollection services)
    {
        services.AddScoped<IRandomService, RandomService>();
        services.AddScoped<BattleSession>();
        services.AddScoped<PrefabSpawner>();
        services.AddScoped<LocalizationService>();
    }
}
