using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PureEngine.Core;
using PureEngine.Runtime;

namespace PureEngine.Editor;

public partial class MainWindow
{
    /// <summary>Display history cap. Oldest retained entries are dropped and counted.</summary>
    public const int ConsoleMaxHistory = 1000;

    private readonly List<LogEntry> _consoleHistory = [];
    private DispatcherTimer? _consoleTimer;
    private int _consoleHistoryDropped;
    private int _consoleQueueDroppedBaseline;
    private int _playLoggedErrorCount;
    private bool _showInfo = true;
    private bool _showWarning = true;
    private bool _showError = true;
    private bool _showEngine = true;
    private string _consoleSearch = "";
    private bool _consoleInitialized;

    internal int ConsoleHistoryCount => _consoleHistory.Count;
    internal int ConsoleHistoryDropped => _consoleHistoryDropped;
    internal int ConsoleQueueDroppedShown => Log.DroppedCount - _consoleQueueDroppedBaseline;

    private void InitConsole()
    {
        if (_consoleInitialized) return;
        _consoleInitialized = true;
        _consoleQueueDroppedBaseline = Log.DroppedCount;
        ConsoleEngineFilter.IsCheckedChanged += (_, _) =>
        {
            _showEngine = ConsoleEngineFilter.IsChecked == true;
            RebuildConsoleView();
        };
        ConsoleInfoFilter.IsCheckedChanged += (_, _) =>
        {
            _showInfo = ConsoleInfoFilter.IsChecked == true;
            RebuildConsoleView();
        };
        ConsoleWarningFilter.IsCheckedChanged += (_, _) =>
        {
            _showWarning = ConsoleWarningFilter.IsChecked == true;
            RebuildConsoleView();
        };
        ConsoleErrorFilter.IsCheckedChanged += (_, _) =>
        {
            _showError = ConsoleErrorFilter.IsChecked == true;
            RebuildConsoleView();
        };
        ConsoleSearch.TextChanged += (_, _) =>
        {
            _consoleSearch = ConsoleSearch.Text ?? "";
            RebuildConsoleView();
        };
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
        };
        RebuildConsoleView();
    }

    private void OnConsoleTick(object? sender, EventArgs e) => DrainConsole();

    /// <summary>Timer/batch intake: takes all pending Log entries at once, not one reservation per entry.</summary>
    internal void DrainConsole()
    {
        var entries = Log.Drain();
        if (entries.Length == 0)
        {
            // Queue drops only grow with enqueues, which would leave pending entries.
            // Still refresh the dropped label in case it changed.
            RefreshConsoleMeta();
            return;
        }
        if (_consoleHistory.Count + entries.Length > ConsoleMaxHistory)
        {
            var overflow = _consoleHistory.Count + entries.Length - ConsoleMaxHistory;
            _consoleHistory.RemoveRange(0, overflow);
            _consoleHistoryDropped += overflow;
        }
        _consoleHistory.AddRange(entries);
        RebuildConsoleView(followTail: true);
    }

    private bool PassesConsoleFilter(LogEntry entry)
    {
        if (!_showEngine && entry.Source == LogSource.Engine) return false;
        switch (entry.Level)
        {
            case LogLevel.Info when !_showInfo: return false;
            case LogLevel.Warning when !_showWarning: return false;
            case LogLevel.Error when !_showError: return false;
        }
        if (string.IsNullOrEmpty(_consoleSearch)) return true;
        return (entry.Message?.Contains(_consoleSearch, StringComparison.OrdinalIgnoreCase) == true)
            || (entry.ExceptionDetail?.Contains(_consoleSearch, StringComparison.OrdinalIgnoreCase) == true);
    }

    private void RebuildConsoleView(bool followTail = false)
    {
        var list = ConsoleList;
        // Preserve selection across batched refreshes so reading past logs keeps its place.
        var selected = (list.SelectedItem as ConsoleRow)?.Entry;
        var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        var offset = scroll?.Offset;
        var wasAtTail = scroll is null || scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 1;
        var filtered = new List<ConsoleRow>(_consoleHistory.Count);
        foreach (var entry in _consoleHistory)
        {
            if (PassesConsoleFilter(entry))
                filtered.Add(new ConsoleRow(entry));
        }
        list.ItemsSource = filtered;
        if (selected is not null)
        {
            var restored = filtered.FirstOrDefault(row => ReferenceEquals(row.Entry, selected));
            if (restored is not null)
            {
                list.SelectedItem = restored;
                ShowConsoleDetail(restored);
            }
            else
            {
                list.SelectedItem = null;
                // Filtered out: keep the previous detail so the read content is not lost by a filter switch.
            }
        }
        RefreshConsoleMeta();
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

    private void RefreshConsoleMeta()
    {
        var info = 0;
        var warning = 0;
        var error = 0;
        foreach (var entry in _consoleHistory)
        {
            switch (entry.Level)
            {
                case LogLevel.Info: info++; break;
                case LogLevel.Warning: warning++; break;
                case LogLevel.Error: error++; break;
            }
        }
        ConsoleInfoCount.Text = info.ToString();
        ConsoleWarningCount.Text = warning.ToString();
        ConsoleErrorCount.Text = error.ToString();
        var queueDropped = Log.DroppedCount - _consoleQueueDroppedBaseline;
        if (queueDropped < 0) queueDropped = 0;
        ConsoleDropped.Text = $"Dropped: queue {queueDropped} / history {_consoleHistoryDropped}";
        ToolTip.SetTip(ConsoleDropped,
            $"Dropped because the queue limit ({Log.MaxQueuedEntries}) or history limit ({ConsoleMaxHistory}) was exceeded. Clear removes the history and the view.");
    }

    private void ShowConsoleDetail(ConsoleRow? row) => ConsoleDetail.Text = row?.DetailText ?? "";

    private void OnConsoleSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        ShowConsoleDetail(ConsoleList.SelectedItem as ConsoleRow);

    private void OnConsoleClear(object? sender, RoutedEventArgs e) => ClearConsole();

    /// <summary>Clears retained history and discards not-yet-drained queue entries.</summary>
    internal void ClearConsole()
    {
        _ = Log.Drain();
        _consoleQueueDroppedBaseline = Log.DroppedCount;
        _consoleHistory.Clear();
        _consoleHistoryDropped = 0;
        ConsoleList.SelectedItem = null;
        ShowConsoleDetail(null);
        RebuildConsoleView();
    }

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
