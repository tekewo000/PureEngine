using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PureEngine.Core;

namespace PureEngine.Editor;

public partial class MainWindow
{
    private ProjectFile? Project => ViewModel.ProjectFile;

    private Task<IStorageFolder?> ScenePickerDirectory() => Project is null
        ? Task.FromResult<IStorageFolder?>(null)
        : StorageProvider.TryGetFolderFromPathAsync(Project.ScenesDirectory);

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
        if (HasInputErrors)
        {
            SetFileStatus("Fix the Inspector input errors before creating a scene.", true);
            return;
        }
        ActivateEditorViewport(0);
        if (!await ConfirmUnsavedChanges()) return;
        SetCurrentScene(new Scene(), null);
        MarkSceneChanged();
        SetFileStatus("Created a new scene. Save with Ctrl+S.");
    }

    private async void OnSetStartupScene(object? sender, RoutedEventArgs e) => await RunFileOperation(async () =>
    {
        if (Project is null) return;
        // Registers the Explorer-selected scene file when invoked from it, or the scene being edited from the File menu.
        if (ReferenceEquals(sender, FilesStartupMenu)
            && ProjectFiles.SelectedItem is ProjectExplorerEntry entry
            && entry.Kind == ProjectExplorerKind.Scene && entry.FullPath is not null)
        {
            Project.SetStartupScene(entry.FullPath);
        }
        else
        {
            if (Documents.Scene.Path is null)
            {
                if (HasInputErrors || !await ConfirmCloseDataAsset()) return;
                ActivateEditorViewport(0);
                if (!await SaveSceneAsync(false)) return;
            }
            Project.SetStartupScene(Documents.Scene.Path!);
        }
        RefreshProjectExplorer();
        SetFileStatus($"Set as startup scene: {Project.Document.StartupScene}");
    });

}
