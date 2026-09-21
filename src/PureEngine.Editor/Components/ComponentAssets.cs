using PureEngine.Core;

namespace PureEngine.Editor;

public static class ComponentAssets
{
    public static ComponentRegistry Registry { get; } = new();
    public static IReadOnlyList<Type> Types => Registry.Types;

    static ComponentAssets()
    {
        Registry.Register<Samples.PlayerStats>("sample.player-stats");
        Registry.Register<Samples.RoundSettings>("sample.round-settings");
    }

    public static bool CanAttach(SceneObject? target, Type? type) =>
        target is not null && type is not null && Types.Contains(type)
        && !target.Components.Any(component => component.GetType() == type);

    public static bool TryAttach(SceneObject? target, Type? type)
    {
        if (!CanAttach(target, type)) return false;
        target!.Attach(Activator.CreateInstance(type!)!);
        return true;
    }
}
