namespace PureEngine.Editor.Samples;

/// <summary>
/// Battle state shared by a single game run. Basic example of scoped registration.
/// DI handles creation and cleanup (scope exit). Never dispose from a component.
/// </summary>
public sealed class BattleSession : IDisposable
{
    private bool _disposed;

    public int Members { get; private set; }

    public bool IsDisposed => _disposed;

    public int DisposeCalls { get; private set; }

    public void Join() => Members++;

    public void Dispose()
    {
        if (_disposed) throw new InvalidOperationException("BattleSession must be disposed once.");
        _disposed = true;
        DisposeCalls++;
    }
}
