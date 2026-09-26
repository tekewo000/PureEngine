using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PureEngine.Core;
using PureEngine.Runtime;

namespace PureEngine.Editor;

public partial class MainWindow
{
    public const int ConsoleMaxHistory = ConsoleViewModel.MaxHistory;
    private DispatcherTimer? _consoleTimer;
    private int _playLoggedErrorCount;
    private bool _consoleInitialized;
    private bool _consoleRefreshing;
    private bool _consoleFollowTail;

    internal int ConsoleHistoryCount => ViewModel.Console.History.Count;
    internal int ConsoleHistoryDropped => ViewModel.Console.HistoryDropped;
    internal int ConsoleQueueDroppedShown => ViewModel.Console.QueueDropped;

    private void InitConsole()
    {
        if (_consoleInitialized) return;
        _consoleInitialized = true;
        ViewModel.Console.PropertyChanged += OnConsoleModelChanged;
        ToolTip.SetTip(ConsoleDropped,
            $"Dropped because the queue limit ({Log.MaxQueuedEntries}) or history limit ({ConsoleMaxHistory}) was exceeded. Clear removes the history and the view.");
        _consoleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _consoleTimer.Tick += OnConsoleTick;
        _consoleTimer.Start();
        Closed += (_, _) =>
        {
            // Close-time cleanup: stop intake so a reopened window does not double-drain the shared queue.
            if (_consoleTimer is not null)
            {
                _consoleTimer.Tick -= OnConsoleTick;
                _consoleTimer.Stop();
            }
            _consoleTimer = null;
            ViewModel.Console.PropertyChanged -= OnConsoleModelChanged;
        };
        RebuildConsoleView();
    }

    private void OnConsoleTick(object? sender, EventArgs e) => DrainConsole();

    private void OnConsoleModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConsoleViewModel.Rows)) RebuildConsoleView(_consoleFollowTail);
    }

    /// <summary>Only the UI timer controls intake; the model handles history and filtering.</summary>
    internal void DrainConsole()
    {
        _consoleFollowTail = true;
        try { ViewModel.Console.Drain(); }
        finally { _consoleFollowTail = false; }
    }

    private void RebuildConsoleView(bool followTail = false)
    {
        var list = ConsoleList;
        // Preserve selection across batched refreshes so reading past logs keeps its place.
        var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        var offset = scroll?.Offset;
        var wasAtTail = scroll is null || scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 1;
        var filtered = ViewModel.Console.Rows;
        _consoleRefreshing = true;
        try
        {
            list.ItemsSource = filtered;
            list.SelectedItem = ViewModel.Console.SelectedRow;
        }
        finally { _consoleRefreshing = false; }
        if (followTail && wasAtTail && filtered.Count > 0)
        {
            try { list.ScrollIntoView(filtered[^1]); } catch { /* Headless or not yet realized. */ }
        }
        else if (offset.HasValue)
        {
            // Search/filter changes preserve the offset, including zero, within the new extent.
            list.UpdateLayout();
            var newScroll = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            newScroll?.Offset = offset.Value;
        }
    }

    private void OnConsoleSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_consoleRefreshing) ViewModel.Console.Select(ConsoleList.SelectedItem as ConsoleRow);
    }

    internal void ClearConsole() => ViewModel.Console.Clear();

    private void OnConsoleCopy(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrEmpty(ConsoleDetail.Text)) return;
            ConsoleDetail.Focus();
            ConsoleDetail.SelectAll();
            ConsoleDetail.Copy();
        }
        catch
        {
            // Clipboard failure must not break the Editor.
        }
    }

    /// <summary>Logs unlogged Runtime errors without duplicating Step/Stop/Dispose reports.</summary>
    private void LogPendingRuntimeErrors(PlaySession session)
    {
        var errors = session.Runtime.Errors;
        while (_playLoggedErrorCount < errors.Count)
        {
            var item = errors[_playLoggedErrorCount++];
            var message = $"{item.ObjectName}/{item.ComponentType.Name}.{item.MethodName}: {item.Exception.GetType().Name}: {item.Exception.Message}";
            // Original exception is kept for stack/inner details; the forwarding line is only the record location.
            Log.Engine.Error(message, item.Exception);
        }
    }
}
