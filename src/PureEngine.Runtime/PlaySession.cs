using Microsoft.Extensions.DependencyInjection;
using PureEngine.Core;

namespace PureEngine.Runtime;

/// <summary>
/// 一回の Play（単体実行を含む）。Play 用 provider・Scope を作り、factory を SceneRuntime に渡す。
/// 生成・復元・検証の成功後に Start し、Stop では Runtime の終了処理を完了してから Scope・provider を終了する。
/// 準備失敗時も生成済みの所有資源を解放し、元の例外と後始末中の例外を保持する。
/// Editor・Avaloniaに依存しない。Registryと登録処理は外部から受け取る。
/// </summary>
public sealed class PlaySession : IDisposable
{
    private GameSession? _services;
    public SceneRuntime Runtime { get; }

    private PlaySession(GameSession services, SceneRuntime runtime)
    {
        _services = services;
        Runtime = runtime;
        runtime.Stopped += ReleaseServices;
    }

    /// <summary>
    /// Play 用の独立したサービス群で準備する。Start は呼ばない。
    /// registryは呼び出し側のプロジェクト所有から渡し、configureはゲーム用サービス登録を受け取る。
    /// </summary>
    public static PlaySession Prepare(Scene source, ComponentRegistry registry, Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(configure);
        var services = GameSession.Create(configure);
        SceneRuntime runtime;
        try
        {
            runtime = new SceneRuntime(source, registry, services.Factory);
        }
        catch (Exception preparationError)
        {
            // Clone 時の生成済み Component は serializer／runtime が逆順で解放済み。
            // ここでは Play 用 Scope・provider を終了し、例外を保持する。
            try
            {
                services.Dispose();
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Play preparation and cleanup failed.", preparationError, cleanupError);
            }
            throw;
        }
        return new PlaySession(services, runtime);
    }

    public void Start() => Runtime.Start();

    public void Step(float dt) => Runtime.Step(dt);

    public void Stop() => Runtime.Stop();

    public void Dispose() => Stop();

    private void ReleaseServices()
    {
        var services = _services;
        _services = null;
        // Runtime.Stop may defer termination until the current callback returns.
        // Automatic stops on lifecycle failure and direct Runtime.Stop also reach this point.
        services?.Dispose();
    }
}
