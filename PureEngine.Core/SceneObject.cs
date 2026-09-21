using System.ComponentModel;

namespace PureEngine.Core;

public sealed class SceneObject : INotifyPropertyChanged
{
    private string _name;

    public SceneObject(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name.Trim();
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string Name => _name;
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = name.Trim();
        if (_name == normalized) return;
        _name = normalized;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
    }
}
