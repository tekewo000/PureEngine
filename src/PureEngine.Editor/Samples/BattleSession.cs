namespace PureEngine.Editor.Samples;

/// <summary>
/// 一回のゲーム実行で共有する対戦状態。Scoped 登録の基本例。
/// 生成は DI が行い、後始末も DI（Scope 終了）が担当する。Component 側で Dispose しない。
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
