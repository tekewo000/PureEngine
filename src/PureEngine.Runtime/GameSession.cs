using Microsoft.Extensions.DependencyInjection;

namespace PureEngine.Runtime;

/// <summary>
/// 独立した一組のサービス群（provider＋明示 Scope）と Core 用の生成関数。
/// 登録処理は外部から受け取り、編集と各 Play で別インスタンスを持つ。
/// 編集と Play、異なる Play の間で Singleton も含めて状態を共有しない。
/// Root provider から Scoped を直接解決せず、必ずこの Scope を通す。
/// Editor・Avaloniaに依存しない。ゲーム側の登録処理はconfigureとして受け取る。
/// </summary>
public sealed class GameSession : IDisposable
{
    private ServiceProvider? _provider;
    private IServiceScope? _scope;
    private bool _disposed;

    /// <summary>Core が知るのはこの生成関数のみ。MS DI への参照はここに留める。</summary>
    public Func<Type, object> Factory { get; }

    /// <summary>明示 Scope の provider。Scoped サービスの直接確認用。</summary>
    public IServiceProvider Services => _scope?.ServiceProvider
        ?? throw new ObjectDisposedException(nameof(GameSession));

    private GameSession(ServiceProvider provider, IServiceScope scope)
    {
        _provider = provider;
        _scope = scope;
        Factory = type => ActivatorUtilities.CreateInstance(Services, type);
    }

    /// <summary>
    /// 外部の登録処理から独立したサービス群を作る。
    /// 呼び出し側が、そのゲーム／プロジェクトのサービス登録処理を渡す。
    /// </summary>
    public static GameSession Create(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var services = new ServiceCollection();
        configure(services);
        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
        try { return new GameSession(provider, provider.CreateScope()); }
        catch (Exception error)
        {
            try { provider.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var scope = _scope;
        var provider = _provider;
        _scope = null;
        _provider = null;
        // Scope を先に、provider を後に終了する。Component の終了処理は所有者（PlaySession）が
        // 先に完了させてからここを呼ぶ。
        var errors = new List<Exception>();
        try { scope?.Dispose(); }
        catch (Exception error) { errors.Add(error); }
        try { provider?.Dispose(); }
        catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0) throw new AggregateException("Game services cleanup failed.", errors);
    }
}
