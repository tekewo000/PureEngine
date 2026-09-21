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
        GameSession? candidateServices = null;
        var adopted = false;
        Exception? preparationError = null;
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
            // 新コードの登録から候補サービス群を作る。曖昧・不正・登録中の失敗は旧状態を維持する。
            try
            {
                candidateServices = GameSession.Create(compiled);
            }
            catch (Exception error)
            {
                preparationError = error;
                Log.Engine.Error("C#のサービス登録に失敗しました。直前の状態を保持します。", error);
                SetFileStatus($"C#のサービス登録に失敗しました。直前の状態を保持します。 {error.GetBaseException().Message}", true);
                return;
            }
            try
            {
                migrated = SceneCodeMigrator.Migrate(_scene, ComponentAssets.Registry, registry, candidateServices.Factory);
            }
            catch (Exception error)
            {
                preparationError = error;
                Log.Engine.Error("C#を反映できません。直前の状態を保持します。", error);
                SetFileStatus($"C#を反映できません。直前の状態を保持します。 {error.GetBaseException().Message}", true);
                return;
            }
            var previous = _scene;
            var previousServices = _editSession;
            var selectedId = (SceneObjects.SelectedItem as SceneObject)?.Id;
            var wasDirty = _sceneDirty;
            ComponentAssets.SetUserCode(compiled);
            _scene = migrated;
            migrated = null;
            SceneObjects.SelectedItem = null;
            SceneObjects.ItemsSource = _scene.Objects;
            SceneObjects.SelectedItem = _scene.Objects.FirstOrDefault(item => item.Id == selectedId);
            RefreshObjectInspector();
            _sceneDirty = wasDirty;
            UpdateSceneTitle();
            _editSession = candidateServices;
            candidateServices = null;
            adopted = true;
            // 終了順：Component を先に、Scope・provider を後に解放する。一方が失敗しても他方を続ける。
            var oldErrors = new List<Exception>();
            try { ComponentAssets.DisposeComponents(previous.Objects.SelectMany(item => item.Components)); }
            catch (Exception error) { oldErrors.Add(error); }
            try { previousServices.Dispose(); }
            catch (Exception error) { oldErrors.Add(error); }
            if (oldErrors.Count != 0)
                throw new AggregateException("旧コードの解放に失敗しました。", oldErrors);
            Log.Engine.Info($"C#を反映しました：{ComponentAssets.UserTypes.Count}クラス。");
            SetFileStatus($"C#を反映しました：{ComponentAssets.UserTypes.Count}クラス。");
        }
        catch (Exception error)
        {
            preparationError = preparationError ?? error;
            var message = adopted ? "旧コードの解放に失敗しました。" : "C#を反映できません。直前の状態を保持します。";
            Log.Engine.Error(message, error);
            SetFileStatus($"{message} {error.GetBaseException().Message}", true);
            if (!adopted && preparationError is not null && !ReferenceEquals(preparationError, error))
            {
                // 準備エラーの後に後片付けでも失敗した場合、両方を残す呼び出し側のために集約する情報はログに残す。
                Log.Engine.Error("採用できなかったC#の後片付け中の追加エラー。", preparationError);
            }
        }
        finally
        {
            if (!adopted)
            {
                var cleanupErrors = new List<Exception>();
                try
                {
                    if (migrated is not null) ComponentAssets.DisposeComponents(migrated.Objects.SelectMany(item => item.Components));
                }
                catch (Exception error)
                {
                    cleanupErrors.Add(error);
                    Log.Engine.Error("採用できなかったC#の後片付けに失敗しました。", error);
                }
                try
                {
                    candidateServices?.Dispose();
                }
                catch (Exception error)
                {
                    cleanupErrors.Add(error);
                    Log.Engine.Error("採用できなかったサービスの後片付けに失敗しました。", error);
                }
                finally { try { compiled?.LoadContext?.Unload(); } catch { } }
                if (cleanupErrors.Count != 0 && preparationError is null)
                {
                    var message = "採用できなかったC#の後片付けに失敗しました。";
                    Log.Engine.Error(message, cleanupErrors[0]);
                    SetFileStatus($"{message} {cleanupErrors[0].GetBaseException().Message}", true);
                }
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
