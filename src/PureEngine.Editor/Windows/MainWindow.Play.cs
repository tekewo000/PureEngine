using System.Diagnostics;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PureEngine.Core;

namespace PureEngine.Editor;

/// <summary>
/// EditorのPlay／Stop接続。編集中のSceneから独立したSceneRuntimeを作り、
/// Start→一定間隔のStep→Stop（Destroy＋Dispose）を行う。既存APIに従い二重終了を避ける。
/// </summary>
public partial class MainWindow
{
    private PlaySession? _play;
    private DispatcherTimer? _playTimer;
    private Stopwatch? _playClock;
    private TimeSpan _playLast;

    /// <summary>実行中かどうか。Playボタンの有効化・編集ガード・終了処理で使う。</summary>
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
    /// Play開始。Inspectorエラー時は開始せず理由を表示する。
    /// 編集中Sceneの複製で開始し、編集側は変更しない。失敗後も操作可能な状態へ戻す。
    /// </summary>
    internal void StartPlay()
    {
        if (_play is not null) return;
        if (_fileBusy)
        {
            SetFileStatus("ファイル操作中はPlayできません。", true);
            return;
        }
        if (_invalidFields.Count > 0 || NameError.IsVisible)
        {
            SetFileStatus("Playできません。Inspectorの入力エラーを修正してください。", true);
            return;
        }

        // Clear on PlayはStart前に実施し、そのPlayの開始ログを消さない。
        if (ConsoleClearOnPlay.IsChecked == true)
            ClearConsole();

        PlaySession? session;
        try
        {
            session = PlaySession.Prepare(_scene, ComponentAssets.Registry);
        }
        catch (Exception error)
        {
            Log.Error($"Playを開始できません: {error.GetBaseException().Message}", error);
            SetFileStatus($"Playを開始できません: {error.GetBaseException().Message}", true);
            return;
        }

        _play = session;
        _playLoggedErrorCount = 0;
        try
        {
            _play.Start();
        }
        catch (Exception error)
        {
            FinishPlayAfterStartFailure($"Playの開始に失敗しました: {error.GetBaseException().Message}");
            return;
        }

        if (!_play.Runtime.IsRunning)
        {
            FinishPlayAfterStartFailure("Playの開始に失敗しました");
            return;
        }

        _playClock = Stopwatch.StartNew();
        _playLast = TimeSpan.Zero;
        _playTimer?.Start();
        UpdatePlayUI();
        Log.Info("Playを開始しました。");
        SetFileStatus("Playを開始しました。");
    }

    /// <summary>手動停止。更新を止め、既存APIに従って終了・解放する。二重終了はno-op。</summary>
    internal bool StopPlay()
    {
        var session = _play;
        if (session is null) return true;
        _playTimer?.Stop();
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

        // 同じRuntimeエラーを重複出力しないよう、未記録分だけConsoleへ取り込む。
        LogPendingRuntimeErrors(session);
        var errors = session.Runtime.Errors;
        if (stopError is not null)
        {
            Log.Error($"Playの停止中にエラー: {stopError.GetBaseException().Message}", stopError);
            SetFileStatus($"Playの停止中にエラー: {stopError.GetBaseException().Message}{FormatPlayErrors(errors)}", true);
        }
        else if (errors.Count > 0)
        {
            Log.Error($"Playを停止しました（エラー{errors.Count}件）");
            SetFileStatus($"Playを停止しました（エラー{errors.Count}件）{FormatPlayErrors(errors)}", true);
        }
        else
        {
            Log.Info("Playを停止しました。");
            SetFileStatus("Playを停止しました。");
        }
        // Play中の変更は保留し、Stop後に反映する。
        FlushPendingUserCodeReload();
        return stopError is null && errors.Count == 0;
    }

    /// <summary>タイマー／テスト共用の1ステップ。実測の経過秒を渡す。</summary>
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
            FinishPlayAfterStepError(session, $"Playの更新に失敗しました: {error.GetBaseException().Message}");
            return;
        }

        LogPendingRuntimeErrors(session);
        if (!session.Runtime.IsRunning)
            FinishPlayAfterAutoStop(session);
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
        IReadOnlyList<SceneRuntimeError> errors = session?.Runtime.Errors ?? [];
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
        if (cleanupError is not null) Log.Error($"Play開始の後片付けに失敗しました: {cleanupError.GetBaseException().Message}", cleanupError);
        Log.Error(message);
        var detail = message + FormatPlayErrors(errors);
        if (cleanupError is not null) detail += $"（後片付け: {cleanupError.GetBaseException().Message}）";
        SetFileStatus(detail, true);
        FlushPendingUserCodeReload();
    }

    private void FinishPlayAfterStepError(PlaySession session, string message)
    {
        _playTimer?.Stop();
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
        if (cleanupError is not null) Log.Error($"Play更新の後片付けに失敗しました: {cleanupError.GetBaseException().Message}", cleanupError);
        Log.Error(message);
        var detail = message + FormatPlayErrors(errors);
        if (cleanupError is not null) detail += $"（後片付け: {cleanupError.GetBaseException().Message}）";
        SetFileStatus(detail, true);
        FlushPendingUserCodeReload();
    }

    private void FinishPlayAfterAutoStop(PlaySession session)
    {
        _playTimer?.Stop();
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
            Log.Error($"Playが停止しました（後片付け: {cleanupError.GetBaseException().Message}）", cleanupError);
            SetFileStatus($"Playが停止しました（後片付け: {cleanupError.GetBaseException().Message}）{FormatPlayErrors(errors)}", true);
        }
        else if (errors.Count > 0)
        {
            Log.Error($"Playがエラーで停止しました（{errors.Count}件）");
            SetFileStatus($"Playがエラーで停止しました（{errors.Count}件）{FormatPlayErrors(errors)}", true);
        }
        else
        {
            Log.Info("Playが停止しました。");
            SetFileStatus("Playが停止しました。");
        }
        FlushPendingUserCodeReload();
    }

    /// <summary>ウィンドウ終了時など、確実に終了・解放するための内部停止。表示は呼び出し側に任せる。</summary>
    private void ForceStopPlayForShutdown()
    {
        var session = _play;
        if (session is null) return;
        _playTimer?.Stop();
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
            try { LogPendingRuntimeErrors(session); } catch { /* 終了時の記録失敗で終了を妨げない。 */ }
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
        SetEditingEnabled(!playing);
    }

    /// <summary>実行中はシーン編集・切り替えを無効化し、Stop後に戻す。終了（Close）は無効化しない。</summary>
    private void SetEditingEnabled(bool enabled)
    {
        SceneObjects.IsEnabled = enabled;
        ObjectInspector.IsEnabled = enabled;
        ProjectTree.IsEnabled = enabled;
        ProjectFiles.IsEnabled = enabled;
        if (NewSceneMenu is not null) NewSceneMenu.IsEnabled = enabled;
        if (OpenSceneMenu is not null) OpenSceneMenu.IsEnabled = enabled;
        if (SaveSceneMenu is not null) SaveSceneMenu.IsEnabled = enabled;
        if (SaveSceneAsMenu is not null) SaveSceneAsMenu.IsEnabled = enabled;
        StartupSceneMenu.IsEnabled = enabled && _project is not null;
        AddObjectMenuItem.IsEnabled = enabled;
        DeleteObjectMenuItem.IsEnabled = enabled && SceneObjects.SelectedItem is not null;
    }

    private bool RejectWhenPlaying(string action)
    {
        if (!IsPlaying) return false;
        SetFileStatus($"{action}はPlay中にできません。先にStopしてください。", true);
        return true;
    }
}
