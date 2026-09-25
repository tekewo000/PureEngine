using PureEngine.Editor;

static class UserCodeIncrementalChecks
{
    private static void Check(bool condition, string message)
    {
        if (condition) return;
        throw new Exception(message);
    }

    private static string NewDirectory(string parent, string name)
    {
        var directory = Path.Combine(parent, name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void WriteSource(string directory, string fileName, string content) =>
        File.WriteAllText(Path.Combine(directory, fileName), content);

    private const string TinyTemplate = "public class {0} {{ public int Value; }}";

    public static void Run(string parent)
    {
        UnchangedSkipsCompilation(parent);
        SingleFileChangeReusesInputs(parent);
        SameLengthContentChangeIsDetected(parent);
        AddRemoveMoveFiles(parent);
        CrossFileBreakageIsReported(parent);
        FailureRecovers(parent);
        SerializationCollapsesToLatest(parent);
        WatcherUsesShortDebounce();
        Console.WriteLine("PASS: incremental user code compilation reuses inputs, skips unchanged saves, and serializes requests.");
    }

    private static void UnchangedSkipsCompilation(string parent)
    {
        var directory = NewDirectory(parent, "IncUnchanged");
        try
        {
            WriteSource(directory, "Alpha.cs", string.Format(TinyTemplate, "Alpha"));
            using var cache = new UserCodeIncrementalCompiler(directory);
            var first = cache.CompileProject();
            Check(first.Success && !first.Unchanged, "First compile must build new code.");
            var parses = cache.TotalParses;
            var references = cache.TotalReferenceCreations;
            var emits = cache.TotalEmits;
            Check(parses == 1 && emits == 1 && references > 0, "First compile must parse, reference, and emit.");
            var second = cache.CompileProject();
            Check(second is { Success: true, Unchanged: true }, "Identical inputs must skip compilation.");
            Check(cache.TotalParses == parses && cache.TotalEmits == emits && cache.TotalReferenceCreations == references,
                "Unchanged inputs must not parse, reference, or emit.");
            UserCodeCompileTracker.Release(first);
            UserCodeCompileTracker.Release(second);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void SingleFileChangeReusesInputs(string parent)
    {
        var directory = NewDirectory(parent, "IncSingle");
        try
        {
            WriteSource(directory, "Alpha.cs", string.Format(TinyTemplate, "Alpha"));
            WriteSource(directory, "Beta.cs", string.Format(TinyTemplate, "Beta"));
            using var cache = new UserCodeIncrementalCompiler(directory);
            var first = cache.CompileProject();
            Check(first.Success, "Fixture must compile.");
            var parses = cache.TotalParses;
            var references = cache.TotalReferenceCreations;
            var emits = cache.TotalEmits;
            WriteSource(directory, "Beta.cs", string.Format(TinyTemplate, "Beta2"));
            var second = cache.CompileProject();
            Check(second is { Success: true, Unchanged: false }, "Changing one file must recompile.");
            Check(cache.TotalParses == parses + 1, "Only the changed file must be parsed.");
            Check(cache.TotalReferenceCreations == references, "Unchanged references must be reused.");
            Check(cache.TotalEmits == emits + 1, "Changed inputs must emit once.");
            Check(second.AttachableTypes.Any(t => t.Name == "Beta2"), "Changed code must be reflected.");
            UserCodeCompileTracker.Release(first);
            UserCodeCompileTracker.Release(second);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void SameLengthContentChangeIsDetected(string parent)
    {
        var directory = NewDirectory(parent, "IncSameLength");
        try
        {
            WriteSource(directory, "Probe.cs", "public class Probe { public int Aaa; }");
            using var cache = new UserCodeIncrementalCompiler(directory);
            var first = cache.CompileProject();
            Check(first.Success, "Fixture must compile.");
            WriteSource(directory, "Probe.cs", "public class Probe { public int Bbb; }");
            var second = cache.CompileProject();
            Check(!second.Unchanged && second.Success, "Same-length content changes must be detected by content, not timestamps.");
            Check(second.AttachableTypes.Single().GetField("Bbb") is not null, "Changed member must be reflected.");
            UserCodeCompileTracker.Release(first);
            UserCodeCompileTracker.Release(second);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void AddRemoveMoveFiles(string parent)
    {
        var directory = NewDirectory(parent, "IncFiles");
        try
        {
            WriteSource(directory, "Alpha.cs", string.Format(TinyTemplate, "Alpha"));
            using var cache = new UserCodeIncrementalCompiler(directory);
            var first = cache.CompileProject();
            Check(first.Success && first.AttachableTypes.Single().Name == "Alpha", "Fixture must compile.");
            WriteSource(directory, "Beta.cs", string.Format(TinyTemplate, "Beta"));
            var added = cache.CompileProject();
            Check(added.Success && added.AttachableTypes.Count == 2, "Added files must compile.");
            File.Delete(Path.Combine(directory, "Alpha.cs"));
            var removed = cache.CompileProject();
            Check(removed.Success && removed.AttachableTypes.Single().Name == "Beta", "Removed files must drop their types.");
            var folder = Path.Combine(directory, "Moved");
            Directory.CreateDirectory(folder);
            File.Move(Path.Combine(directory, "Beta.cs"), Path.Combine(folder, "Beta.cs"));
            var moved = cache.CompileProject();
            Check(moved.Success && moved.AttachableTypes.Single().Name == "Beta", "Moved files must keep their types.");
            File.Delete(Path.Combine(folder, "Beta.cs"));
            var empty = cache.CompileProject();
            Check(empty.Success && empty.AttachableTypes.Count == 0, "Deleting all sources must compile to empty.");
            UserCodeCompileTracker.Release(first);
            UserCodeCompileTracker.Release(added);
            UserCodeCompileTracker.Release(removed);
            UserCodeCompileTracker.Release(moved);
            UserCodeCompileTracker.Release(empty);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void CrossFileBreakageIsReported(string parent)
    {
        var directory = NewDirectory(parent, "IncCross");
        try
        {
            WriteSource(directory, "Base.cs", "public class Base { public int Value; }");
            WriteSource(directory, "User.cs", "public class User { public int Copy = new Base().Value; }");
            using var cache = new UserCodeIncrementalCompiler(directory);
            var first = cache.CompileProject();
            Check(first.Success, "Fixture must compile.");
            WriteSource(directory, "Base.cs", "public class Base { public string? Value; }");
            var broken = cache.CompileProject();
            Check(!broken.Success && !broken.Unchanged && broken.Diagnostics.Any(d => d.IsError),
                "Breaking a dependency must fail even when the dependent file is unchanged.");
            UserCodeCompileTracker.Release(first);
            UserCodeCompileTracker.Release(broken);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void FailureRecovers(string parent)
    {
        var directory = NewDirectory(parent, "IncFailure");
        try
        {
            WriteSource(directory, "Broken.cs", "public class Broken { this is broken; }");
            using var cache = new UserCodeIncrementalCompiler(directory);
            var failed = cache.CompileProject();
            Check(!failed.Success && !failed.Unchanged, "Broken sources must fail.");
            WriteSource(directory, "Broken.cs", string.Format(TinyTemplate, "Fixed"));
            var fixedResult = cache.CompileProject();
            Check(fixedResult.Success && fixedResult.AttachableTypes.Single().Name == "Fixed", "Fixing the source must recover.");
            var repeated = cache.CompileProject();
            Check(repeated.Unchanged, "Repeating the fixed inputs must skip compilation.");
            UserCodeCompileTracker.Release(failed);
            UserCodeCompileTracker.Release(fixedResult);
            UserCodeCompileTracker.Release(repeated);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void SerializationCollapsesToLatest(string parent)
    {
        var directory = NewDirectory(parent, "IncSerialize");
        try
        {
            WriteSource(directory, "Probe.cs", string.Format(TinyTemplate, "Probe"));
            var concurrent = 0;
            var maximum = 0;
            var invocations = 0;
            using var tracker = new UserCodeCompileTracker(directory, (root, cancellationToken) =>
            {
                var running = Interlocked.Increment(ref concurrent);
                Interlocked.Exchange(ref maximum, Math.Max(Volatile.Read(ref maximum), running));
                Interlocked.Increment(ref invocations);
                try
                {
                    Thread.Sleep(50);
                    cancellationToken.ThrowIfCancellationRequested();
                    return Task.FromResult(UserCodeCompiler.CompileProject(directory, cancellationToken));
                }
                finally
                {
                    Interlocked.Decrement(ref concurrent);
                }
            });
            var tickets = new List<UserCodeCompileTicket>();
            for (var i = 0; i < 5; i++) tickets.Add(tracker.Request());
            var runs = tickets.Select(ticket => tracker.CompileAsync(ticket)).ToArray();
            var attempts = runs.Select(run => run.GetAwaiter().GetResult()).ToArray();
            Check(Volatile.Read(ref maximum) <= 1, "Continuous requests must not run concurrently.");
            Check(Volatile.Read(ref invocations) == 1, "Only the latest of continuous requests must run.");
            Check(attempts.Count(a => a.Superseded) == 4 && attempts.Count(a => !a.Superseded && !a.Canceled) == 1,
                "Older tickets must collapse without running.");
            foreach (var attempt in attempts) UserCodeCompileTracker.Release(attempt.Result);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void WatcherUsesShortDebounce() =>
        Check(UserCodeWatcher.DebounceDelay == TimeSpan.FromMilliseconds(250), "Save debounce must be 250ms.");
}
