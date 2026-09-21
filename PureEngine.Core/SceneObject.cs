using System.ComponentModel;

namespace PureEngine.Core;

/// <summary>
/// A named container in a <see cref="Scene"/>.
/// Holds plain C# instances attached via <see cref="Attach"/>; no base class required.
/// </summary>
public sealed class SceneObject : INotifyPropertyChanged
{
    private string _name;
    private readonly List<object> _components = [];

    /// <summary>Attached component instances, in attach order.</summary>
    public IReadOnlyList<object> Components => _components.AsReadOnly();
    /// <summary>Stable identity used for references. Never changes on rename.</summary>
    public Guid Id { get; } = Guid.NewGuid();
    /// <summary>Display name edited in the Editor.</summary>
    public string Name => _name;
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Creates an object with the given display name.</summary>
    public SceneObject(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
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
        _components.Add(component);
    }

    /// <summary>Returns the first attached component assignable to <typeparamref name="T"/>, or null.</summary>
    public T? GetComponent<T>() where T : class
    {
        return _components.OfType<T>().FirstOrDefault();
    }
}
