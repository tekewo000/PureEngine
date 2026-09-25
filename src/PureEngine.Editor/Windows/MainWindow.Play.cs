using PureEngine.Runtime;
using System.Diagnostics;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using PureEngine.Core;

namespace PureEngine.Editor;

/// <summary>
/// Editor Play/Stop wiring. Builds a SceneRuntime independent of the scene being edited,
/// and runs Start, then Steps at fixed intervals, then Stop (Destroy + Dispose). Follows the existing API to avoid double shutdown.
/// </summary>
public partial class MainWindow
{
    private PlaySession? _play;
    private DispatcherTimer? _playTimer;
    private Stopwatch? _playClock;
    private TimeSpan _playLast;

    /// <summary>Whether a run is active. Used for Play button state, edit guards, and shutdown.</summary>
    public bool IsPlaying => _play is not null;

    internal PlaySession? ActivePlay => _play;

    private void InitPlayControls()
    {
        _playTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0 / 60.0) };
        _playTimer.Tick += OnPlayTick;
        UpdatePlayUI();
    }

    private void OnPlayClicked(object? sender, RoutedEventArgs e) => StartPlay();

    private void OnStopClicked(object? sender, RoutedEventArgs e) => StopPlay();

    /// <summary>
    /// Starts Play. Shows the reason instead of starting while Inspector errors exist.
    /// Starts from a copy of the scene being edited without modifying the edit side. Restores an operable state after failures.
    /// </summary>
    internal void StartPlay()
    {
        CancelSceneViewDrag();
        if (_play is not null) return;
        var playBlock = EditorOperationGate.PlayBlockReason(alreadyPlaying: false, _fileBusy, HasInputErrors);
        if (playBlock is not null)
        {
            var message = _fileBusy ? "Cannot play during file operations."
                : HasInputErrors ? "Cannot play. Fix the Inspector input errors."
                : playBlock;
            SetFileStatus(message, true);
            return;
        }

        if (IsPrefabEditing) ActivateEditorViewport(1);
        // Runs Clear on Play before Start without clearing that Play run start log.
        if (ConsoleClearOnPlay.IsChecked == true)
            ClearConsole();

        PlaySession? session;
        try
        {
            // Each run gets a fresh snapshot; edits made during the run never reach files or other runs.
            var assets = BuildProjectAssetStore(_components.Registry);
            var prefabs = BuildPrefabCatalog();
            var configure = GameServices.ForProject(_components);
            session = PlaySession.Prepare(_sceneDocument.Current, _components.Registry, services =>
            {
                configure(services);
                if (assets is not null) services.AddSingleton(assets);
                services.AddSingleton(prefabs);
            });
        }
        catch (Exception error)
        {
            Log.Engine.Error($"Cannot start Play: {error.GetBaseException().Message}", error);
            SetFileStatus($"Cannot start Play: {error.GetBaseException().Message}", true);
            return;
        }

        _play = session;
        _playLoggedErrorCount = 0;
        ResetGameInput();
        try
        {
            _play.Start();
        }
        catch (Exception error)
        {
            FinishPlayAfterStartFailure($"Failed to start Play: {error.GetBaseException().Message}");
            return;
        }

        if (!_play.Runtime.IsRunning)
        {
            FinishPlayAfterStartFailure("Failed to start Play.");
            return;
        }

        _playClock = Stopwatch.StartNew();
        _playLast = TimeSpan.Zero;
        _playTimer?.Start();
        UpdatePlayUI();
        Log.Engine.Info("Play started.");
        SetFileStatus("Play started.");
    }

    /// <summary>Manual stop. Halts updates and shuts down and releases following the existing API. Double shutdown is a no-op.</summary>
    internal bool StopPlay()
    {
        var session = _play;
        if (session is null) return true;
        _playTimer?.Stop();
        CancelGamePress();
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
            _play = null;
            _playClock?.Stop();
            _playClock = null;
            UpdatePlayUI();
        }

        // Pulls only unlogged entries into the Console so the same runtime errors are not logged twice.
        LogPendingRuntimeErrors(session);
        var errors = session.Runtime.Errors;
        if (stopError is not null)
        {
            Log.Engine.Error($"Error while stopping Play: {stopError.GetBaseException().Message}", stopError);
            SetFileStatus($"Error while stopping Play: {stopError.GetBaseException().Message}{FormatPlayErrors(errors)}", true);
        }
        else if (errors.Count > 0)
        {
            Log.Engine.Error($"Stopped Play ({errors.Count} error(s))");
            SetFileStatus($"Stopped Play ({errors.Count} error(s)){FormatPlayErrors(errors)}", true);
        }
        else
        {
            Log.Engine.Info("Stopped Play.");
            SetFileStatus("Stopped Play.");
        }
        // Defers changes made during Play and applies them after Stop.
        FlushPendingUserCodeReload();
        ResetGameInput();
        return stopError is null && errors.Count == 0;
    }

    /// <summary>One shared step for the timer and tests. Takes measured elapsed seconds.</summary>
    internal void StepPlayOnce(float dt)
    {
        var session = _play;
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
            ValidateGameInput();
    }

    private void OnPlayTick(object? sender, EventArgs e)
    {
        var session = _play;
        var clock = _playClock;
        if (session is null || clock is null) return;
        var now = clock.Elapsed;
        var dt = (float)(now - _playLast).TotalSeconds;
        _playLast = now;
        if (!float.IsFinite(dt) || dt < 0) dt = 0;
        StepPlayOnce(dt);
    }

    private void FinishPlayAfterStartFailure(string message)
    {
        var session = _play;
        _play = null;
        _playTimer?.Stop();
        _playClock?.Stop();
        _playClock = null;
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

        UpdatePlayUI();
        if (session is not null) LogPendingRuntimeErrors(session);
        if (cleanupError is not null) Log.Engine.Error($"Failed to clean up after Play start: {cleanupError.GetBaseException().Message}", cleanupError);
        Log.Engine.Error(message);
        var detail = message + FormatPlayErrors(errors);
        if (cleanupError is not null) detail += $" (cleanup: {cleanupError.GetBaseException().Message})";
        SetFileStatus(detail, true);
        FlushPendingUserCodeReload();
        ResetGameInput();
    }

    private void FinishPlayAfterStepError(PlaySession session, string message)
    {
        _playTimer?.Stop();
        CancelGamePress();
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
            if (ReferenceEquals(_play, session)) _play = null;
            _playClock?.Stop();
            _playClock = null;
            UpdatePlayUI();
        }

        LogPendingRuntimeErrors(session);
        if (cleanupError is not null) Log.Engine.Error($"Failed to clean up after Play update: {cleanupError.GetBaseException().Message}", cleanupError);
        Log.Engine.Error(message);
        var detail = message + FormatPlayErrors(errors);
        if (cleanupError is not null) detail += $" (cleanup: {cleanupError.GetBaseException().Message})";
        SetFileStatus(detail, true);
        FlushPendingUserCodeReload();
        ResetGameInput();
    }

    private void FinishPlayAfterAutoStop(PlaySession session)
    {
        _playTimer?.Stop();
        CancelGamePress();
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
            if (ReferenceEquals(_play, session)) _play = null;
            _playClock?.Stop();
            _playClock = null;
            UpdatePlayUI();
        }

        LogPendingRuntimeErrors(session);
        if (cleanupError is not null)
        {
            Log.Engine.Error($"Play stopped (cleanup: {cleanupError.GetBaseException().Message})", cleanupError);
            SetFileStatus($"Play stopped (cleanup: {cleanupError.GetBaseException().Message}){FormatPlayErrors(errors)}", true);
        }
        else if (errors.Count > 0)
        {
            Log.Engine.Error($"Play stopped with errors ({errors.Count})");
            SetFileStatus($"Play stopped with errors ({errors.Count}){FormatPlayErrors(errors)}", true);
        }
        else
        {
            Log.Engine.Info("Play stopped.");
            SetFileStatus("Play stopped.");
        }
        FlushPendingUserCodeReload();
        ResetGameInput();
    }

    /// <summary>Internal stop that reliably shuts down and releases on window shutdown and similar paths. Leaves display to the caller.</summary>
    private void ForceStopPlayForShutdown()
    {
        var session = _play;
        if (session is null) return;
        _playTimer?.Stop();
        CancelGamePress();
        try
        {
            session.Dispose();
        }
        finally
        {
            _play = null;
            _playClock?.Stop();
            _playClock = null;
            UpdatePlayUI();
            ResetGameInput();
            try { LogPendingRuntimeErrors(session); } catch { /* Do not let shutdown-time logging failures block shutdown. */ }
        }
    }

    private static string FormatPlayErrors(IReadOnlyList<SceneRuntimeError> errors)
    {
        if (errors.Count == 0) return "";
        return Environment.NewLine + string.Join(Environment.NewLine, errors.Select(error =>
            $"{error.ObjectName}/{error.ComponentType.Name}.{error.MethodName}: {error.Exception}"));
    }

    private void UpdatePlayUI()
    {
        var playing = _play is not null;
        PlayButton.IsEnabled = !playing;
        StopButton.IsEnabled = playing;
        GamePlaceholder.IsVisible = !playing;
        SetEditingEnabled(!playing);
        UpdatePrefabEditorChrome();
    }

    /// <summary>Disables scene editing and switching while running, then restores them after Stop. Never disables closing (Close).</summary>
    private void SetEditingEnabled(bool enabled)
    {
        SceneObjects.IsEnabled = enabled;
        ObjectInspector.IsEnabled = enabled;
        DataAssetInspector.IsEnabled = enabled;
        ProjectTree.IsEnabled = enabled;
        ProjectFiles.IsEnabled = enabled;
        NewSceneMenu?.IsEnabled = enabled;
        OpenSceneMenu?.IsEnabled = enabled;
        SaveSceneMenu?.IsEnabled = enabled;
        SaveSceneAsMenu?.IsEnabled = enabled;
        StartupSceneMenu.IsEnabled = enabled && _project is not null;
        AddObjectMenuItem.IsEnabled = enabled;
        AddUiMenuItem.IsEnabled = enabled;
        DeleteObjectMenuItem.IsEnabled = enabled && GetSelectedSceneObject() is { } item && !IsPrefabRoot(item);
        SavePrefabMenuItem.IsEnabled = enabled && GetSelectedSceneObject() is not null;
        UpdateDataAssetTableChrome();
    }

    private bool RejectWhenPlaying(string action)
    {
        if (!IsPlaying) return false;
        SetFileStatus($"Cannot {action} while playing. Stop first.", true);
        return true;
    }
}
