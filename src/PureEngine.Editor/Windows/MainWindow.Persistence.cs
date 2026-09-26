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
    private SceneSerializer SceneSerializer => ViewModel.SceneSerializer;
    private static readonly FilePickerFileType SceneFileType = new("PureEngine scene")
        { Patterns = ["*.pure.scene.yaml", "*.yaml", "*.yml"] };
    private bool _allowClose;
    private bool _sessionClosed;

    internal bool IsFileBusy => ViewModel.FileBusy;

    private void MarkSceneChanged()
    {
        Documents.Current.MarkChanged();
        UpdateSceneTitle();
        UpdatePrefabEditorChrome();
    }

    private void UpdateSceneTitle() => ViewModel.RefreshDocumentState();

    private void SetFileStatus(string message, bool error = false) => ViewModel.SetStatus(message, error);

    private async void OnOpenScene(object? sender, RoutedEventArgs e) => await RunFileOperation(OpenSceneAsync);
    private async void OnSaveScene(object? sender, RoutedEventArgs e) => await RunFileOperation(async () => await SaveSceneAsync(false));
    private async void OnSaveSceneAs(object? sender, RoutedEventArgs e) => await RunFileOperation(async () => await SaveSceneAsync(true));

    private Task RunFileOperation(Func<Task> operation) => ViewModel.RunFileOperation(operation);

    private void OnDocumentSaved(EditedDocumentKind kind)
    {
        if (kind == EditedDocumentKind.DataAsset) RefreshDataAssetInspector();
        else if (kind == EditedDocumentKind.Localization)
        {
            SyncSceneLocalization();
            RebuildLocalizationRows();
            RefreshObjectInspector();
            RefreshProjectExplorer();
        }
        else
        {
            if (kind == EditedDocumentKind.Table) UpdateDataAssetTableChrome();
            RefreshProjectExplorer();
        }
        RefreshPreviewLanguages();
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
        // Validate before opening a destination picker or touching a file.
        var yaml = ViewModel.PrepareSceneSave();
        if (yaml is null) return false;
        var path = Documents.Scene.Path;
        if (saveAs || path is null)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Scene",
                SuggestedFileName = Path.GetFileName(path) ?? Project?.NextSceneName() ?? "Main.pure.scene.yaml",
                DefaultExtension = "pure.scene.yaml",
                FileTypeChoices = [SceneFileType],
                ShowOverwritePrompt = true,
                SuggestedStartLocation = await ExplorerSaveDirectory(),
            });
            if (file is null) return false;
            path = file.TryGetLocalPath() ?? throw new IOException("Select a local save destination.");
        }
        ViewModel.SaveScene(path, yaml);
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
        var restored = Documents.ReadScene(path, Project, Components.Registry, out _);
        // This first scene only validates the file; it is never adopted by the editor.
        ComponentAssets.DisposeComponents(restored.OwnedComponents);
        if (!await ConfirmUnsavedChanges()) return;
        // Saving the old scene during confirmation may overwrite the file just selected.
        restored = Documents.ReadScene(path, Project, Components.Registry, out var membersChanged);
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
        try { Documents.ReplaceScene(restored, path); }
        finally
        {
            RefreshHierarchy();
            SelectSceneObject(Documents.Current.Current.RootObjects.Count > 0 ? Documents.Current.Current.RootObjects[0] : null, focus: false);
            RefreshObjectInspector();
            UpdateSceneTitle();
            SyncExplorerToScene(path);
            RefreshProjectExplorer();
        }
    }

    private void CloseEditSession()
    {
        if (_sessionClosed) return;
        _sessionClosed = true;
        ClearHierarchyDropIndicator();
        CancelSceneViewDrag();
        StopUserCodeWatching();
        StopPlayUpdates();
        DisconnectViewModel();
        _hierarchyRefreshing = true;
        _tableTypeChanging = true;
        DataContext = null;
        SceneSurface.ContextMenu!.DataContext = null;
        _inputIds.Clear();
        ComponentEditors.Children.Clear();
        DataAssetEditors.Children.Clear();
        DataAssetTableRows.Children.Clear();
        _tableViews.Clear();
        _dragTypes = null;
        _assetPress = null;
        _pressedPrefab = null;
        ViewModel.Dispose();
    }

    private void DisconnectViewModel()
    {
        _playTimer?.Tick -= OnPlayTick;
        if (_consoleTimer is not null)
        {
            _consoleTimer.Tick -= OnConsoleTick;
            _consoleTimer.Stop();
            _consoleTimer = null;
        }
        ViewModel.Hierarchy.BeforeMutation -= CancelSceneViewDrag;
        ViewModel.Hierarchy.SceneChanged -= OnHierarchySceneChanged;
        ViewModel.BeforeSceneSave -= CancelSceneViewDrag;
        ViewModel.DocumentSaved -= OnDocumentSaved;
        ViewModel.Console.PropertyChanged -= OnConsoleModelChanged;
        ViewModel.Inspector.DocumentEdited -= RefreshEditedDocument;
        ViewModel.Inspector.PropertyChanged -= OnInspectorModelChanged;
        ViewModel.Play.BeforeStart -= CancelSceneViewDrag;
        ViewModel.Play.Started -= OnPlayStarted;
        ViewModel.Play.Stopping -= StopPlayUpdates;
        ViewModel.Play.InputReset -= ResetGameInput;
        ViewModel.Play.ReturnToScene -= OnPlayReturnedToScene;
        ViewModel.Play.FrameCompleted -= ValidateGameInput;
        ViewModel.Play.PropertyChanged -= OnPlayModelChanged;
        ViewModel.Compilation.BeforeApply -= CancelSceneViewDrag;
        ViewModel.Compilation.Adopted -= OnUserCodeAdopted;
        ViewModel.Compilation.AttemptCompleted -= OnUserCodeAttemptCompleted;
    }

    /// <summary>Reselects the opened scene folder in the Explorer and selects that file in the right pane.</summary>
    private void SyncExplorerToScene(string? path)
    {
        ViewModel.Project.SelectedFile = path;
        if (Project is null || path is null)
        {
            ViewModel.Project.ComponentsSelected = Project is null;
            return;
        }
        try
        {
            var relative = Project.GetSceneRelativePath(path);
            ViewModel.Project.Folder = relative.Contains('/') ? relative[..relative.LastIndexOf('/')] : "";
            ViewModel.Project.ComponentsSelected = false;
        }
        catch (InvalidDataException)
        {
            // Leaves the Explorer selection unchanged for scenes outside the project.
        }
    }

    private async Task<bool> ConfirmUnsavedChanges()
    {
        if (!EditorOperationGate.NeedsUnsavedConfirmation(
            Documents.Scene.IsDirty, !IsPrefabEditing && Documents.Asset is null && HasInputErrors)) return true;
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
        if (ViewModel.FileBusy) { e.Cancel = true; return; }
        if (IsPlaying)
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
            Documents.IsDirty, HasInputErrors)) return;
        e.Cancel = true;
        await RunFileOperation(async () =>
        {
            // Do not discard any document until every confirmation accepts closing the window.
            if (!await ConfirmTableRowsClose(closeOnConfirm: false)) return;
            if (!await ConfirmDataAssetClose(closeOnConfirm: false)) return;
            if (!await ConfirmLocalizationClose()) return;
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
            if (ViewportTabs.SelectedIndex == DataAssetTableViewportIndex && Documents.Table.Type is not null)
                await RunFileOperation(SaveDataAssetTableAsync);
            else if (ViewportTabs.SelectedIndex == LocalizationViewportIndex)
                await RunFileOperation(SaveLocalizationTableAsync);
            else if (Documents.Asset is not null && GetSelectedSceneObject() is null)
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
