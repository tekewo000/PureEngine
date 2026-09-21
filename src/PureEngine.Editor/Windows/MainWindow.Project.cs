using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PureEngine.Core;

namespace PureEngine.Editor;

public partial class MainWindow
{
    private ProjectFile? _project;

    private Task<IStorageFolder?> ScenePickerDirectory() => _project is null
        ? Task.FromResult<IStorageFolder?>(null)
        : StorageProvider.TryGetFolderFromPathAsync(_project.ScenesDirectory);

    private async void OnNewScene(object? sender, RoutedEventArgs e) => await RunFileOperation(NewSceneAsync);
    private void OnCloseProject(object? sender, RoutedEventArgs e) => Close();

    private async Task NewSceneAsync()
    {
        if (IsPlaying)
        {
            SetFileStatus("Play中はシーンを切り替えできません。先にStopしてください。", true);
            return;
        }
        if (!await ConfirmUnsavedChanges()) return;
        SetCurrentScene(new Scene(), null);
        MarkSceneChanged();
        SetFileStatus("新しいシーンを作成しました。Ctrl+Sで保存できます。");
    }

    private async void OnSetStartupScene(object? sender, RoutedEventArgs e) => await RunFileOperation(async () =>
    {
        if (_project is null) return;
        // Explorerでシーンファイルを選んで呼んだ場合はそのファイルを、Fileメニューからは編集中シーンを登録する。
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
        SetFileStatus($"起動シーンに設定しました: {_project.Document.StartupScene}");
    });

}
