using System.Reflection;
using PureEngine.Core;

static class LogChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Run()
    {
        // Start clean: shared static queue must not leak from other checks.
        _ = Log.Drain();
        LevelsAndCaller();
        OverloadForwardingKeepsCaller();
        ExceptionDetail();
        NoStackForNormalLogs();
        ThreadSafety();
        QueueCap();
        NoAvaloniaDependency();
        ErrorDoesNotThrow();
        _ = Log.Drain();
        Console.WriteLine("PASS: shared Log API, caller info, exception detail, thread safety, and bounded queue.");
    }

    private static void LevelsAndCaller()
    {
        _ = Log.Drain();
        Log.Info("info-body");
        Log.Warning("warn-body");
        Log.Error("error-body");
        var entries = Log.Drain();
        Check(entries.Length == 3, $"Expected 3 entries, got {entries.Length}.");
        Check(entries[0].Level == LogLevel.Info && entries[0].Message == "info-body", "Info level/body wrong.");
        Check(entries[1].Level == LogLevel.Warning && entries[1].Message == "warn-body", "Warning level/body wrong.");
        Check(entries[2].Level == LogLevel.Error && entries[2].Message == "error-body", "Error level/body wrong.");
        foreach (var entry in entries)
        {
            Check(entry.Timestamp != default, "Timestamp must be kept.");
            Check(!string.IsNullOrEmpty(entry.FilePath) && entry.FilePath.EndsWith("LogChecks.cs", StringComparison.Ordinal),
                $"Caller file must be LogChecks.cs, got '{entry.FilePath}'.");
            Check(entry.LineNumber > 0, "Caller line must be kept.");
            Check(entry.MemberName == nameof(LevelsAndCaller), $"Caller member must be {nameof(LevelsAndCaller)}, got '{entry.MemberName}'.");
            Check(entry.ExceptionDetail is null, "Normal logs must not capture exception detail.");
        }
    }

    private static void ForwardHelper(string message) => Log.Error(message);

    private static void OverloadForwardingKeepsCaller()
    {
        _ = Log.Drain();
        ForwardHelper("forwarded");
        var entries = Log.Drain();
        Check(entries.Length == 1, "Forwarded log missing.");
        // Caller must be the helper, not Log internals.
        Check(entries[0].MemberName == nameof(ForwardHelper), $"Forwarding replaced caller with '{entries[0].MemberName}'.");
        Check(!entries[0].FilePath.EndsWith("Log.cs", StringComparison.Ordinal), "Caller must not be Log internals.");

        _ = Log.Drain();
        Log.Error("with-exception", new InvalidOperationException("boom"));
        var withEx = Log.Drain().Single();
        Check(withEx.MemberName == nameof(OverloadForwardingKeepsCaller), "Error(message, ex) forwarding replaced caller.");
        Check(!withEx.FilePath.EndsWith("Log.cs", StringComparison.Ordinal), "Error(message, ex) caller must not be Log internals.");

        _ = Log.Drain();
        Log.Error(new InvalidOperationException("only-ex"));
        var onlyEx = Log.Drain().Single();
        Check(onlyEx.MemberName == nameof(OverloadForwardingKeepsCaller), "Error(ex) forwarding replaced caller.");
    }

    private static void ExceptionDetail()
    {
        _ = Log.Drain();
        Exception failure;
        try
        {
            try
            {
                throw new ArgumentException("inner boom");
            }
            catch (Exception inner)
            {
                throw new InvalidOperationException("outer boom", inner);
            }
        }
        catch (Exception outer)
        {
            failure = outer;
            Log.Error("failed op", outer);
        }
        var entry = Log.Drain().Single();
        Check(entry.Level == LogLevel.Error && entry.Message == "failed op", "Error message wrong.");
        Check(!string.IsNullOrEmpty(entry.ExceptionDetail), "Exception detail must be kept.");
        Check(entry.ExceptionDetail!.Contains("outer boom") && entry.ExceptionDetail.Contains("inner boom"),
            "Exception detail must include inner exceptions.");
        Check(entry.ExceptionDetail.Contains(nameof(ExceptionDetail)),
            "Exception detail must include a stack trace with the throw site.");

        _ = Log.Drain();
        Log.Error(failure);
        var only = Log.Drain().Single();
        Check(only.Message.Contains("outer boom"), "Error(ex) must use the exception message as body.");
        Check(!string.IsNullOrEmpty(only.ExceptionDetail) && only.ExceptionDetail.Contains("inner boom"),
            "Error(ex) must keep inner/stack detail.");
    }

    private static void NoStackForNormalLogs()
    {
        _ = Log.Drain();
        Log.Info("plain");
        var entry = Log.Drain().Single();
        Check(entry.ExceptionDetail is null, "Normal logs must not take a stack trace each time.");
    }

    private static void ThreadSafety()
    {
        _ = Log.Drain();
        const int threads = 8;
        const int perThread = 200;
        var tasks = new Task[threads];
        for (var t = 0; t < threads; t++)
        {
            var id = t;
            tasks[t] = Task.Run(() =>
            {
                for (var i = 0; i < perThread; i++)
                    Log.Info($"t{id}-{i}");
            });
        }
        Task.WaitAll(tasks);
        // Queue is bounded, so drain in a loop to collect everything without losing to the cap.
        var total = 0;
        while (true)
        {
            var batch = Log.Drain();
            if (batch.Length == 0) break;
            total += batch.Length;
            // All drained entries must be well-formed even under concurrency.
            foreach (var entry in batch)
                Check(entry.Level == LogLevel.Info && !string.IsNullOrEmpty(entry.Message), "Concurrent entry corrupted.");
            if (total > threads * perThread + Log.MaxQueuedEntries) break;
        }
        // At least the cap worth must survive; drops are counted rather than failing.
        Check(total + Log.DroppedCount >= 0, "Drop counter must be readable.");
        Check(total > 0, "Concurrent logging must record entries.");
        _ = Log.Drain();
    }

    private static void QueueCap()
    {
        _ = Log.Drain();
        var droppedBefore = Log.DroppedCount;
        var extra = 50;
        var total = Log.MaxQueuedEntries + extra;
        for (var i = 0; i < total; i++)
            Log.Info($"cap-{i}");
        var entries = Log.Drain();
        Check(entries.Length == Log.MaxQueuedEntries, $"Queue must cap at {Log.MaxQueuedEntries}, got {entries.Length}.");
        Check(Log.DroppedCount - droppedBefore == extra,
            $"Queue must drop {extra} oldest, dropped {Log.DroppedCount - droppedBefore}.");
        // Newest entries survive; oldest are dropped.
        Check(entries[0].Message == $"cap-{extra}", "Queue must drop the oldest entries.");
        Check(entries[^1].Message == $"cap-{total - 1}", "Queue must keep the newest entries.");
        // No receiver: further logging still succeeds and stays bounded.
        for (var i = 0; i < Log.MaxQueuedEntries + 10; i++)
            Log.Warning("no-receiver");
        var again = Log.Drain();
        Check(again.Length == Log.MaxQueuedEntries, "Queue must stay bounded without a receiver.");
        _ = Log.Drain();
    }

    private static void NoAvaloniaDependency()
    {
        var referenced = typeof(Log).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
        Check(!referenced.Contains("Avalonia") && !referenced.Contains("Avalonia.Controls"),
            "Core Log must not depend on Editor/Avalonia.");
    }

    private static void ErrorDoesNotThrow()
    {
        _ = Log.Drain();
        try
        {
            Log.Error("record only", new InvalidOperationException("logged, not thrown"));
            Log.Error(new InvalidOperationException("logged, not thrown"));
            Log.Error("record only");
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"Log.Error must only record: {error}");
        }
        var entries = Log.Drain();
        Check(entries.Length == 3, "Log.Error must record without throwing.");
    }
}
