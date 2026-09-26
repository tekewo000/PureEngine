using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace PureEngine.Editor;

public partial class MainWindow
{
    private async void OnBuild(object? sender, RoutedEventArgs e) =>
        await RunFileOperation(() => BuildGameAsync(run: false));

    private async void OnBuildAndRun(object? sender, RoutedEventArgs e) =>
        await RunFileOperation(() => BuildGameAsync(run: true));

    private async Task BuildGameAsync(bool run)
    {
        if (Project is null) return;
        if (HasInputErrors)
        {
            SetFileStatus("Cannot build. Fix all input errors first.", true);
            return;
        }
        if (run && !OperatingSystem.IsWindows())
        {
            SetFileStatus("Build & Run requires Windows. Build can produce a Windows folder on this host.", true);
            return;
        }
        if (Documents.IsDirty)
        {
            if (Documents.BuildSaveConflict() is { } conflict)
            {
                SetFileStatus(conflict, true);
                return;
            }
            if (!await ConfirmBuildSave()) return;
            if (Documents.Asset is { Dirty: true } && !ViewModel.SaveDataAsset()) return;
            if (Documents.Table.IsDirty && !ViewModel.SaveTable()) return;
            if (Documents.Localization.IsDirty && !ViewModel.SaveLocalization()) return;
            if (Documents.Prefab is { IsDirty: true } && !ViewModel.SavePrefab()) return;
            if (Documents.Scene.IsDirty && !await SaveMainSceneAsync(false)) return;
            if (Documents.IsDirty) throw new InvalidOperationException("Save every dirty document before building.");
        }
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose build parent folder (outside the project and engine checkout)",
            AllowMultiple = false,
        });
        if (folders.Count == 0) return;
        var parent = folders[0].TryGetLocalPath() ?? throw new IOException("Choose a local build folder.");
        var name = Project.Document.Name ?? throw new InvalidDataException("The project requires a name.");
        WindowsGameBuild.ValidateWindowsName(name);
        var destination = Path.Combine(parent, name + "-Windows-x64");
        var engineRoot = WindowsGameBuild.FindEngineRoot();
        SetFileStatus("Building Windows x64 self-contained folder. The previous successful build is preserved until validation succeeds.");
        // Freeze editing while the saved disk snapshot is compiled and packaged. FileBusy also defers code adoption and Play.
        var content = (Control)Content!;
        content.IsEnabled = false;
        try
        {
            var executable = await Task.Run(() => WindowsGameBuild.BuildAsync(Project, destination, engineRoot));
            SetFileStatus($"Build succeeded: {destination}");
            if (run)
            {
                using var process = Process.Start(new ProcessStartInfo(executable)
                {
                    WorkingDirectory = destination,
                    UseShellExecute = true,
                }) ?? throw new IOException("The build succeeded, but the Player could not be launched.");
                SetFileStatus($"Build succeeded; Player launched: {destination}");
            }
        }
        finally { content.IsEnabled = true; }
    }

    private async Task<bool> ConfirmBuildSave()
    {
        var dialog = new Window
        {
            Title = "Save Before Build", Width = 440, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var save = new Avalonia.Controls.Button { Content = "Save All & Build", IsDefault = true };
        var cancel = new Avalonia.Controls.Button { Content = "Cancel", IsCancel = true };
        save.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 20,
            Children =
            {
                new TextBlock { Text = "Build uses saved files. Save all open scenes, prefabs, data assets and localization changes?", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Children = { save, cancel },
                },
            },
        };
        return await dialog.ShowDialog<bool>(this);
    }
}
