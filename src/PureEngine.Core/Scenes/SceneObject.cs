using System.ComponentModel;

namespace PureEngine.Core;

/// <summary>Lifecycle whose execution order can be tuned per attachment.</summary>
public enum ComponentLifecycle
{
    Start,
    Update,
    Destroy,
}

/// <summary>
/// A named container in a <see cref="Scene"/>.
/// Holds plain C# instances attached via <see cref="Attach"/>; no base class required.
/// </summary>
public sealed class SceneObject : INotifyPropertyChanged
{
    private string _name;
    private readonly List<object> _components = [];
    private readonly Dictionary<object, Priorities> _priorities = new(ReferenceEqualityComparer.Instance);
    internal SceneRuntime? Runtime { get; set; }

    private sealed class Priorities
    {
        public int Start;
        public int Update;
        public int Destroy;
    }

    /// <summary>Attached component instances, in attach order.</summary>
    public IReadOnlyList<object> Components => _components.AsReadOnly();
    /// <summary>Stable identity used for references. Never changes on rename.</summary>
    public Guid Id { get; }
    /// <summary>Display name edited in the Editor.</summary>
    public string Name => _name;
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Creates an object with the given display name.</summary>
    public SceneObject(string name) : this(Guid.NewGuid(), name) { }

    internal SceneObject(Guid id, string name)
    {
        if (id == Guid.Empty) throw new ArgumentException("Object ID must not be empty.", nameof(id));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Id = id;
        _name = name.Trim();
    }

    /// <summary>Renames the object and notifies once. Identity (<see cref="Id"/>) is preserved.</summary>
    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = name.Trim();
        if (_name == normalized) return;
        _name = normalized;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
    }

    /// <summary>
    /// Attaches a plain C# instance. Each exact runtime type may be attached at most once.
    /// </summary>
    /// <exception cref="ArgumentNullException">Component is null.</exception>
    /// <exception cref="InvalidOperationException">Same exact type is already attached.</exception>
    public void Attach(object component)
    {
        ArgumentNullException.ThrowIfNull(component);
        if (_components.Any(c => c.GetType() == component.GetType()))
            throw new InvalidOperationException($"Already attached: {component.GetType().Name}");
        Runtime?.RegisterComponent(this, component);
        _components.Add(component);
    }

    /// <summary>Detaches the exact editing instance and its priorities. The caller owns disposal.</summary>
    /// <remarks>Runtime component detachment is not supported.</remarks>
    public bool Detach(object component)
    {
        ArgumentNullException.ThrowIfNull(component);
        if (Runtime is not null)
            throw new InvalidOperationException("Cannot detach components from a runtime scene.");
        var index = _components.FindIndex(candidate => ReferenceEquals(candidate, component));
        if (index < 0) return false;
        _components.RemoveAt(index);
        _priorities.Remove(component);
        return true;
    }

    /// <summary>Returns the first attached component assignable to <typeparamref name="T"/>, or null.</summary>
    public T? GetComponent<T>() where T : class
    {
        return _components.OfType<T>().FirstOrDefault();
    }

    /// <summary>Start Priority for one attachment. Defaults to 0; negatives are allowed.</summary>
    public int GetStartPriority(object component) => ReadPriorities(component).Start;

    /// <summary>Update Priority for one attachment. Defaults to 0; negatives are allowed.</summary>
    public int GetUpdatePriority(object component) => ReadPriorities(component).Update;

    /// <summary>Destroy Priority for one attachment. Defaults to 0; negatives are allowed.</summary>
    public int GetDestroyPriority(object component) => ReadPriorities(component).Destroy;

    /// <summary>
    /// Changes Start Priority. On an executing scene only the pre-Start setting is accepted;
    /// after the first Start the execution order is already fixed.
    /// </summary>
    public void SetStartPriority(object component, int priority)
    {
        EnsureLifecyclePresent(component, ComponentLifecycle.Start);
        Runtime?.SetComponentPriority(this, component, ComponentLifecycle.Start, priority);
        var stored = StoredPriorities(component);
        stored.Start = priority;
        PrunePriorities(component, stored);
    }

    /// <summary>
    /// Changes Update Priority. On an executing scene only the pre-Start setting is accepted;
    /// after the first Start the execution order is already fixed.
    /// </summary>
    public void SetUpdatePriority(object component, int priority)
    {
        EnsureLifecyclePresent(component, ComponentLifecycle.Update);
        Runtime?.SetComponentPriority(this, component, ComponentLifecycle.Update, priority);
        var stored = StoredPriorities(component);
        stored.Update = priority;
        PrunePriorities(component, stored);
    }

    /// <summary>
    /// Changes Destroy Priority. On an executing scene it is accepted until Destroy runs;
    /// destruction and post-Stop changes are rejected.
    /// </summary>
    public void SetDestroyPriority(object component, int priority)
    {
        EnsureLifecyclePresent(component, ComponentLifecycle.Destroy);
        Runtime?.SetComponentPriority(this, component, ComponentLifecycle.Destroy, priority);
        var stored = StoredPriorities(component);
        stored.Destroy = priority;
        PrunePriorities(component, stored);
    }

    private static void EnsureLifecyclePresent(object component, ComponentLifecycle kind)
    {
        ArgumentNullException.ThrowIfNull(component);
        var type = component.GetType();
        var present = kind switch
        {
            ComponentLifecycle.Start => ComponentSchema.GetStartMethod(type) is not null,
            ComponentLifecycle.Update => ComponentSchema.GetUpdateMethod(type) is not null,
            _ => ComponentSchema.GetDestroyMethod(type) is not null,
        };
        if (!present)
            throw new InvalidOperationException($"{type.FullName}: no [{kind}] lifecycle to prioritize.");
    }

    private Priorities ReadPriorities(object component)
    {
        ArgumentNullException.ThrowIfNull(component);
        if (!_components.Any(c => ReferenceEquals(c, component)))
            throw new InvalidOperationException("Component is not attached to this object.");
        return _priorities.TryGetValue(component, out var stored) ? stored : new Priorities();
    }

    private Priorities StoredPriorities(object component)
    {
        ArgumentNullException.ThrowIfNull(component);
        if (!_components.Any(c => ReferenceEquals(c, component)))
            throw new InvalidOperationException("Component is not attached to this object.");
        if (!_priorities.TryGetValue(component, out var stored))
        {
            stored = new Priorities();
            _priorities.Add(component, stored);
        }
        return stored;
    }

    internal (int Start, int Update, int Destroy) ReadAttachedPriorities(object component)
    {
        if (_priorities.TryGetValue(component, out var stored))
            return (stored.Start, stored.Update, stored.Destroy);
        return (0, 0, 0);
    }

    internal void RestorePriorities(object component, int start, int update, int destroy)
    {
        if (!_components.Any(c => ReferenceEquals(c, component)))
            throw new InvalidOperationException("Component is not attached to this object.");
        if (start == 0 && update == 0 && destroy == 0)
        {
            _priorities.Remove(component);
            return;
        }
        if (!_priorities.TryGetValue(component, out var stored))
        {
            stored = new Priorities();
            _priorities.Add(component, stored);
        }
        stored.Start = start;
        stored.Update = update;
        stored.Destroy = destroy;
    }

    private void PrunePriorities(object component, Priorities stored)
    {
        if (stored.Start == 0 && stored.Update == 0 && stored.Destroy == 0)
            _priorities.Remove(component);
    }
}
