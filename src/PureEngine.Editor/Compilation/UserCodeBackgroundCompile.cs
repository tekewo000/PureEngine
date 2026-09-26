namespace PureEngine.Editor;

/// <summary>
/// Request ticket for a background compilation. Issued on the UI thread and validated with the same ticket on adoption.
/// The generation number represents the recency of consecutive saves; all tickets become invalid after a project switch or shutdown.
/// </summary>
public sealed record UserCodeCompileTicket(long Generation, string ProjectRoot);

/// <summary>
/// Completion state of a single background compilation. Used to decide adoption and release; holds no scene or UI.
/// Elapsed measures the wall-clock time from request to completion, including queue waits and skipped inputs.
/// </summary>
public sealed record UserCodeCompileAttempt(
    UserCodeCompileTicket Ticket,
    UserCodeCompileResult? Result,
    bool Superseded,
    bool Canceled,
    TimeSpan Elapsed);

/// <summary>
/// Runs only source reading and compilation in the background.
/// Scene migration and result adoption run on the caller's UI thread, validating the generation immediately before adoption.
/// CompilationViewModel owns the latest pending result and releases it when a new request or shutdown makes it unnecessary.
/// ProjectSession.OpenAsync owns the tracker until startup completes or is canceled.
/// Only one compilation runs at a time; queued requests collapse to the latest ticket.
/// </summary>
public sealed class UserCodeCompileTracker : IDisposable
{
    private readonly Lock _sync = new();
    private readonly Func<string, CancellationToken, Task<UserCodeCompileResult>>? _customCompile;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _active;
    private UserCodeIncrementalCompiler? _cache;
    private long _latest;
    private bool _disposed;

    /// <summary>The project root this binding targets. Recreate the tracker on project switches.</summary>
    public string ProjectRoot { get; }

    /// <param name="projectRoot">The project root folder this tracker is bound to.</param>
    /// <param name="compileAsync">
    /// The compilation work to run in the background. The invocation itself also runs on a pool thread.
    /// Uses the project incremental cache when unspecified. Do not touch the scene, ComponentAssets, or UI.
    /// In tests, pass a substitute that controls completion order to verify serialization without depending on delays.
    /// </param>
    /// <param name="cache">Existing incremental state to reuse, usually transferred from project open. The tracker takes ownership.</param>
    public UserCodeCompileTracker(
        string projectRoot,
        Func<string, CancellationToken, Task<UserCodeCompileResult>>? compileAsync = null,
        UserCodeIncrementalCompiler? cache = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        if (compileAsync is not null && cache is not null)
            throw new ArgumentException("Custom compilation and a shared cache cannot be combined.", nameof(cache));
        ProjectRoot = Path.GetFullPath(projectRoot);
        _customCompile = compileAsync;
        if (compileAsync is null)
        {
            if (cache is not null && !string.Equals(cache.ProjectRoot, ProjectRoot, StringComparison.Ordinal))
                throw new ArgumentException("Shared cache targets another project.", nameof(cache));
            _cache = cache ?? new UserCodeIncrementalCompiler(ProjectRoot);
        }
    }

    /// <summary>Takes ownership of the incremental cache, usually to transfer it from project open to the editor. Returns null for custom compilation.</summary>
    public UserCodeIncrementalCompiler? TakeCache()
    {
        lock (_sync)
        {
            var cache = _cache;
            _cache = null;
            return cache;
        }
    }

