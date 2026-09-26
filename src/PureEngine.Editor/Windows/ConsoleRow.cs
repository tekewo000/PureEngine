using PureEngine.Core;

namespace PureEngine.Editor;

/// <summary>Console list row. Formats a <see cref="LogEntry"/> for time/kind/head display.</summary>
public sealed class ConsoleRow(LogEntry entry)
{
    public LogEntry Entry { get; } = entry;

    public string TimeText => Entry.Timestamp.ToString("HH:mm:ss.fff");

    public string LevelText => Entry.Level.ToString();

    public string SourceText => Entry.Source.ToString();

    /// <summary>Severity accent used for the row's left bar and level badge. Text label remains, so color is never the only indicator.</summary>
    /// <remarks>Info is blue, Warning/Error are amber/red. Structural accent (purple) is reserved for Engine/source.</remarks>
    public string LevelAccent => Entry.Level switch
    {
        LogLevel.Warning => "#E0B45A",
        LogLevel.Error => "#E06A5A",
        _ => "#7AB5F0",
    };

    /// <summary>Level badge fill. Unified with the Project tile field color so Console rows scan like Project rows.</summary>
    public string LevelBadgeBackground { get; } = "#333842";

    public string LevelBadgeForeground => Entry.Level switch
    {
        LogLevel.Warning => "#E8D0A0",
        LogLevel.Error => "#E2B0B0",
        _ => "#C9E2FF",
    };

    /// <summary>Level badge outline. Thin severity cue; fill stays neutral.</summary>
    public string LevelBadgeBorder => Entry.Level switch
    {
        LogLevel.Warning => "#E0B45A",
        LogLevel.Error => "#E06A5A",
        _ => "#7AB5F0",
    };

    /// <summary>Source tint: Engine reuses the editor structural accent (purple, same as Startup pill/focus ring), Game stays neutral.</summary>
    public string SourceBadgeBackground { get; } = "#333842";

    public string SourceBadgeForeground => Entry.Source == LogSource.Engine
        ? "#D6C9F5"
        : "#B8BDC5";

    public string SourceBadgeBorder => Entry.Source == LogSource.Engine
        ? "#8B7CF6"
        : "#3A3F47";

    public string Head
    {
        get
        {
            var message = Entry.Message ?? string.Empty;
            var lineEnd = message.IndexOfAny(['\r', '\n']);
            var first = lineEnd < 0 ? message : message[..lineEnd];
            first = first.Trim();
            if (first.Length == 0 && !string.IsNullOrEmpty(Entry.ExceptionDetail))
                first = "(exception)";
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
                ? "(unknown location)"
                : $"{entry.FilePath}:{entry.LineNumber} ({entry.MemberName})";
            var body = string.IsNullOrEmpty(entry.Message) ? "(no message)" : entry.Message;
            if (string.IsNullOrEmpty(entry.ExceptionDetail))
                return $"[{entry.Source}][{entry.Level}] {entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}{Environment.NewLine}Message:{Environment.NewLine}{body}{Environment.NewLine}Location:{Environment.NewLine}{location}";
            return $"[{entry.Source}][{entry.Level}] {entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}{Environment.NewLine}Message:{Environment.NewLine}{body}{Environment.NewLine}Location:{Environment.NewLine}{location}{Environment.NewLine}Exception:{Environment.NewLine}{entry.ExceptionDetail}";
        }
    }
}
