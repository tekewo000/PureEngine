using PureEngine.Core;

namespace PureEngine.Editor;

/// <summary>Console list row. Formats a <see cref="LogEntry"/> for time/kind/head display.</summary>
public sealed class ConsoleRow
{
    public LogEntry Entry { get; }

    public ConsoleRow(LogEntry entry) => Entry = entry;

    public string TimeText => Entry.Timestamp.ToString("HH:mm:ss.fff");

    public string LevelText => Entry.Level.ToString();

    public string SourceText => Entry.Source.ToString();

    public string Head
    {
        get
        {
            var message = Entry.Message ?? string.Empty;
            var lineEnd = message.IndexOfAny(['\r', '\n']);
            var first = lineEnd < 0 ? message : message[..lineEnd];
            first = first.Trim();
            if (first.Length == 0 && !string.IsNullOrEmpty(Entry.ExceptionDetail))
                first = "(例外あり)";
            const int max = 120;
            return first.Length > max ? first[..max] + "…" : first;
        }
    }

    /// <summary>Full text for the detail pane and Copy: body, location, and exception detail.</summary>
    public string DetailText
    {
        get
        {
            var entry = Entry;
            var location = string.IsNullOrEmpty(entry.FilePath)
                ? "(場所不明)"
                : $"{entry.FilePath}:{entry.LineNumber} ({entry.MemberName})";
            var body = string.IsNullOrEmpty(entry.Message) ? "(本文なし)" : entry.Message;
            if (string.IsNullOrEmpty(entry.ExceptionDetail))
                return $"[{entry.Source}][{entry.Level}] {entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}{Environment.NewLine}本文:{Environment.NewLine}{body}{Environment.NewLine}記録箇所:{Environment.NewLine}{location}";
            return $"[{entry.Source}][{entry.Level}] {entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}{Environment.NewLine}本文:{Environment.NewLine}{body}{Environment.NewLine}記録箇所:{Environment.NewLine}{location}{Environment.NewLine}例外詳細:{Environment.NewLine}{entry.ExceptionDetail}";
        }
    }
}
