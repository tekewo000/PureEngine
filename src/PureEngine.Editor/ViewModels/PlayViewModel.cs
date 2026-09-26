using Microsoft.Extensions.DependencyInjection;
using PureEngine.Core;
using PureEngine.Runtime;

namespace PureEngine.Editor;

/// <summary>Owns one editor Play run, its errors, and teardown; the view supplies native timing and input.</summary>
public sealed class PlayViewModel(EditorViewModel editor) : EditorObservable, IDisposable
{
    private int _loggedErrors;
    public PlaySession? Session { get; private set; }
    public bool IsPlaying => Session is not null;
    /// <summary>Run-owned localization service. Starts from the preview language; game code may switch it at runtime.</summary>
    public LocalizationService? PlayLocalization { get; private set; }
    public event Action? BeforeStart;
    public event Action? Started;
    public event Action? Stopping;
    public event Action? InputReset;
    public event Action? ReturnToScene;
    public event Action? FrameCompleted;

    /// <summary>
    /// Starts Play. Shows the reason instead of starting while Inspector errors exist.
    /// Starts from a copy of the scene being edited without modifying the edit side. Restores an operable state after failures.
    /// Opens the Game tab on success; Stop and automatic stops return to the Scene View tab.
    /// </summary>
    public void Start()
    {
        BeforeStart?.Invoke();
        if (Session is not null) return;
        var playBlock = EditorOperationGate.PlayBlockReason(alreadyPlaying: false, editor.FileBusy, editor.Inspector.HasInputErrors, editor.Documents.Localization.IsDirty);
        if (playBlock is not null)
        {
            var message = editor.FileBusy ? "Cannot play during file operations."
                : editor.Inspector.HasInputErrors ? "Cannot play. Fix the Inspector input errors."
                : playBlock;
            editor.SetStatus(message, true);
            return;
        }

        // Runs Clear on Play before Start without clearing that Play run start log.
        if (editor.Console.ClearOnPlay)
            editor.Console.Clear();

        PlaySession? session;
        LocalizationService? localization = null;
        try
        {
            // Each run gets a fresh snapshot; edits made during the run never reach files or other runs.
            var assets = EditorDocuments.BuildAssetStore(editor.ProjectFile, editor.Components.Registry);
            var prefabs = EditorDocuments.BuildPrefabCatalog(editor.ProjectFile);
            localization = new LocalizationService { CurrentLanguage = editor.Localization.PreviewLanguage };
            var configure = GameServices.ForProject(editor.Components);
            session = PlaySession.Prepare(editor.Documents.Scene.Current, editor.Components.Registry, services =>
            {
                configure(services);
                if (assets is not null) services.AddSingleton(assets);
                services.AddSingleton(prefabs);
                services.AddSingleton(localization);
                services.AddSingleton(EditorDocuments.BuildLocalizationStore(editor.ProjectFile));
            });
        }
        catch (Exception error)
        {
            Log.Engine.Error($"Cannot start Play: {error.GetBaseException().Message}", error);
            editor.SetStatus($"Cannot start Play: {error.GetBaseException().Message}", true);
            return;
        }

        Session = session;
        PlayLocalization = localization;
        _loggedErrors = 0;
        InputReset?.Invoke();
        try
        {
            Session.Start();
        }
        catch (Exception error)
        {
            FinishPlayAfterStartFailure($"Failed to start Play: {error.GetBaseException().Message}");
            return;
        }

        if (!Session.Runtime.IsRunning)
        {
            FinishPlayAfterStartFailure("Failed to start Play.");
            return;
        }

        Started?.Invoke();
        Changed(nameof(IsPlaying));
        Log.Engine.Info("Play started.");
        editor.SetStatus("Play started.");
    }

    /// <summary>Manual stop. Halts updates and shuts down and releases following the existing API. Double shutdown is a no-op.</summary>
    public bool Stop()
    {
        var session = Session;
        if (session is null) return true;
        Stopping?.Invoke();
        Exception? stopError = null;
        try
        {
            session.Stop();
        }
        catch (Exception error)
        {
            stopError = error;
        }

        try
        {
            session.Dispose();
        }
        catch (Exception error)
        {
            stopError = stopError is null ? error : new AggregateException(stopError, error);
        }
        finally
        {
            Session = null;
            PlayLocalization = null;
            Changed(nameof(IsPlaying));
        }

        // Pulls only unlogged entries
        // Pulls only unlogged entries into the Console so the same runtime errors are not logged twice.
        LogPendingRuntimeErrors(session);
        var errors = session.Runtime.Errors;
        if (stopError is not null)
        {
            Log.Engine.Error($"Error while stopping Play: {stopError.GetBaseException().Message}", stopError);
            editor.SetStatus($"Error while stopping Play: {stopError.GetBaseException().Message}{FormatPlayErrors(errors)}", true);
        }
        else if (errors.Count > 0)
        {
            Log.Engine.Error($"Stopped Play ({errors.Count} error(s))");
            editor.SetStatus($"Stopped Play ({errors.Count} error(s)){FormatPlayErrors(errors)}", true);
        }
        else
        {
            Log.Engine.Info("Stopped Play.");
            editor.SetStatus("Stopped Play.");
        }
        // Defers changes made during Play and applies them after Stop.
        editor.RequestReloadApply();
        InputReset?.Invoke();
        ReturnToScene?.Invoke();
        return stopError is null && errors.Count == 0;
    }

