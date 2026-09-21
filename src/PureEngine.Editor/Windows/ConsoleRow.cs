using Avalonia.Media;
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

    /// <summary>Severity accent used for the row's left bar and level badge. Text label remains, so color is never the only indicator.</summary>
    public SolidColorBrush LevelAccent => Entry.Level switch
    {
        LogLevel.Warning => new SolidColorBrush(Color.Parse("#FFB74D")),
        LogLevel.Error => new SolidColorBrush(Color.Parse("#FF5252")),
        _ => new SolidColorBrush(Color.Parse("#64B5F6")),
    };

    public SolidColorBrush LevelBadgeBackground => Entry.Level switch
    {
        LogLevel.Warning => new SolidColorBrush(Color.Parse("#4A2F0B")),
        LogLevel.Error => new SolidColorBrush(Color.Parse("#4A1A1A")),
        _ => new SolidColorBrush(Color.Parse("#1E3A5F")),
    };

    public SolidColorBrush LevelBadgeForeground => Entry.Level switch
    {
        LogLevel.Warning => new SolidColorBrush(Color.Parse("#FFCC80")),
        LogLevel.Error => new SolidColorBrush(Color.Parse("#FF8A80")),
        _ => new SolidColorBrush(Color.Parse("#93C5FD")),
    };

    /// <summary>Source tint: Engine uses the editor accent, Game stays neutral.</summary>
    public SolidColorBrush SourceBadgeBackground => Entry.Source == LogSource.Engine
        ? new SolidColorBrush(Color.Parse("#3D3455"))
        : new SolidColorBrush(Color.Parse("#454545"));

    public SolidColorBrush SourceBadgeForeground => Entry.Source == LogSource.Engine
        ? new SolidColorBrush(Color.Parse("#D6C9F5"))
        : new SolidColorBrush(Color.Parse("#D5D5D5"));

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
