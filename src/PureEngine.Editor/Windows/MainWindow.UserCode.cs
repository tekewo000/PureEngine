using Avalonia.Controls;
using Avalonia.Threading;
using PureEngine.Core;

namespace PureEngine.Editor;

public partial class MainWindow
{
    private UserCodeWatcher? _userCodeWatcher;
    internal Task ReloadTask => ViewModel.Compilation.ReloadTask;

    private void InitCompilationModel()
    {
        ViewModel.Compilation.BeforeApply += CancelSceneViewDrag;
        ViewModel.Compilation.Adopted += OnUserCodeAdopted;
        ViewModel.Compilation.AttemptCompleted += OnUserCodeAttemptCompleted;
    }

    internal void SetCompileStatusForTest(TimeSpan elapsed) => ViewModel.Compilation.SetElapsed(elapsed);

    private void StartUserCodeWatching()
    {
        _userCodeWatcher?.Dispose();
        _userCodeWatcher = null;
        if (Project is null) return;
        if (ViewModel.Compilation.Tracker is null) ViewModel.Compilation.Start(Project, null);
        try
        {
            _userCodeWatcher = new UserCodeWatcher(Project.RootDirectory);
            _userCodeWatcher.ReloadRequested += OnUserCodeReloadRequested;
        }
        catch (Exception error)
        {
            Log.Engine.Error("Cannot start watching user code.", error);
            SetFileStatus(error.Message, true);
        }
    }

    private void StopUserCodeWatching()
    {
        _userCodeWatcher?.Dispose();
        _userCodeWatcher = null;
        ViewModel.Compilation.Dispose();
    }

    private void OnUserCodeReloadRequested()
    {
        if (IsPlaying) SetFileStatus("C# changes detected. They will be applied after Stop.");
        _ = ReloadUserCode();
    }

    internal Task ReloadUserCode() => ViewModel.Compilation.Reload();

    private void FlushPendingUserCodeReload() => ViewModel.Compilation.ApplyPending();

    private void OnUserCodeAdopted(Guid? selectedId)
    {
        // Native drag payloads must release the previous collectible assembly.
        _dragTypes = null;
        _assetPress = null;
        _pressedPrefab = null;
        RefreshTableAfterReload();
        RefreshDataAssetTableTypes(rescanRows: false);
        RefreshAssetOwned();
        SelectSceneObject(selectedId is { } id ? ViewModel.Hierarchy.Find(id) : null, focus: false);
        RefreshObjectInspector();
        UpdateSceneTitle();
    }

    private void OnUserCodeAttemptCompleted()
    {
        RefreshProjectExplorer();
        DrainConsole();
    }

    private void QueuePendingUserCodeReload() => Dispatcher.UIThread.Post(FlushPendingUserCodeReload);
}
