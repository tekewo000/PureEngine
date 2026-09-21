namespace PureEngine.Editor;

/// <summary>
/// バックグラウンドコンパイルの要求札。UIスレッドで発行し、採用時にも同じ札で有効性を確認する。
/// 世代番号は連続保存の新しさを表し、プロジェクト切替・終了後はどの札も無効になる。
/// </summary>
public sealed record UserCodeCompileTicket(long Generation, string ProjectRoot);

/// <summary>
/// バックグラウンドコンパイル1件の終了状態。結果の採用・解放の判断材料で、SceneやUIは持たない。
/// </summary>
public sealed record UserCodeCompileAttempt(
    UserCodeCompileTicket Ticket,
    UserCodeCompileResult? Result,
    bool Superseded,
    bool Canceled);

/// <summary>
/// ソース読み取り・コンパイルだけをバックグラウンドで行う。
/// Scene移行・結果採用は呼び出し側のUIスレッドで行い、その直前にも世代の有効性を確認する。
/// MainWindowは保留中の最新結果を所有し、新しい要求や終了で不要になれば解放する。
/// ProjectSession.OpenAsyncは起動完了またはキャンセルまでのTrackerを所有する。
/// </summary>
public sealed class UserCodeCompileTracker : IDisposable
{
    private readonly object _sync = new();
    private readonly Func<string, CancellationToken, Task<UserCodeCompileResult>> _compileAsync;
    private long _latest;
    private bool _disposed;

    /// <summary>この束縛が対象にするプロジェクトのルート。切替時はTrackerごと作り直す。</summary>
    public string ProjectRoot { get; }

    /// <param name="projectRoot">このTrackerが束縛されるプロジェクトのルートフォルダ。</param>
    /// <param name="compileAsync">
    /// バックグラウンドで実行するコンパイル処理。呼び出し自体もプールスレッドで行う。
    /// 未指定時は <see cref="UserCodeCompiler.CompileProject"/> を実行する。Scene・ComponentAssets・UIには触れないこと。
    /// テストでは完了順を制御できる差し替えを渡し、待ち時間に依存しない逆転・重なりの検証に使う。
    /// </param>
    public UserCodeCompileTracker(
        string projectRoot,
        Func<string, CancellationToken, Task<UserCodeCompileResult>>? compileAsync = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ProjectRoot = Path.GetFullPath(projectRoot);
        _compileAsync = compileAsync ?? DefaultCompileAsync;
    }

    /// <summary>新しい変更の要求札を発行する。UIスレッドで呼ぶ。連続保存では最新の札だけが有効になる。</summary>
    public UserCodeCompileTicket Request()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new UserCodeCompileTicket(++_latest, ProjectRoot);
        }
    }

    /// <summary>
    /// 採用時に札がまだ有効かを確認する。UIスレッドで、採用直前に呼ぶ。
    /// プロジェクト切替・終了（Dispose）後はすべて無効で、別プロジェクトの札も無効になる。
    /// </summary>
    public bool IsCurrent(UserCodeCompileTicket? ticket)
    {
        if (ticket is null) return false;
        lock (_sync)
        {
            return !_disposed && ticket.Generation == _latest
                && string.Equals(ticket.ProjectRoot, ProjectRoot, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 札に対応するコンパイルをバックグラウンドで実行する。UIスレッドを占有しない。
    /// 開始時点で札が古い場合は処理を実行せず、Supersededの試行を即返す（連続保存の collapse）。
    /// 開始後に古くなった場合は最後まで実行し、呼び出し側が採用時に <see cref="IsCurrent"/> で不採用にする。
    /// await で必ず観測すること。OperationCanceledExceptionはCanceledの試行に変える。
    /// それ以外の例外はそのまま送出し、呼び出し側で報告・旧状態維持・再試行する。
    /// このメソッド自体はDispatcherへ触れない。UIへの通知は呼び出し側がUIスレッドで行う。
    /// </summary>
    public Task<UserCodeCompileAttempt> CompileAsync(
        UserCodeCompileTicket ticket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        Func<string, CancellationToken, Task<UserCodeCompileResult>> compile;
        lock (_sync)
        {
            if (_disposed || ticket.Generation != _latest
                || !string.Equals(ticket.ProjectRoot, ProjectRoot, StringComparison.Ordinal))
                return Task.FromResult(new UserCodeCompileAttempt(ticket, null, Superseded: true, Canceled: false));
            compile = _compileAsync;
        }
        return RunAsync(ticket, ProjectRoot, compile, cancellationToken);
    }

    private static async Task<UserCodeCompileAttempt> RunAsync(
        UserCodeCompileTicket ticket,
        string root,
        Func<string, CancellationToken, Task<UserCodeCompileResult>> compile,
        CancellationToken cancellationToken)
    {
        // func全体をプールスレッドで実行する。呼び出し側（UI）の継続を待たず、
        // 開始前の取り消しではfuncを起動しない。func内部の除外は行わない。
        try
        {
            var result = await Task.Run(() => compile(root, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            return new UserCodeCompileAttempt(ticket, result, Superseded: false, Canceled: false);
        }
        catch (OperationCanceledException)
        {
            return new UserCodeCompileAttempt(ticket, null, Superseded: false, Canceled: true);
        }
    }

    /// <summary>
    /// 標準のコンパイル処理。ファイル列挙・読み取り・Roslyn・読込までをプールスレッドで行う。
    /// 開始前の取り消しだけを尊重し、実行中のRoslynを中断しない。古い結果の不採用は
    /// <see cref="IsCurrent"/> で保証するため、取り消しの伝達遅れが正しさを壊さない。
    /// </summary>
    private static Task<UserCodeCompileResult> DefaultCompileAsync(string root, CancellationToken cancellationToken) =>
        Task.FromResult(UserCodeCompiler.CompileProject(root));

    /// <summary>
    /// 不採用のコンパイル結果が持つ読込コードを解放する。どのスレッドからでも呼べる。失敗時は無視する。
    /// 失敗結果や空結果には読込コードがないため何もしない。採用済みの結果には呼ばない。
    /// </summary>
    public static void Release(UserCodeCompileResult? result)
    {
        var context = result?.LoadContext;
        if (context is null) return;
        try { context.Unload(); }
        catch { }
    }

    /// <summary>
    /// プロジェクト切替・終了時に呼ぶ。以後の要求を拒否し、進行中の試行はすべて不採用にする。
    /// 進行中のバックグラウンド処理自体は最後まで走るが、その結果は呼び出し側が解放する。
    /// UIには触れないため、閉じた画面や別プロジェクトを変更しない。待機もしない。
    /// </summary>
    public void Dispose()
    {
        lock (_sync) _disposed = true;
    }
}
