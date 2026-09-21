using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace PureEngine.Editor;

public partial class LauncherWindow : Window
{
    private readonly RecentProjects _history;
    private static readonly FilePickerFileType ProjectType = new("PureEngine project")
        { Patterns = ["*.pure.project.yaml"] };
    private bool _busy;
    private bool _closed;

    public LauncherWindow() : this(new RecentProjects(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PureEngine", "recent-projects.json"))) { }

    public LauncherWindow(RecentProjects history)
    {
        _history = history;
        InitializeComponent();
        _history.Load();
        RefreshHistory();
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        ProjectLocation.Text = Directory.Exists(documents) ? documents : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        ProjectName.TextChanged += (_, _) => UpdateDestination();
        ProjectLocation.TextChanged += (_, _) => UpdateDestination();
        UpdateDestination();
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
        Closed += (_, _) => _closed = true;
    }

    private void UpdateDestination()
    {
        var location = ProjectLocation.Text?.Trim() ?? "";
        var name = ProjectName.Text?.Trim() ?? "";
        ProjectDestination.Text = string.IsNullOrEmpty(location) || string.IsNullOrEmpty(name)
            ? "Project名と作成先を指定してください。" : $"作成先: {Path.Combine(location, name)}";
    }

    private void RefreshHistory()
    {
        RecentList.ItemsSource = _history.Entries;
        EmptyHistory.IsVisible = _history.Entries.Count == 0;
        SetError(_history.Warning);
    }

    private void SetError(string? message)
    {
        LauncherError.Text = message;
        LauncherError.IsVisible = !string.IsNullOrEmpty(message);
    }

    private async Task RunOperation(Func<Task> operation)
    {
        if (_busy) return;
        _busy = true;
        LauncherSurface.IsEnabled = false;
        SetError(null);
        try { await operation(); }
        catch (Exception error) { SetError($"Projectを開けませんでした: {error.GetBaseException().Message}"); }
        finally { _busy = false; LauncherSurface.IsEnabled = true; }
    }

    private async void OnBrowseLocation(object? sender, RoutedEventArgs e) => await RunOperation(async () =>
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Projectを作成する親フォルダ", AllowMultiple = false,
            SuggestedStartLocation = Directory.Exists(ProjectLocation.Text)
                ? await StorageProvider.TryGetFolderFromPathAsync(ProjectLocation.Text!) : null,
        });
        if (folders.Count == 0) return;
        ProjectLocation.Text = folders[0].TryGetLocalPath() ?? throw new IOException("ローカルのフォルダを選択してください。");
    });

    private async void OnCreateProject(object? sender, RoutedEventArgs e) => await RunOperation(() =>
    {
        var created = ProjectSession.Create(ProjectLocation.Text?.Trim() ?? "", ProjectName.Text?.Trim() ?? "");
        OpenEditor(created, GameSession.Create());
        return Task.CompletedTask;
    });

    private async void OnOpenProject(object? sender, RoutedEventArgs e) => await RunOperation(async () =>
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            { Title = "Open Project", AllowMultiple = false, FileTypeFilter = [ProjectType] });
        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath() ?? throw new IOException("ローカルのProjectを選択してください。");
        // 候補コードの登録・サービス生成・Scene移行が成功してから採用する。
        // 失敗時は Open 側で候補資源を解放し、既存の登録を維持する。ここで作る資源はない。
        var (session, editServices) = ProjectSession.Open(path);
        try
        {
            OpenEditor(session, editServices);
        }
        catch
        {
            try { editServices.Dispose(); } catch { }
            throw;
        }
    });

    private async void OnRecentDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext is RecentProject project)
            await OpenRecent(project);
    }

    private async void OnRecentKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || RecentList.SelectedItem is not RecentProject project) return;
        e.Handled = true;
        await OpenRecent(project);
    }

    private Task OpenRecent(RecentProject project) => RunOperation(() =>
    {
        var (session, editServices) = ProjectSession.Open(project.ManifestPath);
        try
        {
            OpenEditor(session, editServices);
        }
        catch
        {
            try { editServices.Dispose(); } catch { }
            throw;
        }
        return Task.CompletedTask;
    });

    private void OpenEditor(ProjectSession session, GameSession editServices)
    {
        var editor = new MainWindow(session, editServices);
        editor.Closed += (_, _) =>
        {
            if (_closed) return;
            RefreshHistory();
            Show();
            Activate();
        };
        editor.Show();
        _history.Remember(session.Project);
        Hide();
    }
}
