using PureEngine.Core;
using PureEngine.Core.Components;

namespace PureEngine.Editor;

public static class ComponentAssets
{
    /// <summary>Releases editing instances without running game lifecycle callbacks.</summary>
    internal static void DisposeComponents(IEnumerable<object> components)
    {
        var errors = new List<Exception>();
        foreach (var component in components.Distinct(ReferenceEqualityComparer.Instance).Reverse())
        {
            if (component is not IDisposable disposable) continue;
            try { disposable.Dispose(); }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count != 0) throw new AggregateException("Editor components cleanup failed.", errors);
    }

    /// <summary>
    /// Built-in type registration policy (A4): each project owner (ProjectComponents) registers
    /// into its own registry on creation. Holds no shared static registration. Survives user.* replacement.
    /// Transform, UiElement, Image, and Button follow the same search, add, and save path as ordinary components.
    /// </summary>
    public static void RegisterBuiltins(ComponentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register<Transform>("core.transform");
        registry.Register<UiElement>("core.ui-element");
        registry.Register<global::Image>("core.image");
        registry.Register<Button>("core.button");
        registry.Register<Samples.PlayerStats>("sample.player-stats");
        registry.Register<Samples.RoundSettings>("sample.round-settings");
        registry.Register<Samples.InjectedPlayer>("sample.injected-player");
    }

    /// <summary>Filters Inspector add candidates by substring match on type name, full name, and type ID. Case-insensitive.</summary>
    public static IReadOnlyList<(Type Type, string TypeId)> SearchCandidates(ComponentRegistry registry, string? query)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var text = (query ?? "").Trim();
        List<(Type Type, string TypeId)> found = [];
        foreach (var id in registry.Ids.Order(StringComparer.Ordinal))
        {
            var type = registry.GetType(id);
            var name = type.Name ?? "";
            var fullName = type.FullName ?? name;
            if (text.Length == 0
                || name.Contains(text, StringComparison.OrdinalIgnoreCase)
                || fullName.Contains(text, StringComparison.OrdinalIgnoreCase)
                || id.Contains(text, StringComparison.OrdinalIgnoreCase))
                found.Add((type, id));
        }
        return found;
    }

    /// <summary>
    /// Builds a validation candidate from the current non-user.* registrations plus a new compilation result. Never modifies the caller's owner.
    /// Passes it to scene restore and migration validation before adoption. Keeps the old registrations on failure.
    /// </summary>
    internal static ComponentRegistry CreateCandidateRegistry(ComponentRegistry current, UserCodeCompileResult? result)
    {
        ArgumentNullException.ThrowIfNull(current);
        var registry = new ComponentRegistry();
        foreach (var id in current.Ids.Where(id => !id.StartsWith("user.", StringComparison.Ordinal)))
            registry.RegisterType(current.GetType(id), id);
        if (result is { Success: true })
            foreach (var type in result.AttachableTypes)
                registry.RegisterType(type, result.GetTypeId(type));
        return registry;
    }

    public static bool CanAttach(ComponentRegistry registry, SceneObject? target, Type? type)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return target is not null && type is not null && registry.Types.Contains(type)
            && !target.Components.Any(component => component.GetType() == type);
    }

    public static bool TryAttach(ComponentRegistry registry, SceneObject? target, Type? type, Func<Type, object>? factory = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (!CanAttach(registry, target, type)) return false;
        // Component creation point (editing): creates a new editing instance via drag-and-drop.
        // Without a factory, uses the conventional parameterless creation; with a factory, uses it for constructor injection.
        // Reports factory failures without hiding them via a parameterless retry. The creator releases on acceptance failure.
        object component;
        if (factory is null)
        {
            component = Activator.CreateInstance(type!)!;
        }
        else
        {
            object? created;
            try
            {
                created = factory(type!);
            }
            catch (Exception error)
            {
                throw new InvalidOperationException(
                    $"Failed to create component {type!.FullName} for object '{target!.Name}'.", error);
            }
            if (created is null)
                throw new InvalidOperationException($"Component factory returned null for {type!.FullName}.");
            if (created.GetType() != type)
                throw new InvalidOperationException(
                    $"Component factory returned {created.GetType().FullName} instead of {type!.FullName}.");
            component = created;
        }
        try
        {
            target!.Attach(component);
        }
        catch
        {
            if (component is IDisposable disposable)
            {
                try { disposable.Dispose(); }
                catch { /* Prefer the original Attach failure. */ }
            }
            throw;
        }
        return true;
    }
}