    /// <summary>Issues a request ticket for a new change. Call on the UI thread. Only the latest ticket stays valid across consecutive saves.</summary>
    public UserCodeCompileTicket Request()
    {
        CancellationTokenSource? active;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            active = _active;
            var ticket = new UserCodeCompileTicket(++_latest, ProjectRoot);
            try { active?.Cancel(); }
            catch { }
            return ticket;
        }
    }

    /// <summary>
    /// Checks whether the ticket is still valid on adoption. Call on the UI thread immediately before adoption.
    /// All tickets become invalid after a project switch or shutdown (Dispose), including tickets from other projects.
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
    /// Runs the compilation for the ticket in the background. Does not occupy the UI thread.
    /// Returns a superseded attempt without running the work when the ticket is already stale or disposed.
    /// Serializes executions; queued tickets collapse so only the latest runs after the active one finishes.
    /// Always observe the result with await. Translates OperationCanceledException into a canceled attempt.
    /// Rethrows any other exception so the caller can report it, keep the previous state, and retry.
    /// This method never touches the dispatcher. The caller notifies the UI on the UI thread.
    /// </summary>
    public async Task<UserCodeCompileAttempt> CompileAsync(
        UserCodeCompileTicket ticket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        lock (_sync)
        {
            if (_disposed || ticket.Generation != _latest
                || !string.Equals(ticket.ProjectRoot, ProjectRoot, StringComparison.Ordinal))
                return new UserCodeCompileAttempt(ticket, null, Superseded: true, Canceled: false, Elapsed: timer.Elapsed);
        }
        if (cancellationToken.IsCancellationRequested)
            return new UserCodeCompileAttempt(ticket, null, Superseded: false, Canceled: true, Elapsed: timer.Elapsed);
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new UserCodeCompileAttempt(ticket, null, Superseded: false, Canceled: true, Elapsed: timer.Elapsed);
        }
        Func<string, CancellationToken, Task<UserCodeCompileResult>>? custom;
        UserCodeIncrementalCompiler? cache;
        CancellationTokenSource running;
        lock (_sync)
        {
            if (_disposed || ticket.Generation != _latest
                || !string.Equals(ticket.ProjectRoot, ProjectRoot, StringComparison.Ordinal))
            {
                _gate.Release();
                return new UserCodeCompileAttempt(ticket, null, Superseded: true, Canceled: false, Elapsed: timer.Elapsed);
            }
            if (cancellationToken.IsCancellationRequested)
            {
                _gate.Release();
                return new UserCodeCompileAttempt(ticket, null, Superseded: false, Canceled: true, Elapsed: timer.Elapsed);
            }
            custom = _customCompile;
            cache = _cache;
            running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _active = running;
        }
        try
        {
            // Run the whole func on a pool thread without waiting for the caller (UI) continuation.
            var result = await Task.Run(() => RunCompileAsync(ProjectRoot, custom, cache, running.Token), running.Token)
                .ConfigureAwait(false);
            return new UserCodeCompileAttempt(ticket, result, Superseded: false, Canceled: false, Elapsed: timer.Elapsed);
        }
        catch (OperationCanceledException)
        {
            return new UserCodeCompileAttempt(ticket, null, Superseded: false, Canceled: true, Elapsed: timer.Elapsed);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_active, running)) _active = null;
            }
            try { running.Dispose(); }
            catch { }
            try { _gate.Release(); }
            catch { }
        }
    }

    private static Task<UserCodeCompileResult> RunCompileAsync(
        string root,
        Func<string, CancellationToken, Task<UserCodeCompileResult>>? custom,
        UserCodeIncrementalCompiler? cache,
        CancellationToken cancellationToken)
    {
        if (custom is not null) return custom(root, cancellationToken);
        ArgumentNullException.ThrowIfNull(cache);
        return Task.FromResult(cache.CompileProject(cancellationToken));
    }

    /// <summary>
    /// Releases the loaded code held by a rejected compilation result. Callable from any thread. Ignores failures.
    /// Does nothing for failed, unchanged, or empty results because they hold no loaded code. Never call for an adopted result.
    /// </summary>
    public static void Release(UserCodeCompileResult? result)
    {
        var context = result?.LoadContext;
        if (context is null) return;
        try { context.Unload(); }
        catch { }
    }

    /// <summary>
    /// Call on project switch or shutdown. Rejects later requests and invalidates every in-flight attempt.
    /// Cancels the active compilation when it observes cancellation; the caller releases any completed result.
    /// Touches no UI, so it never modifies closed views or other projects. Does not wait.
    /// </summary>
    public void Dispose()
    {
        CancellationTokenSource? active;
        UserCodeIncrementalCompiler? cache;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            active = _active;
            cache = _cache;
            _cache = null;
            _active = null;
        }
        try { active?.Cancel(); }
        catch { }
        if (cache is not null)
        {
            try { cache.Dispose(); }
            catch { }
        }
    }
}
