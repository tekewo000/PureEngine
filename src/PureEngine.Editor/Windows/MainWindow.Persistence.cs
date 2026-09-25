using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PureEngine.Core;
using Color = Avalonia.Media.Color;

namespace PureEngine.Editor;

public partial class MainWindow
{
    private readonly SceneSerializer _sceneSerializer;
    private static readonly FilePickerFileType SceneFileType = new("PureEngine scene")
        { Patterns = ["*.pure.scene.yaml", "*.yaml", "*.yml"] };
    private bool _fileBusy;
    private bool _allowClose;

    internal bool IsFileBusy => _fileBusy;

    private void MarkSceneChanged()
    {
        _editScene.MarkChanged();
        UpdateSceneTitle();
        UpdatePrefabEditorChrome();
    }

    private void UpdateSceneTitle() => Title = $"{(_editScene.IsDirty ? "* " : "")}{Path.GetFileName(_editScene.Path) ?? "Untitled"} — {(_project is null ? "" : _project.Document.Name + " — ")}PureEngine Editor";

    private void SetFileStatus(string message, bool error = false)
    {
        FileStatus.Text = message;
        FileStatus.Foreground = new SolidColorBrush(Color.Parse(error ? "#FF9E99" : "#B8BDC5"));
        ToolTip.SetTip(FileStatus, message);
    }

    private async void OnOpenScene(object? sender, RoutedEventArgs e) => await RunFileOperation(OpenSceneAsync);
    private async void OnSaveScene(object? sender, RoutedEventArgs e) => await RunFileOperation(async () => await SaveSceneAsync(false));
    private async void OnSaveSceneAs(object? sender, RoutedEventArgs e) => await RunFileOperation(async () => await SaveSceneAsync(true));

    private async Task RunFileOperation(Func<Task> operation)
    {
        var blockReason = EditorOperationGate.FileOperationBlockReason(IsPlaying, _fileBusy);
        if (blockReason is not null)
        {
            if (IsPlaying) SetFileStatus(blockReason, true);
            return;
        }
        _fileBusy = true;
        EditorSurface.IsEnabled = false;
        try { await operation(); }
        catch (Exception error) { SetFileStatus($"Operation failed: {error.GetBaseException().Message}", true); }
        finally { EditorSurface.IsEnabled = true; _fileBusy = false; FlushPendingUserCodeReload(); }
    }

    private async Task<bool> SaveSceneAsync(bool saveAs)
    {
        if (!IsPrefabEditing) return await SaveMainSceneAsync(saveAs);
        if (!saveAs) return SavePrefabEditor();
        SetFileStatus("Prefab Save As is not supported. Use Save as Prefab on a Stuffs object to create a new asset.", true);
        return false;
    }

