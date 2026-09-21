using Avalonia.Threading;
using PureEngine.Core;

namespace PureEngine.Editor;

public partial class MainWindow
{
    private UserCodeWatcher? _userCodeWatcher;
    private bool _userCodePendingReload;
    private bool _userCodeReloading;

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
        _userCodePendingReload = false;
    }

    private void OnUserCodeReloadRequested()
    {
        _userCodePendingReload = true;
        if (IsPlaying) SetFileStatus("C#の変更を検知しました。Stop後に反映します。");
        FlushPendingUserCodeReload();
    }

    internal void ReloadUserCode()
    {
        if (_project is null || _userCodeReloading) return;
        if (IsPlaying || _fileBusy || _invalidFields.Count > 0 || NameError.IsVisible)
        {
            _userCodePendingReload = true;
            return;
        }
        _userCodeReloading = true;
        _userCodePendingReload = false;
        UserCodeCompileResult? compiled = null;
        Scene? migrated = null;
        var adopted = false;
        try
        {
            compiled = UserCodeCompiler.CompileProject(_project.RootDirectory);
            foreach (var diagnostic in compiled.Diagnostics)
            {
                var message = UserCodeCompiler.FormatDiagnostic(diagnostic);
                if (diagnostic.IsError) Log.Engine.Error(message);
                else Log.Engine.Warning(message);
            }
            if (!compiled.Success)
            {
                SetFileStatus("C#のコンパイルに失敗しました。直前の状態を保持します。修正して保存してください。", true);
                return;
            }
            var registry = ComponentAssets.CreateRegistry(compiled);
            migrated = SceneCodeMigrator.Migrate(_scene, ComponentAssets.Registry, registry, _editSession.Factory);
            var previous = _scene;
            var selectedId = (SceneObjects.SelectedItem as SceneObject)?.Id;
            var wasDirty = _sceneDirty;
            ComponentAssets.SetUserCode(compiled);
            adopted = true;
            _scene = migrated;
            SceneObjects.SelectedItem = null;
            SceneObjects.ItemsSource = _scene.Objects;
            SceneObjects.SelectedItem = _scene.Objects.FirstOrDefault(item => item.Id == selectedId);
            RefreshObjectInspector();
            _sceneDirty = wasDirty;
            UpdateSceneTitle();
            ComponentAssets.DisposeComponents(previous.Objects.SelectMany(item => item.Components));
            Log.Engine.Info($"C#を反映しました：{compiled.AttachableTypes.Count}クラス。");
            SetFileStatus($"C#を反映しました：{compiled.AttachableTypes.Count}クラス。");
        }
        catch (Exception error)
        {
            var message = adopted ? "旧コードの解放に失敗しました。" : "C#を反映できません。直前の状態を保持します。";
            Log.Engine.Error(message, error);
            SetFileStatus($"{message} {error.GetBaseException().Message}", true);
        }
        finally
        {
            if (!adopted)
            {
                try
                {
                    if (migrated is not null) ComponentAssets.DisposeComponents(migrated.Objects.SelectMany(item => item.Components));
                }
                catch (Exception error) { Log.Engine.Error("採用できなかったC#の後片付けに失敗しました。", error); }
                finally { compiled?.LoadContext?.Unload(); }
            }
            _userCodeReloading = false;
            RefreshProjectExplorer();
            DrainConsole();
        }
    }

    private void FlushPendingUserCodeReload()
    {
        if (_userCodePendingReload && _userCodeWatcher is not null && !_userCodeReloading
            && !IsPlaying && !_fileBusy && _invalidFields.Count == 0 && !NameError.IsVisible)
            ReloadUserCode();
    }

    // Wait until the current text-change handler has finished updating its controls.
    private void QueuePendingUserCodeReload() => Dispatcher.UIThread.Post(FlushPendingUserCodeReload);
}
