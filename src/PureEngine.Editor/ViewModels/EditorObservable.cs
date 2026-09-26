using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PureEngine.Editor;

/// <summary>Property notification shared by the editor's presentation models.</summary>
public abstract class EditorObservable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value)) return false;
        storage = value;
        Changed(name);
        return true;
    }
}