    private async Task<bool> SaveMainSceneAsync(bool saveAs)
    {
        CancelSceneViewDrag();
        var saveBlock = EditorOperationGate.SaveBlockReason(!IsPrefabEditing && _assetEdit is null && HasInputErrors);
        if (saveBlock is not null)
        {
            SetFileStatus($"Cannot save. {saveBlock}", true);
            return false;
        }
        // Validation happens before picking or touching a destination file.
        var yaml = _sceneSerializer.Serialize(_sceneDocument.Current);
        var path = _sceneDocument.Path;
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
            path = file.TryGetLocalPath() ?? throw new IOException("Select a local save destination.");
        }
        _project?.ValidateScenePath(path);
        SceneFile.Write(path, yaml);
        _sceneDocument.MarkSaved(path);
        _explorerSelectedFile = path;
        UpdateSceneTitle();
        RefreshProjectExplorer();
        SetFileStatus($"Saved: {path}");
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
        var path = files[0].TryGetLocalPath() ?? throw new IOException("Select a local scene.");
        await OpenScenePathAsync(path);
    }

    private async Task OpenScenePathAsync(string path)
    {
        CancelSceneViewDrag();
        if (IsPlaying)
        {
            SetFileStatus("Cannot switch scenes while playing. Stop first.", true);
            return;
        }
        if (!await ConfirmCloseDataAsset()) return;
        if (HasInputErrors)
        {
            SetFileStatus("Fix the Inspector input errors before opening a scene.", true);
            return;
        }
        ActivateEditorViewport(0);
        _project?.ValidateScenePath(path);
        // Completely restore into a separate scene before replacing any editor data.
        // Use the editing factory for constructor injection with a service set separate from Play.
        var serializer = new SceneSerializer(_components.Registry, BuildProjectAssetStore(_components.Registry), BuildPrefabCatalog());
        var restored = serializer.Deserialize(File.ReadAllText(path), EditSession.Factory);
        // This first scene only validates the file; it is never adopted by the editor.
        ComponentAssets.DisposeComponents(restored.OwnedComponents);
        if (!await ConfirmUnsavedChanges()) return;
        // Saving the old scene during confirmation may overwrite the file just selected.
        restored = serializer.Deserialize(File.ReadAllText(path), out var membersChanged, EditSession.Factory);
        SetCurrentScene(restored, path);
        if (membersChanged) MarkSceneChanged();
        SetFileStatus($"Loaded: {path}");
    }

    private void SetCurrentScene(Scene restored, string? path)
    {
        CancelSceneViewDrag();
        if (IsPlaying)
        {
            SetFileStatus("Cannot switch scenes while playing. Stop first.", true);
            return;
        }
        var previous = _editScene.Replace(restored, path, dirty: false);
        RefreshHierarchy();
        SelectSceneObject(_editScene.Current.RootObjects.Count > 0 ? _editScene.Current.RootObjects[0] : null, focus: false);
        // Selection may already have been empty; explicitly reset the Inspector as well.
        RefreshObjectInspector();
        UpdateSceneTitle();
        SyncExplorerToScene(path);
        RefreshProjectExplorer();
        if (!ReferenceEquals(previous, restored))
            ComponentAssets.DisposeComponents(previous.OwnedComponents);
    }

    private void CloseEditSession()
    {
        ClearHierarchyDropIndicator();
        CancelSceneViewDrag();
        // When switching or closing projects, stop watching that project.
        // Shutdown order: dispose edit-scene components, then edit services, then request code release. Leaves other projects untouched.
        StopUserCodeWatching();
        _playTimer?.Stop();
        _assetEdit = null;
        _assetOwned.Clear();
        DataAssetEditors.Children.Clear();
        var errors = new List<Exception>();
        try { ForceStopPlayForShutdown(); }
        catch (Exception error) { errors.Add(error); }
        try { ClosePrefabEditor(); }
        catch (Exception error) { errors.Add(error); }
        var previous = _editScene.Reset();
        try { ComponentAssets.DisposeComponents(previous.OwnedComponents); }
        catch (Exception error) { errors.Add(error); }
        try { EditSession.Dispose(); }
        catch (Exception error) { errors.Add(error); }
        try { _components.Dispose(); }
        catch (Exception error) { errors.Add(error); }
        _dragTypes = null;
        _assetPress = null;
        _pressedPrefab = null;
        if (errors.Count != 0) throw new AggregateException("Editor cleanup failed.", errors);
    }

    /// <summary>Reselects the opened scene folder in the Explorer and selects that file in the right pane.</summary>
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
            // Leaves the Explorer selection unchanged for scenes outside the project.
        }
    }

    private async Task<bool> ConfirmUnsavedChanges()
    {
        if (!EditorOperationGate.NeedsUnsavedConfirmation(
            _sceneDocument.IsDirty, !IsPrefabEditing && _assetEdit is null && HasInputErrors)) return true;
        var dialog = new Window
        {
            Title = "Unsaved Scene", Width = 420, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (label, result) in new[] { ("Save", "save"), ("Discard", "discard"), ("Cancel", "cancel") })
        {
            var button = new Avalonia.Controls.Button { Content = label, IsDefault = result == "save", IsCancel = result == "cancel" };
            button.Click += (_, _) => dialog.Close(result);
            buttons.Children.Add(button);
        }
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 20,
            Children = { new TextBlock { Text = "The scene has unsaved changes. Save?", TextWrapping = TextWrapping.Wrap }, buttons },
        };
        var answer = await dialog.ShowDialog<string?>(this);
        return answer == "discard" || (answer == "save" && await SaveMainSceneAsync(false));
    }

    private async void OnEditorClosing(object? sender, WindowClosingEventArgs e)
    {
        CancelSceneViewDrag();
        if (_allowClose) return;
        if (_fileBusy) { e.Cancel = true; return; }
        if (_play is not null)
        {
            // While running, always stop and release before the unsaved-changes check. Keeps the edit scene as it was before the run.
            if (!StopPlay())
            {
                // Keeps the window open so the error stays readable. The next Close goes through the usual unsaved-changes check.
                e.Cancel = true;
                return;
            }
        }
        if (!EditorOperationGate.NeedsUnsavedConfirmation(
            _sceneDocument.IsDirty || _prefabScene is { IsDirty: true } || _assetEdit is { Dirty: true }, HasInputErrors)) return;
        e.Cancel = true;
        await RunFileOperation(async () =>
        {
            // Do not discard any document until every confirmation accepts closing the window.
            if (!await ConfirmDataAssetClose(closeOnConfirm: false)) return;
            if (!await ConfirmPrefabEditorClose(closeOnConfirm: false)) return;
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
            if (_assetEdit is not null && GetSelectedSceneObject() is null)
                await RunFileOperation(SaveDataAssetAsync);
            else
                await RunFileOperation(async () => await SaveSceneAsync(e.KeyModifiers.HasFlag(KeyModifiers.Shift)));
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