    /// <summary>One shared step for the timer and tests. Takes measured elapsed seconds.</summary>
    public void Step(float dt)
    {
        var session = Session;
        if (session is null) return;
        try
        {
            if (!session.Runtime.IsRunning)
                throw new InvalidOperationException("Runtime is not running.");
            session.Step(dt);
        }
        catch (Exception error)
        {
            FinishPlayAfterStepError(session, $"Play update failed: {error.GetBaseException().Message}");
            return;
        }

        LogPendingRuntimeErrors(session);
        if (!session.Runtime.IsRunning)
            FinishPlayAfterAutoStop(session);
        else
            FrameCompleted?.Invoke();
    }

    private void FinishPlayAfterStartFailure(string message)
    {
        var session = Session;
        Session = null;
        PlayLocalization = null;
        Stopping?.Invoke();

        var errors = session?.Runtime.Errors ?? [];
        Exception? cleanupError = null;
        if (session is not null)
        {
            try
            {
                session.Dispose();
            }
            catch (Exception error)
            {
                cleanupError = error;
            }
        }

        Changed(nameof(IsPlaying));
        if (session is not null) LogPendingRuntimeErrors(session);
        if (cleanupError is not null) Log.Engine.Error($"Failed to clean up after Play start: {cleanupError.GetBaseException().Message}", cleanupError);
        Log.Engine.Error(message);
        var detail = message + FormatPlayErrors(errors);
        if (cleanupError is not null) detail += $" (cleanup: {cleanupError.GetBaseException().Message})";
        editor.SetStatus(detail, true);
        editor.RequestReloadApply();
        InputReset?.Invoke();
    }

    private void FinishPlayAfterStepError(PlaySession session, string message)
    {
        Stopping?.Invoke();
        var errors = session.Runtime.Errors;
        Exception? cleanupError = null;
        try
        {
            session.Dispose();
        }
        catch (Exception error)
        {
            cleanupError = error;
        }
        finally
        {
            if (ReferenceEquals(Session, session)) Session = null;
            if (Session is null) PlayLocalization = null;
            Changed(nameof(IsPlaying));
        }

        LogPendingRuntimeErrors(session);
        if (cleanupError is not null) Log.Engine.Error($"Failed to clean up after Play update: {cleanupError.GetBaseException().Message}", cleanupError);
        Log.Engine.Error(message);
        var detail = message + FormatPlayErrors(errors);
        if (cleanupError is not null) detail += $" (cleanup: {cleanupError.GetBaseException().Message})";
        editor.SetStatus(detail, true);
        editor.RequestReloadApply();
        InputReset?.Invoke();
        ReturnToScene?.Invoke();
    }

    private void FinishPlayAfterAutoStop(PlaySession session)
    {
        Stopping?.Invoke();
        var errors = session.Runtime.Errors;
        Exception? cleanupError = null;
        try
        {
            session.Dispose();
        }
        catch (Exception error)
        {
            cleanupError = error;
        }
        finally
        {
            if (ReferenceEquals(Session, session)) Session = null;
            if (Session is null) PlayLocalization = null;
            Changed(nameof(IsPlaying));
        }

        LogPendingRuntimeErrors(session);
        if (cleanupError is not null)
        {
            Log.Engine.Error($"Play stopped (cleanup: {cleanupError.GetBaseException().Message})", cleanupError);
            editor.SetStatus($"Play stopped (cleanup: {cleanupError.GetBaseException().Message}){FormatPlayErrors(errors)}", true);
        }
        else if (errors.Count > 0)
        {
            Log.Engine.Error($"Play stopped with errors ({errors.Count})");
            editor.SetStatus($"Play stopped with errors ({errors.Count}){FormatPlayErrors(errors)}", true);
        }
        else
        {
            Log.Engine.Info("Play stopped.");
            editor.SetStatus("Play stopped.");
        }
        editor.RequestReloadApply();
        InputReset?.Invoke();
        ReturnToScene?.Invoke();
    }

    /// <summary>Internal stop that reliably shuts down and releases on window shutdown and similar paths. Leaves display to the caller.</summary>
    public void Dispose()
    {
        var session = Session;
        if (session is null) return;
        Stopping?.Invoke();
        try
        {
            session.Dispose();
        }
        finally
        {
            Session = null;
            PlayLocalization = null;
            Changed(nameof(IsPlaying));
            InputReset?.Invoke();
            try { LogPendingRuntimeErrors(session); } catch { /* Do not let shutdown-time logging failures block shutdown. */ }
        }
    }

    private static string FormatPlayErrors(IReadOnlyList<SceneRuntimeError> errors)
    {
        if (errors.Count == 0) return "";
        return Environment.NewLine + string.Join(Environment.NewLine, errors.Select(error =>
            $"{error.ObjectName}/{error.ComponentType.Name}.{error.MethodName}: {error.Exception}"));
    }

    /// <summary>Logs unlogged Runtime errors without duplicating Step/Stop/Dispose reports.</summary>
    private void LogPendingRuntimeErrors(PlaySession session)
    {
        var errors = session.Runtime.Errors;
        while (_loggedErrors < errors.Count)
        {
            var item = errors[_loggedErrors++];
            var message = $"{item.ObjectName}/{item.ComponentType.Name}.{item.MethodName}: {item.Exception.GetType().Name}: {item.Exception.Message}";
            // Original exception is kept for stack/inner details; the forwarding line is only the record location.
            Log.Engine.Error(message, item.Exception);
        }
    }
}
