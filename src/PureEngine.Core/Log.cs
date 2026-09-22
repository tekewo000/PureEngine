using System.Runtime.CompilerServices;

namespace PureEngine.Core;

/// <summary>Origin of a log, independent of severity.</summary>
public enum LogSource
{
    Game,
    Engine,
}

/// <summary>Log severity, independent of its source.</summary>
public enum LogLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// A single log record: body, level, time, caller location, and optional exception detail.
/// Normal logs do not capture a stack trace; only <see cref="Log"/> calls with an exception keep it.
/// </summary>
public sealed record LogEntry(
    LogLevel Level,
    string Message,
    DateTimeOffset Timestamp,
    string FilePath,
    int LineNumber,
    string MemberName,
    string? ExceptionDetail)
{
    public LogSource Source { get; init; } = LogSource.Game;
}

/// <summary>
/// Shared logging API. Call via <c>using PureEngine.Core; Log.Info(...)</c>.
/// No logger creation, DI registration, or base class is required.
/// Thread-safe; never throws for queue state and never stops Play.
/// Core has no Editor/Avalonia dependency. The Editor drains the bounded queue periodically in batches.
/// </summary>
public static class Log
{
    /// <summary>Pending queue cap. Oldest entries are dropped and counted when full.</summary>
    public const int MaxQueuedEntries = 1000;

    private static readonly Lock Sync = new();
    private static readonly Queue<LogEntry> Pending = new();
    private static int _droppedCount;

    /// <summary>Total entries dropped from the pending queue due to the cap.</summary>
    public static int DroppedCount
    {
        get
        {
            lock (Sync) return _droppedCount;
        }
    }

    public static void Info(string message,
        [CallerFilePath] string? filePath = null,
        [CallerLineNumber] int lineNumber = 0,
        [CallerMemberName] string? memberName = null) =>
        Enqueue(LogLevel.Info, message, null, filePath, lineNumber, memberName);

    public static void Warning(string message,
        [CallerFilePath] string? filePath = null,
        [CallerLineNumber] int lineNumber = 0,
        [CallerMemberName] string? memberName = null) =>
        Enqueue(LogLevel.Warning, message, null, filePath, lineNumber, memberName);

    public static void Error(string message,
        [CallerFilePath] string? filePath = null,
        [CallerLineNumber] int lineNumber = 0,
        [CallerMemberName] string? memberName = null) =>
        Enqueue(LogLevel.Error, message, null, filePath, lineNumber, memberName);

    public static void Error(Exception? exception,
        [CallerFilePath] string? filePath = null,
        [CallerLineNumber] int lineNumber = 0,
        [CallerMemberName] string? memberName = null) =>
        Enqueue(LogLevel.Error, exception?.Message ?? string.Empty, exception?.ToString(),
            filePath, lineNumber, memberName);

    public static void Error(string message, Exception? exception,
        [CallerFilePath] string? filePath = null,
        [CallerLineNumber] int lineNumber = 0,
        [CallerMemberName] string? memberName = null) =>
        Enqueue(LogLevel.Error, message, exception?.ToString(), filePath, lineNumber, memberName);

    /// <summary>Engine diagnostics share the game log queue and caller metadata.</summary>
    public static class Engine
    {
        public static void Info(string message,
            [CallerFilePath] string? filePath = null,
            [CallerLineNumber] int lineNumber = 0,
            [CallerMemberName] string? memberName = null) =>
            Enqueue(LogLevel.Info, message, null, filePath, lineNumber, memberName, LogSource.Engine);

        public static void Warning(string message,
            [CallerFilePath] string? filePath = null,
            [CallerLineNumber] int lineNumber = 0,
            [CallerMemberName] string? memberName = null) =>
            Enqueue(LogLevel.Warning, message, null, filePath, lineNumber, memberName, LogSource.Engine);

        public static void Error(string message,
            [CallerFilePath] string? filePath = null,
            [CallerLineNumber] int lineNumber = 0,
            [CallerMemberName] string? memberName = null) =>
            Enqueue(LogLevel.Error, message, null, filePath, lineNumber, memberName, LogSource.Engine);

        public static void Error(Exception? exception,
            [CallerFilePath] string? filePath = null,
            [CallerLineNumber] int lineNumber = 0,
            [CallerMemberName] string? memberName = null) =>
            Enqueue(LogLevel.Error, exception?.Message ?? string.Empty, exception?.ToString(),
                filePath, lineNumber, memberName, LogSource.Engine);

        public static void Error(string message, Exception? exception,
            [CallerFilePath] string? filePath = null,
            [CallerLineNumber] int lineNumber = 0,
            [CallerMemberName] string? memberName = null) =>
            Enqueue(LogLevel.Error, message, exception?.ToString(), filePath, lineNumber, memberName, LogSource.Engine);
    }

    private static void Enqueue(LogLevel level, string? message, string? exceptionDetail,
        string? filePath, int lineNumber, string? memberName, LogSource source = LogSource.Game)
    {
        // Recording only: never throw for logging state. Nulls are normalized.
        LogEntry entry;
        try
        {
            entry = new LogEntry(level, message ?? string.Empty, DateTimeOffset.Now,
                filePath ?? string.Empty, lineNumber, memberName ?? string.Empty, exceptionDetail) { Source = source };
        }
        catch
        {
            return;
        }

        lock (Sync)
        {
            try
            {
                if (Pending.Count >= MaxQueuedEntries)
                {
                    Pending.Dequeue();
                    _droppedCount++;
                }
                Pending.Enqueue(entry);
            }
            catch
            {
                // Queue state must never fail the caller.
            }
        }
    }

    /// <summary>
    /// Atomically takes all pending entries for batched UI intake. Returns empty when none.
    /// The Console tab may be hidden; entries stay queued until drained.
    /// </summary>
    public static LogEntry[] Drain()
    {
        lock (Sync)
        {
            if (Pending.Count == 0) return [];
            var entries = Pending.ToArray();
            Pending.Clear();
            return entries;
        }
    }
}
