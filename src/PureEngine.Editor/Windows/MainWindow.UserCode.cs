using Avalonia.Threading;
using PureEngine.Core;

namespace PureEngine.Editor;

public partial class MainWindow
{
    private UserCodeWatcher? _userCodeWatcher;

    private void StartUserCodeWatching()
    {
        StopUserCodeWatching();
        if (_project is null) return;
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
        _reloadCoordinator.ClearPending();
    }

    private void OnUserCodeReloadRequested()
    {
        _reloadCoordinator.RequestPending();
        if (IsPlaying) SetFileStatus("C#の変更を検知しました。Stop後に反映します。");
        FlushPendingUserCodeReload();
    }

    /// <summary>
    /// 操作受付・表示更新の担当。準備・採用・後片付けの中核は
    /// <see cref="UserCodeReloadCoordinator"/> に委ね、ここでは選択状態・Dirty維持と
    /// Console・ステータス表示・Explorer更新を行う。
    /// </summary>
    internal void ReloadUserCode()
    {
        if (_project is null || _reloadCoordinator.IsReloading) return;
        if (EditorOperationGate.ReloadBlockReason(IsPlaying, _fileBusy, HasInputErrors) is not null)
        {
            _reloadCoordinator.RequestPending();
            return;
        }

        var selectedId = (SceneObjects.SelectedItem as SceneObject)?.Id;
        var wasDirty = _editScene.IsDirty;
        UserCodeReloadOutcome outcome;
        try
        {
            // プロジェクト単位の所有者として現在のRegistryと編集用factoryを渡す。
            // コンパイル成功だけでは採用せず、Scene移行の準備成功後にCoordinator内で採用する。
            outcome = _reloadCoordinator.Reload(
                _editScene.Current,
                ComponentAssets.Registry,
                _editSession.Factory,
                _project.RootDirectory,
                IsPlaying,
                _fileBusy,
                HasInputErrors);
            if (outcome.IsDeferred)
            {
                // 競合により保留になった。状態はCoordinatorが保持する。
                return;
            }

            foreach (var diagnostic in outcome.Diagnostics)
            {
                var message = UserCodeCompiler.FormatDiagnostic(diagnostic);
                if (diagnostic.IsError) Log.Engine.Error(message);
                else Log.Engine.Warning(message);
            }

            if (!outcome.Success || outcome.MigratedScene is null)
            {
                if (outcome.FailureException is not null)
                {
                    var message = outcome.AdoptedBeforeFailure ? "旧コードの解放に失敗しました。" : "C#を反映できません。直前の状態を保持します。";
                    Log.Engine.Error(message, outcome.FailureException);
                    SetFileStatus($"{message} {outcome.FailureException.GetBaseException().Message}", true);
                }
                else
                {
                    SetFileStatus("C#のコンパイルに失敗しました。直前の状態を保持します。修正して保存してください。", true);
                }
                return;
            }

            var previous = _editScene.Replace(outcome.MigratedScene, _editScene.Path, wasDirty);
            SceneObjects.SelectedItem = null;
            SceneObjects.ItemsSource = _editScene.Current.Objects;
            SceneObjects.SelectedItem = _editScene.Current.Objects.FirstOrDefault(item => item.Id == selectedId);
            RefreshObjectInspector();
            UpdateSceneTitle();
            ComponentAssets.DisposeComponents(previous.Objects.SelectMany(item => item.Components));
            Log.Engine.Info($"C#を反映しました：{outcome.AttachableCount}クラス。");
            SetFileStatus($"C#を反映しました：{outcome.AttachableCount}クラス。");
        }
        catch (Exception error)
        {
            // Coordinator外の表示更新での失敗。編集Sceneの旧状態は保持されている。
            Log.Engine.Error("C#を反映できません。直前の状態を保持します。", error);
            SetFileStatus($"C#を反映できません。直前の状態を保持します。 {error.GetBaseException().Message}", true);
        }
        finally
        {
            RefreshProjectExplorer();
            DrainConsole();
        }
    }

    private void FlushPendingUserCodeReload()
    {
        if (_userCodeWatcher is not null && _reloadCoordinator.ShouldReloadNow(IsPlaying, _fileBusy, HasInputErrors))
            ReloadUserCode();
    }

    // Wait until the current text-change handler has finished updating its controls.
    private void QueuePendingUserCodeReload() => Dispatcher.UIThread.Post(FlushPendingUserCodeReload);
}
