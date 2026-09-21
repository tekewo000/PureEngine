using Avalonia.Threading;
using PureEngine.Core;

namespace PureEngine.Editor;

public partial class MainWindow
{
    private UserCodeWatcher? _userCodeWatcher;
    private UserCodeCompileTracker? _compileTracker;
    private UserCodeCompileAttempt? _pendingCompilation;
    internal Task ReloadTask { get; private set; } = Task.CompletedTask;

    private void StartUserCodeWatching()
    {
        StopUserCodeWatching();
        if (_project is null) return;
        _compileTracker = new UserCodeCompileTracker(_project.RootDirectory);
        try
        {
            _userCodeWatcher = new UserCodeWatcher(_project.RootDirectory);
            _userCodeWatcher.ReloadRequested += OnUserCodeReloadRequested;
        }
        catch (Exception error)
        {
            Log.Engine.Error("ユーザーコードの監視を開始できません。", error);
            SetFileStatus(error.Message, true);
        }
    }

    private void StopUserCodeWatching()
    {
        _userCodeWatcher?.Dispose();
        _userCodeWatcher = null;
        _compileTracker?.Dispose();
        _compileTracker = null;
        UserCodeCompileTracker.Release(_pendingCompilation?.Result);
        _pendingCompilation = null;
    }

    private void OnUserCodeReloadRequested()
    {
        if (IsPlaying) SetFileStatus("C#の変更を検知しました。Stop後に反映します。");
        _ = ReloadUserCode();
    }

    internal Task ReloadUserCode()
    {
        var tracker = _compileTracker;
        if (tracker is null) return Task.CompletedTask;
        var ticket = tracker.Request();
        UserCodeCompileTracker.Release(_pendingCompilation?.Result);
        _pendingCompilation = null;
        return ReloadTask = CompileAndQueue(tracker, ticket);
    }

    private async Task CompileAndQueue(UserCodeCompileTracker tracker, UserCodeCompileTicket ticket)
    {
        UserCodeCompileAttempt? attempt = null;
        try
        {
            attempt = await tracker.CompileAsync(ticket);
            if (!ReferenceEquals(tracker, _compileTracker) || !tracker.IsCurrent(ticket)
                || attempt.Canceled || attempt.Superseded) return;
            _pendingCompilation = attempt;
            attempt = null; // The pending slot now owns the result.
            FlushPendingUserCodeReload();
        }
        catch (Exception error)
        {
            if (ReferenceEquals(tracker, _compileTracker) && tracker.IsCurrent(ticket))
            {
                Log.Engine.Error("C#を反映できません。直前の状態を保持します。", error);
                SetFileStatus(error.GetBaseException().Message, true);
            }
        }
        finally { UserCodeCompileTracker.Release(attempt?.Result); }
    }

    private void FlushPendingUserCodeReload()
    {
        if (_pendingCompilation is not { Result: { } compiled } attempt || _compileTracker is null
            || _reloadCoordinator.IsReloading
            || EditorOperationGate.ReloadBlockReason(IsPlaying, _fileBusy, HasInputErrors) is not null) return;
        _pendingCompilation = null;
        if (!_compileTracker.IsCurrent(attempt.Ticket))
        {
            UserCodeCompileTracker.Release(compiled);
            return;
        }
        var selectedId = (SceneObjects.SelectedItem as SceneObject)?.Id;
        var outcome = _reloadCoordinator.Apply(_editScene, _components, compiled);
        foreach (var diagnostic in outcome.Diagnostics)
        {
            var message = UserCodeCompiler.FormatDiagnostic(diagnostic);
            if (diagnostic.IsError) Log.Engine.Error(message);
            else Log.Engine.Warning(message);
        }
        if (outcome.Adopted)
        {
            // Clear drag references to the previous collectible assembly.
            _dragTypes = null;
            _assetPress = null;
            SceneObjects.SelectedItem = null;
            SceneObjects.ItemsSource = _editScene.Current.Objects;
            SceneObjects.SelectedItem = _editScene.Current.Objects.FirstOrDefault(item => item.Id == selectedId);
            RefreshObjectInspector();
            UpdateSceneTitle();
        }
        if (outcome.Error is not null)
        {
            var message = outcome.Adopted ? "旧コードの解放に失敗しました。" : "C#を反映できません。直前の状態を保持します。";
            Log.Engine.Error(message, outcome.Error);
            SetFileStatus(message + " " + outcome.Error.GetBaseException().Message, true);
        }
        else if (outcome.Adopted)
        {
            var message = $"C#を反映しました：{_components.UserTypes.Count}クラス。";
            Log.Engine.Info(message);
            SetFileStatus(message);
        }
        else SetFileStatus("C#のコンパイルに失敗しました。直前の状態を保持します。修正して保存してください。", true);
        RefreshProjectExplorer();
        DrainConsole();
    }

    private void QueuePendingUserCodeReload() => Dispatcher.UIThread.Post(FlushPendingUserCodeReload);
}