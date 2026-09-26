using PureEngine.Core;

namespace PureEngine.Editor;

/// <summary>Bounded console history, filters, and selection; no window, timer, or clipboard ownership.</summary>
public sealed class ConsoleViewModel : EditorObservable
{
    public const int MaxHistory = 1000;
    private readonly List<LogEntry> _history = [];
    private int _queueDroppedBaseline = Log.DroppedCount;

    public ConsoleViewModel() => ClearCommand = new EditorCommand(Clear);

    public IReadOnlyList<LogEntry> History => _history;
    public IReadOnlyList<ConsoleRow> Rows { get; private set; } = [];
    public ConsoleRow? SelectedRow { get; private set; }
    public string Detail { get; private set; } = "";
    public int HistoryDropped { get; private set; }
    public int QueueDropped => Math.Max(0, Log.DroppedCount - _queueDroppedBaseline);
    public int InfoCount => _history.Count(entry => entry.Level == LogLevel.Info);
    public int WarningCount => _history.Count(entry => entry.Level == LogLevel.Warning);
    public int ErrorCount => _history.Count(entry => entry.Level == LogLevel.Error);
    public string DroppedText => $"Dropped: queue {QueueDropped} / history {HistoryDropped}";
    public EditorCommand ClearCommand { get; }
    public bool ClearOnPlay { get; set => SetProperty(ref field, value); } = true;
    public bool ShowInfo { get; set { if (SetProperty(ref field, value)) Rebuild(); } } = true;
    public bool ShowWarning { get; set { if (SetProperty(ref field, value)) Rebuild(); } } = true;
    public bool ShowError { get; set { if (SetProperty(ref field, value)) Rebuild(); } } = true;
    public bool ShowEngine { get; set { if (SetProperty(ref field, value)) Rebuild(); } } = true;
    public string Search { get; set { if (SetProperty(ref field, value ?? "")) Rebuild(); } } = "";

    public void Drain()
    {
        var entries = Log.Drain();
        if (entries.Length != 0)
        {
            var overflow = _history.Count + entries.Length - MaxHistory;
            if (overflow > 0)
            {
                _history.RemoveRange(0, overflow);
                HistoryDropped += overflow;
            }
            _history.AddRange(entries);
            Rebuild();
        }
        Changed(nameof(DroppedText));
    }

    private bool Passes(LogEntry entry) =>
        (ShowEngine || entry.Source != LogSource.Engine)
        && (entry.Level != LogLevel.Info || ShowInfo)
        && (entry.Level != LogLevel.Warning || ShowWarning)
        && (entry.Level != LogLevel.Error || ShowError)
        && (Search.Length == 0 || entry.Message?.Contains(Search, StringComparison.OrdinalIgnoreCase) == true
            || entry.ExceptionDetail?.Contains(Search, StringComparison.OrdinalIgnoreCase) == true);

    private void Rebuild()
    {
        Rows = [.. _history.Where(Passes).Select(entry => new ConsoleRow(entry))];
        SelectedRow = Rows.FirstOrDefault(row => ReferenceEquals(row.Entry, SelectedRow?.Entry));
        Changed(nameof(SelectedRow));
        Changed(nameof(InfoCount));
        Changed(nameof(WarningCount));
        Changed(nameof(ErrorCount));
        Changed(nameof(DroppedText));
        Changed(nameof(Rows));
    }

    public void Select(ConsoleRow? row)
    {
        SelectedRow = row;
        Detail = row?.DetailText ?? "";
        Changed(nameof(SelectedRow));
        Changed(nameof(Detail));
    }

    public void Clear()
    {
        _ = Log.Drain();
        _queueDroppedBaseline = Log.DroppedCount;
        _history.Clear();
        HistoryDropped = 0;
        Select(null);
        Rebuild();
    }
}
