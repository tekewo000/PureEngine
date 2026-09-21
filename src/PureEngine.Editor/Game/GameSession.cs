using Microsoft.Extensions.DependencyInjection;

namespace PureEngine.Editor;

/// <summary>
/// 独立した一組のサービス群（provider＋明示 Scope）と Core 用の生成関数。
/// 組み込みの <see cref="GameServices.Configure"/> に加え、採用中のプロジェクト登録
/// （<see cref="ProjectGameServices"/>）があれば同じ登録処理から作り、編集と各 Play で別インスタンスを持つ。
/// 編集と Play、異なる Play の間で Singleton も含めて状態を共有しない。
/// Root provider から Scoped を直接解決せず、必ずこの Scope を通す。
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

    public static GameSession Create()
    {
        return CreateFromUserCode(ComponentAssets.CurrentUserCode);
    }

    /// <summary>
    /// 候補コードの登録からサービス群を作る。コード再読み込み・Project読み込みの準備用。
    /// まだ採用していないコンパイル結果を渡し、成功してから採用する。
    /// null はプロジェクト登録なし（組み込みのみ）を意味する。
    /// 登録口の曖昧・不正・登録中の例外は理由付きで投げ、provider は残さない。
    /// </summary>
    public static GameSession Create(UserCodeCompileResult? userCode)
    {
        return CreateFromUserCode(userCode);
    }

    private static GameSession CreateFromUserCode(UserCodeCompileResult? userCode)
    {
        var services = new ServiceCollection();
        GameServices.Configure(services);
        // プロジェクト登録の検証・適用。失敗時は provider を作らず報告する。
        ProjectGameServices.Apply(userCode, services);
        ServiceProvider? provider = null;
        try
        {
            provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true,
            });
            var scope = provider.CreateScope();
            var session = new GameSession(provider, scope);
            provider = null;
            return session;
        }
        finally
        {
            // Build 後の CreateScope 失敗など、provider だけ残った場合に解放する。
            // Build 自体が投げた場合は provider がなく、解放対象はない。
            provider?.Dispose();
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
