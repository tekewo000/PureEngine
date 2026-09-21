using PureEngine.Core;

namespace PureEngine.Editor;

public static class ComponentAssets
{
    public static IReadOnlyList<Type> Types { get; } =
        Array.AsReadOnly(new[] { typeof(Samples.PlayerStats), typeof(Samples.RoundSettings) });

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
