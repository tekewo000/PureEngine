using System.Text;
using PureEngine.Core;

namespace PureEngine.Player;

/// <summary>Best-effort per-user diagnostics, capped at two files of 256 KiB each.</summary>
public sealed class PlayerLog
{
    public const int MaxFileBytes = 256 * 1024;
    private const int MaxEntryCharacters = 16 * 1024;
    private readonly Lock _sync = new();
    private int _droppedCount;
    public static PlayerLog Current { get; } = new(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    public string? FilePath { get; }
    public string? Failure { get; private set; }

    public string LocationDescription => Failure is null
        ? $"Log: {FilePath}"
        : $"Log file unavailable: {Failure}\nAttempted location: {FilePath ?? "LocalApplicationData was not available"}\nDiagnostics were sent to standard error when available.";

    public PlayerLog(string localApplicationData)
    {
        if (string.IsNullOrWhiteSpace(localApplicationData) || !Path.IsPathFullyQualified(localApplicationData))
        {
            Failure = "The per-user application data directory is unavailable.";
            return;
        }
        FilePath = Path.Combine(localApplicationData, "PureEngine", "Player", "player.log");
        Write("Player diagnostics started.");
    }

    public void Report(Exception error)
    {
        Drain();
        Write(error.ToString());
    }

    public void Drain()
    {
        foreach (var entry in Log.Drain())
            Write($"{entry.Timestamp:O} [{entry.Source}/{entry.Level}] {entry.Message}\n{entry.FilePath}:{entry.LineNumber} {entry.MemberName}\n{entry.ExceptionDetail}");
        var dropped = Log.DroppedCount;
        if (dropped != _droppedCount)
        {
            Write($"The engine log queue dropped {dropped - _droppedCount} entries.");
            _droppedCount = dropped;
        }
    }

    public void Write(string message)
    {
        var text = message.Length <= MaxEntryCharacters ? message : message[..MaxEntryCharacters] + "\n[Entry truncated]";
        text = $"{DateTimeOffset.Now:O} {text}\n";
        lock (_sync)
        {
            if (Failure is null && FilePath is not null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    var bytes = Encoding.UTF8.GetBytes(text);
                    if (File.Exists(FilePath) && new FileInfo(FilePath).Length + bytes.Length > MaxFileBytes)
                        File.Move(FilePath, FilePath + ".previous", overwrite: true);
                    using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                    stream.Write(bytes);
                    return;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    Failure = error.Message;
                }
            }
            // A missing console must not turn an unavailable log directory into a second failure.
            try { Console.Error.WriteLine(text); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }
}
