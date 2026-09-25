using Avalonia.Threading;

namespace PureEngine.Editor;

/// <summary>Debounced file and directory changes, delivered on the UI thread.</summary>
public sealed class UserCodeWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly DispatcherTimer _debounce;
    private bool _disposed;
    public event Action? ReloadRequested;
    public static TimeSpan DebounceDelay { get; } = TimeSpan.FromMilliseconds(250);

    public UserCodeWatcher(string rootDirectory)
    {
        _debounce = new DispatcherTimer { Interval = DebounceDelay };
        _debounce.Tick += OnTick;
        // Watch directories too: moving a folder may produce no child-file events.
        _watcher = new FileSystemWatcher(Path.GetFullPath(rootDirectory))
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
        };
        _watcher.Changed += OnTouched;
        _watcher.Created += OnTouched;
        _watcher.Deleted += OnTouched;
        _watcher.Renamed += OnTouched;
        _watcher.Error += OnError;
        _watcher.EnableRaisingEvents = true;
    }

    private bool Ignored(string path) => Path.GetRelativePath(_watcher.Path, path)
        .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(part => part.StartsWith('.') || part.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || part.Equals("obj", StringComparison.OrdinalIgnoreCase));

    private void OnTouched(object? sender, FileSystemEventArgs e)
    {
        if (Ignored(e.FullPath) && (e is not RenamedEventArgs rename || Ignored(rename.OldFullPath))) return;
        // Structural changes include directory moves/deletes and atomic-save renames.
        if (e.ChangeType == WatcherChangeTypes.Changed
            && !e.FullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return;
        Dispatcher.UIThread.Post(RequestReload);
    }

    private void OnError(object sender, ErrorEventArgs e) => Dispatcher.UIThread.Post(RequestReload);

    private void RequestReload()
    {
        if (_disposed) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _debounce.Stop();
        if (!_disposed) ReloadRequested?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounce.Stop();
        _debounce.Tick -= OnTick;
        _watcher.Dispose();
        ReloadRequested = null;
    }
}
