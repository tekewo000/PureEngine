using PureEngine.Core;

namespace PureEngine.Editor;

/// <summary>Owns pending code, generation checks, and adoption on the calling synchronization context.</summary>
public sealed class CompilationViewModel(EditorViewModel editor) : EditorObservable, IDisposable
{
    private UserCodeCompileAttempt? _pending;
    private TimeSpan? _elapsed;
    public UserCodeCompileTracker? Tracker { get; private set; }
    public UserCodeReloadCoordinator Coordinator { get; } = new();
    public Task ReloadTask { get; private set; } = Task.CompletedTask;
    public bool HasPending => _pending is not null;
    public bool IsApplying { get; private set; }
    public string StatusText { get; private set; } = "Compile: —";
    public event Action? BeforeApply;
    public event Action<Guid?>? Adopted;
    public event Action? AttemptCompleted;

    public void SetElapsed(TimeSpan? elapsed)
    {
        _elapsed = elapsed;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        StatusText = _elapsed is { } elapsed ? $"Compile: {elapsed.TotalSeconds:0.00}s" : "Compile: —";
        Changed(nameof(StatusText));
    }

    public void Start(ProjectFile? project, UserCodeIncrementalCompiler? cache)
    {
        Dispose();
        try
        {
            if (project is null) return;
            Tracker = new UserCodeCompileTracker(project.RootDirectory, cache: cache);
            cache = null;
        }
        finally
        {
            if (cache is not null)
            {
                try { cache.Dispose(); } catch { }
            }
        }
    }

    public void ReplaceTracker(UserCodeCompileTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        if (ReferenceEquals(Tracker, tracker)) return;
        Dispose();
        Tracker = tracker;
    }

    public Task Reload()
    {
        var tracker = Tracker;
        if (tracker is null) return Task.CompletedTask;
        StatusText = "Compiling...";
        Changed(nameof(StatusText));
        var ticket = tracker.Request();
        return ReloadTask = CompileAndQueue(tracker, ticket);
    }

    private async Task CompileAndQueue(UserCodeCompileTracker tracker, UserCodeCompileTicket ticket)
    {
        UserCodeCompileAttempt? attempt = null;
        try
        {
            attempt = await tracker.CompileAsync(ticket);
            if (!ReferenceEquals(tracker, Tracker) || !tracker.IsCurrent(ticket)
                || attempt.Canceled || attempt.Superseded) return;
            SetElapsed(attempt.Elapsed);
            if (attempt.Result?.Unchanged == true) return;
            UserCodeCompileTracker.Release(_pending?.Result);
            _pending = attempt;
            attempt = null;
            ApplyPending();
        }
        catch (Exception error)
        {
            if (ReferenceEquals(tracker, Tracker) && tracker.IsCurrent(ticket))
            {
                Log.Engine.Error("Cannot apply C# changes. Keeping the previous state.", error);
                editor.SetStatus(error.GetBaseException().Message, true);
                UpdateStatus();
            }
        }
        finally { UserCodeCompileTracker.Release(attempt?.Result); }
    }

    public UserCodeReloadOutcome? ApplyPending()
    {
        if (IsApplying) return null;
        IsApplying = true;
        try
        {
            if (_pending is not { Result: { } compiled } attempt || Tracker is null || Coordinator.IsReloading
                || EditorOperationGate.ReloadBlockReason(editor.Play.IsPlaying, editor.FileBusy, editor.Inspector.HasInputErrors) is not null) return null;
            if (editor.Documents.Prefab is not null && compiled.Success)
            {
                editor.SetStatus("C# changes are ready. Close Prefab Editor to apply them.");
                return null;
            }
            BeforeApply?.Invoke();
            _pending = null;
            if (!Tracker.IsCurrent(attempt.Ticket))
            {
                UserCodeCompileTracker.Release(compiled);
                return null;
            }
            var selectedId = editor.Hierarchy.Primary?.Ref.Id;
            var outcome = Coordinator.Apply(editor.Documents, editor.Components, compiled, editor.ProjectFile);
            foreach (var diagnostic in outcome.Diagnostics)
            {
                var message = UserCodeCompiler.FormatDiagnostic(diagnostic);
                if (diagnostic.IsError) Log.Engine.Error(message);
                else Log.Engine.Warning(message);
            }
            if (outcome.Adopted)
            {
                editor.RefreshAfterCodeAdoption();
                Adopted?.Invoke(selectedId);
            }
            if (outcome.Error is not null)
            {
                var message = outcome.Adopted ? "Failed to unload the previous code." : "Cannot apply C# changes. Keeping the previous state.";
                Log.Engine.Error(message, outcome.Error);
                editor.SetStatus(message + " " + outcome.Error.GetBaseException().Message, true);
            }
            else if (outcome.Adopted)
            {
                var message = $"Applied C# changes: {editor.Components.UserTypes.Count} class(es).";
                Log.Engine.Info(message);
                editor.SetStatus(message);
            }
            else editor.SetStatus("C# compilation failed. Keeping the previous state. Fix the errors and save.", true);
            AttemptCompleted?.Invoke();
            return outcome;
        }
        finally { IsApplying = false; }
    }

    public void Dispose()
    {
        Tracker?.Dispose();
        Tracker = null;
        UserCodeCompileTracker.Release(_pending?.Result);
        _pending = null;
    }
}
