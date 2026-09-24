using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PureEngine.Core;

namespace PureEngine.Editor;

public partial class MainWindow
{
    private readonly ProjectFile? _project;

    private Task<IStorageFolder?> ScenePickerDirectory() => _project is null
        ? Task.FromResult<IStorageFolder?>(null)
        : StorageProvider.TryGetFolderFromPathAsync(_project.ScenesDirectory);

    private async void OnNewScene(object? sender, RoutedEventArgs e) => await RunFileOperation(NewSceneAsync);
    private void OnCloseProject(object? sender, RoutedEventArgs e) => Close();

    private async Task NewSceneAsync()
    {
        CancelSceneViewDrag();
        if (IsPlaying)
        {
            SetFileStatus("Cannot switch scenes while playing. Stop first.", true);
            return;
        }
        if (!await ConfirmCloseDataAsset()) return;
        if (!await ConfirmUnsavedChanges()) return;
        SetCurrentScene(new Scene(), null);
        MarkSceneChanged();
        SetFileStatus("Created a new scene. Save with Ctrl+S.");
    }

    private async void OnSetStartupScene(object? sender, RoutedEventArgs e) => await RunFileOperation(async () =>
    {
        if (_project is null) return;
        // Registers the Explorer-selected scene file when invoked from it, or the scene being edited from the File menu.
        if (ReferenceEquals(sender, FilesStartupMenu)
            && ProjectFiles.SelectedItem is ProjectExplorerEntry entry
            && entry.Kind == ProjectExplorerKind.Scene && entry.FullPath is not null)
        {
            _project.SetStartupScene(entry.FullPath);
        }
        else
        {
            if (_editScene.Path is null && !await SaveSceneAsync(false)) return;
            _project.SetStartupScene(_editScene.Path!);
        }
        RefreshProjectExplorer();
        SetFileStatus($"Set as startup scene: {_project.Document.StartupScene}");
    });

}
