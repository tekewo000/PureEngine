using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
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
            Log.Engine.Error("Cannot start watching user code.", error);
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
        if (IsPlaying) SetFileStatus("C# changes detected. They will be applied after Stop.");
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
                Log.Engine.Error("Cannot apply C# changes. Keeping the previous state.", error);
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
        CancelSceneViewDrag();
        _pendingCompilation = null;
        if (!_compileTracker.IsCurrent(attempt.Ticket))
        {
            UserCodeCompileTracker.Release(compiled);
            return;
        }
        var selectedId = GetSelectedSceneObject()?.Id;
        DataAssetEditState? candidateAsset = null;
        Action<IServiceCollection>? assetConfigure = null;
        try
        {
            if (compiled.Success)
            {
                var registry = _components.CreateCandidateRegistry(compiled);
                candidateAsset = PrepareDataAssetReload(registry);
                var reloadedAssets = BuildProjectAssetStore(registry);
                if (reloadedAssets is not null) assetConfigure = services => services.AddSingleton(reloadedAssets);
            }
        }
        catch (Exception error)
        {
            UserCodeCompileTracker.Release(compiled);
            Log.Engine.Error("Cannot apply C# changes. Keeping the previous scene and data asset.", error);
            SetFileStatus(error.GetBaseException().Message, true);
            return;
        }
        var outcome = _reloadCoordinator.Apply(_editScene, _components, compiled, assetConfigure);
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
            _pressedPrefab = null;
            _assetEdit = candidateAsset;
            RefreshAssetOwned();
            RefreshHierarchy(selectedId);
            RefreshObjectInspector();
            UpdateSceneTitle();
        }
        if (outcome.Error is not null)
        {
            var message = outcome.Adopted ? "Failed to unload the previous code." : "Cannot apply C# changes. Keeping the previous state.";
            Log.Engine.Error(message, outcome.Error);
            SetFileStatus(message + " " + outcome.Error.GetBaseException().Message, true);
        }
        else if (outcome.Adopted)
        {
            var message = $"Applied C# changes: {_components.UserTypes.Count} class(es).";
            Log.Engine.Info(message);
            SetFileStatus(message);
        }
        else SetFileStatus("C# compilation failed. Keeping the previous state. Fix the errors and save.", true);
        RefreshProjectExplorer();
        DrainConsole();
    }

    private void QueuePendingUserCodeReload() => Dispatcher.UIThread.Post(FlushPendingUserCodeReload);
}