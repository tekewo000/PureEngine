using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PureEngine.Core;

namespace PureEngine.Editor;

public partial class MainWindow
{
    private readonly SceneSerializer _sceneSerializer = new(ComponentAssets.Registry);
    private static readonly FilePickerFileType SceneFileType = new("PureEngine scene")
        { Patterns = ["*.pure.scene.yaml", "*.yaml", "*.yml"] };
    private string? _scenePath;
    private bool _sceneDirty;
    private bool _fileBusy;
    private bool _allowClose;

    private void MarkSceneChanged()
    {
        _sceneDirty = true;
        UpdateSceneTitle();
    }

    private void UpdateSceneTitle() => Title = $"{(_sceneDirty ? "* " : "")}{Path.GetFileName(_scenePath) ?? "Untitled"} — {(_project is null ? "" : _project.Document.Name + " — ")}PureEngine Editor";

    private void SetFileStatus(string message, bool error = false)
    {
        FileStatus.Text = message;
        FileStatus.Foreground = new SolidColorBrush(Color.Parse(error ? "#FF5252" : "#BBBBBB"));
        ToolTip.SetTip(FileStatus, message);
    }

    private async void OnOpenScene(object? sender, RoutedEventArgs e) => await RunFileOperation(OpenSceneAsync);
    private async void OnSaveScene(object? sender, RoutedEventArgs e) => await RunFileOperation(async () => { await SaveSceneAsync(false); });
    private async void OnSaveSceneAs(object? sender, RoutedEventArgs e) => await RunFileOperation(async () => { await SaveSceneAsync(true); });

    private async Task RunFileOperation(Func<Task> operation)
    {
        if (_fileBusy) return;
        _fileBusy = true;
        EditorSurface.IsEnabled = false;
        try { await operation(); }
        catch (Exception error) { SetFileStatus($"操作に失敗しました: {error.GetBaseException().Message}", true); }
        finally { EditorSurface.IsEnabled = true; _fileBusy = false; }
    }

    private async Task<bool> SaveSceneAsync(bool saveAs)
    {
        if (_invalidFields.Count > 0 || NameError.IsVisible)
        {
            SetFileStatus("保存できません。Inspectorの入力エラーを修正してください。", true);
            return false;
        }
        // Validation happens before picking or touching a destination file.
        var yaml = _sceneSerializer.Serialize(_scene);
        var path = _scenePath;
        if (saveAs || path is null)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Scene",
                SuggestedFileName = Path.GetFileName(path) ?? _project?.NextSceneName() ?? "Main.pure.scene.yaml",
                DefaultExtension = "pure.scene.yaml",
                FileTypeChoices = [SceneFileType],
                ShowOverwritePrompt = true,
                SuggestedStartLocation = await ExplorerSaveDirectory(),
            });
            if (file is null) return false;
            path = file.TryGetLocalPath() ?? throw new IOException("ローカルの保存先を選択してください。");
        }
        _project?.ValidateScenePath(path);
        SceneFile.Write(path, yaml);
        _scenePath = path;
        _explorerSelectedFile = path;
        _sceneDirty = false;
        UpdateSceneTitle();
        RefreshProjectExplorer();
        SetFileStatus($"保存しました: {path}");
        return true;
    }

    private async Task OpenSceneAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Scene", AllowMultiple = false, FileTypeFilter = [SceneFileType],
            SuggestedStartLocation = await ScenePickerDirectory(),
        });
        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath() ?? throw new IOException("ローカルのシーンを選択してください。");
        await OpenScenePathAsync(path);
    }

    private async Task OpenScenePathAsync(string path)
    {
        _project?.ValidateScenePath(path);
        // Completely restore into a separate scene before replacing any editor data.
        var restored = _sceneSerializer.Deserialize(File.ReadAllText(path));
        if (!await ConfirmUnsavedChanges()) return;
        // Saving the old scene during confirmation may overwrite the file just selected.
        restored = _sceneSerializer.Deserialize(File.ReadAllText(path));
        SetCurrentScene(restored, path);
        SetFileStatus($"読み込みました: {path}");
    }

    private void SetCurrentScene(Scene restored, string? path)
    {
        _scene = restored;
        SceneObjects.SelectedItem = null;
        SceneObjects.ItemsSource = _scene.Objects;
        SceneObjects.SelectedIndex = _scene.Objects.Count > 0 ? 0 : -1;
        _scenePath = path;
        _sceneDirty = false;
        // Selection may already have been empty; explicitly reset the Inspector as well.
        RefreshObjectInspector();
        UpdateSceneTitle();
        SyncExplorerToScene(path);
        RefreshProjectExplorer();
    }

    /// <summary>開いたシーンのフォルダをExplorerで選び直し、右ペインでそのファイルを選択する。</summary>
    private void SyncExplorerToScene(string? path)
    {
        _explorerSelectedFile = path;
        if (_project is null || path is null)
        {
            _explorerComponentsSelected = _project is null;
            return;
        }
        try
        {
            var relative = _project.GetSceneRelativePath(path);
            _explorerFolder = relative.Contains('/') ? relative[..relative.LastIndexOf('/')] : "";
            _explorerComponentsSelected = false;
        }
        catch (InvalidDataException)
        {
            // Project外シーンはExplorer選択を変えない。
        }
    }

    private async Task<bool> ConfirmUnsavedChanges()
    {
        if (!_sceneDirty && _invalidFields.Count == 0 && !NameError.IsVisible) return true;
        var dialog = new Window
        {
            Title = "Unsaved Scene", Width = 420, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (label, result) in new[] { ("Save", "save"), ("Discard", "discard"), ("Cancel", "cancel") })
        {
            var button = new Button { Content = label, IsDefault = result == "save", IsCancel = result == "cancel" };
            button.Click += (_, _) => dialog.Close(result);
            buttons.Children.Add(button);
        }
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 20,
            Children = { new TextBlock { Text = "シーンに未保存の変更があります。保存しますか？", TextWrapping = TextWrapping.Wrap }, buttons },
        };
        var answer = await dialog.ShowDialog<string?>(this);
        return answer == "discard" || (answer == "save" && await SaveSceneAsync(false));
    }

    private async void OnEditorClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose) return;
        if (_fileBusy) { e.Cancel = true; return; }
        if (!_sceneDirty && _invalidFields.Count == 0 && !NameError.IsVisible) return;
        e.Cancel = true;
        await RunFileOperation(async () =>
        {
            if (!await ConfirmUnsavedChanges()) return;
            _allowClose = true;
            Close();
        });
    }

    private async void OnFileShortcut(object? sender, KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (e.Key == Key.S)
        {
            e.Handled = true;
            await RunFileOperation(async () => { await SaveSceneAsync(e.KeyModifiers.HasFlag(KeyModifiers.Shift)); });
        }
        else if (e.Key == Key.O)
        {
            e.Handled = true;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) Close();
            else await RunFileOperation(OpenSceneAsync);
        }
        else if (e.Key == Key.N)
        {
            e.Handled = true;
            await RunFileOperation(NewSceneAsync);
        }
    }
}
