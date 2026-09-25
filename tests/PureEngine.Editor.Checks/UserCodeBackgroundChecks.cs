using PureEngine.Runtime;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using Avalonia.Threading;
using PureEngine.Core;
using PureEngine.Editor;

static class UserCodeBackgroundChecks
{
    private static readonly PropertyInfo LoadContextProperty =
        typeof(UserCodeCompileResult).GetProperty("LoadContext",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();

    /// <summary>Waits for pool-side progress. The success path does not depend on time; the cutoff is an upper bound treated as failure.</summary>
    private static void SpinUntil(Func<bool> condition, string message)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(15))
            Thread.Sleep(5);
        Check(condition(), message);
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

    /// <summary>A real compilation result. It holds loaded code, so it can verify release when not adopted.</summary>
    private static UserCodeCompileResult RealCompile(string directory)
    {
        var result = UserCodeCompiler.CompileProject(directory);
        Check(result.Success, "Background check fixture failed to compile: "
            + string.Join(" / ", result.Diagnostics.Select(UserCodeCompiler.FormatDiagnostic)));
        return result;
    }

    /// <summary>A gate that lets tests control completion order. It waits on the pool until opened, then returns a real compilation.</summary>
    private sealed class Gate
    {
        public readonly TaskCompletionSource<bool> Open =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Invocations;
    }

    private static UserCodeCompileResult GatedCompile(
        string directory, Gate gate, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref gate.Invocations);
        gate.Open.Task.GetAwaiter().GetResult();
        cancellationToken.ThrowIfCancellationRequested();
        return UserCodeCompiler.CompileProject(directory, cancellationToken);
    }

    /// <summary>Test-only reproduction of the documented adoption procedure. It verifies the caller contract, not a duplicate of creation management.</summary>
    private static bool TryAdopt(UserCodeCompileTracker tracker, UserCodeCompileAttempt attempt,
        Action<UserCodeCompileResult> adopted)
    {
        if (attempt.Superseded || attempt.Canceled || !tracker.IsCurrent(attempt.Ticket)
            || attempt.Result is not { Success: true })
        {
            UserCodeCompileTracker.Release(attempt.Result);
            return false;
        }
        adopted(attempt.Result);
        return true;
    }

    private static WeakReference TrackForUnload(UserCodeCompileResult result)
    {
        var context = (AssemblyLoadContext)LoadContextProperty.GetValue(result)!;
        Check(context is not null, "A successful compile must carry unloadable user code.");
        return new WeakReference(context);
    }

    private static void CheckUnloaded(WeakReference reference, string message)
    {
        // Request collection repeatedly after dropping references instead of waiting on time.
        // Isolate load-to-release work in a separate method so only the weak reference remains after returning.
        for (var i = 0; i < 100 && reference.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Check(!reference.IsAlive, message);
    }

    public static void Run(string parent)
    {
        OffUiThread(parent);
        ReverseCompletionKeepsLatest(parent);
        ContinuousRequestsCollapse(parent);
        ProjectSwitchDropsLateResults(parent);
        ShutdownTouchesNoUi(parent);
        FailureKeepsOldStateAndRetries(parent);
        LiveSceneEditsSurviveAdoption(parent);
        UiResponsiveWhileCompiling(parent);
        CancelBeforeStart(parent);
        FaultIsObserved(parent);
        Console.WriteLine("PASS: background C# compilation off the UI thread, latest-only adoption on overlap/reversal/switch/shutdown, failure retry, live-edit preservation, and UI responsiveness.");
    }

    private static void OffUiThread(string parent)
    {
        var directory = NewDirectory(parent, "BgOffUi");
        try
        {
            WriteSource(directory, "Probe.cs", string.Format(TinyTemplate, "OffUiProbe"));
            Check(Dispatcher.UIThread.CheckAccess(), "Background checks must start on the UI thread.");
            CheckUnloaded(CompileOffUiAndRelease(directory),
                "Released background results must unload their user code.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static WeakReference CompileOffUiAndRelease(string directory)
    {
        var uiThread = Environment.CurrentManagedThreadId;
        var sawUiThread = true;
        var workerThread = uiThread;
        using var tracker = new UserCodeCompileTracker(directory, (root, cancellationToken) =>
        {
            sawUiThread = Dispatcher.UIThread.CheckAccess();
            workerThread = Environment.CurrentManagedThreadId;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(UserCodeCompiler.CompileProject(root, cancellationToken));
        });
        var ticket = tracker.Request();
        var attempt = tracker.CompileAsync(ticket).GetAwaiter().GetResult();
        Check(!attempt.Superseded && !attempt.Canceled && attempt.Result?.Success == true,
            "Background compile must complete for the latest ticket.");
        Check(!sawUiThread && workerThread != uiThread,
            "Source reading and compilation must run off the UI thread.");
        Check(tracker.IsCurrent(attempt.Ticket), "The latest ticket must stay adoptable.");
        var unload = TrackForUnload(attempt.Result!);
        UserCodeCompileTracker.Release(attempt.Result);
        return unload;
    }

    private static void ReverseCompletionKeepsLatest(string parent)
    {
        var directory = NewDirectory(parent, "BgReverse");
        try
        {
            WriteSource(directory, "Probe.cs", string.Format(TinyTemplate, "ReverseProbe"));
            var unload = ReverseOverlapAndRelease(directory);
            CheckUnloaded(unload, "Adopted user code must unload after its owner releases it.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static WeakReference ReverseOverlapAndRelease(string directory)
    {
        // Serialized execution: only one compilation runs, queued requests collapse to the latest.
        var queue = new ConcurrentQueue<Gate>([new Gate(), new Gate()]);
        var order = new ConcurrentQueue<Gate>();
        using var tracker = new UserCodeCompileTracker(directory, (root, cancellationToken) =>
        {
            if (!queue.TryDequeue(out var gate)) throw new Exception("Too many background starts.");
            order.Enqueue(gate);
            return Task.Run(() => GatedCompile(directory, gate, cancellationToken), cancellationToken);
        });
        var first = tracker.Request();
        var run1 = tracker.CompileAsync(first);
        SpinUntil(() => order.Count == 1, "First background compile must start.");
        var second = tracker.Request();
        var run2 = tracker.CompileAsync(second);
        var third = tracker.Request();
        var run3 = tracker.CompileAsync(third);
        Thread.Sleep(50);
        Check(order.Count == 1, "Queued compilations must wait for the active one instead of running concurrently.");
        Check(!run2.IsCompleted && !run3.IsCompleted, "Queued requests must stay pending while the active compile runs.");
        var started = order.ToArray();
        Check(started.Length == 1, "Only the active compile must be in flight.");

        // Newer requests cancel the active work. The middle queued ticket collapses without running.
        started[0].Open.SetResult(true);
        var attempt1 = run1.GetAwaiter().GetResult();
        Check(attempt1.Canceled && attempt1.Result is null, "Stale active work must observe cancellation for the latest request.");
        var attempt2 = run2.GetAwaiter().GetResult();
        Check(attempt2.Superseded && attempt2.Result is null, "A queued ticket overtaken by a newer request must not run.");
        SpinUntil(() => order.Count == 2, "Only the latest queued request must run after the active one finishes.");
        var latest = order.ToArray()[1];
        latest.Open.SetResult(true);
        var attempt3 = run3.GetAwaiter().GetResult();
        var adopted = new List<UserCodeCompileResult>();
        Check(TryAdopt(tracker, attempt3, adopted.Add) && adopted.Count == 1, "The latest completion must be adopted.");
        Check(TrackForUnload(adopted[0]).IsAlive, "Adopted user code must stay alive while owned.");
        var unload = TrackForUnload(adopted[0]);
        UserCodeCompileTracker.Release(adopted[0]);
        return unload;
    }

    private static void ContinuousRequestsCollapse(string parent)
    {
        var directory = NewDirectory(parent, "BgCollapse");
        try
        {
            WriteSource(directory, "Probe.cs", string.Format(TinyTemplate, "CollapseProbe"));
            CheckUnloaded(CollapseAndRelease(directory),
                "Collapsed compilations must release their user code.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static WeakReference CollapseAndRelease(string directory)
    {
        var invocations = 0;
        using var tracker = new UserCodeCompileTracker(directory, (_, cancellationToken) =>
        {
            Interlocked.Increment(ref invocations);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(RealCompile(directory));
        });
        var older = tracker.Request();
        var latest = tracker.Request();
        var superseded = tracker.CompileAsync(older).GetAwaiter().GetResult();
        Check(superseded.Superseded && superseded.Result is null && Volatile.Read(ref invocations) == 0,
            "A ticket superseded before starting must skip its compilation work.");
        var attempt = tracker.CompileAsync(latest).GetAwaiter().GetResult();
        Check(Volatile.Read(ref invocations) == 1 && !attempt.Superseded && tracker.IsCurrent(attempt.Ticket),
            "Only the latest of continuous requests must run and stay adoptable.");
        var unload = TrackForUnload(attempt.Result!);
        UserCodeCompileTracker.Release(attempt.Result);
        return unload;
    }

    private static void ProjectSwitchDropsLateResults(string parent)
    {
        var directoryA = NewDirectory(parent, "BgSwitchA");
        var directoryB = NewDirectory(parent, "BgSwitchB");
        try
        {
            WriteSource(directoryA, "Alpha.cs", string.Format(TinyTemplate, "Alpha"));
            WriteSource(directoryB, "Beta.cs", string.Format(TinyTemplate, "Beta"));
            var unload = SwitchProjectsAndRelease(directoryA, directoryB);
            CheckUnloaded(unload, "The new project's user code must unload after release.");
        }
        finally
        {
            Directory.Delete(directoryA, recursive: true);
            Directory.Delete(directoryB, recursive: true);
        }
    }

    private static WeakReference SwitchProjectsAndRelease(string directoryA, string directoryB)
    {
        var gateA = new Gate();
        var trackerA = new UserCodeCompileTracker(directoryA, (root, cancellationToken) =>
            Task.Run(() => GatedCompile(directoryA, gateA, cancellationToken), cancellationToken));
        var ticketA = trackerA.Request();
        var compileA = trackerA.CompileAsync(ticketA);
        SpinUntil(() => Volatile.Read(ref gateA.Invocations) == 1, "Previous project compile must start.");
        // Project switch: dispose the old binding and create a new binding.
        trackerA.Dispose();
        using var trackerB = new UserCodeCompileTracker(directoryB);
        gateA.Open.SetResult(true);
        var attemptA = compileA.GetAwaiter().GetResult();
        Check(!trackerA.IsCurrent(attemptA.Ticket), "Tickets must expire when their project is switched away.");
        Check(attemptA.Canceled && attemptA.Result is null, "Switching away must cancel the previous project's work.");
        trackerA.Dispose();

        var adopted = new List<UserCodeCompileResult>();
        var attemptB = trackerB.CompileAsync(trackerB.Request()).GetAwaiter().GetResult();
        Check(TryAdopt(trackerB, attemptB, adopted.Add)
            && adopted[0].AttachableTypes.Single().Name == "Beta",
            "Switching projects must not disturb the new project's adoption.");
        var unloadB = TrackForUnload(adopted[0]);
        UserCodeCompileTracker.Release(adopted[0]);
        return unloadB;
    }

    private static void ShutdownTouchesNoUi(string parent)
    {
        var directory = NewDirectory(parent, "BgShutdown");
        try
        {
            WriteSource(directory, "Probe.cs", string.Format(TinyTemplate, "ShutdownProbe"));
            ShutdownAndRelease(directory);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void ShutdownAndRelease(string directory)
    {
        var gate = new Gate();
        var tracker = new UserCodeCompileTracker(directory, (root, cancellationToken) =>
            Task.Run(() => GatedCompile(directory, gate, cancellationToken), cancellationToken));
        var ticket = tracker.Request();
        var compile = tracker.CompileAsync(ticket);
        SpinUntil(() => Volatile.Read(ref gate.Invocations) == 1, "Shutdown probe compile must start.");
        // Shutdown: even if a completion arrives after the window closed, do not touch the closed window or another project.
        tracker.Dispose();
        var adoptedCount = 0;
        gate.Open.SetResult(true);
        var attempt = compile.GetAwaiter().GetResult();
        Check(!tracker.IsCurrent(attempt.Ticket), "Shutdown must invalidate every pending ticket.");
        Check(attempt.Canceled && attempt.Result is null, "Shutdown must cancel in-flight work without producing adoptable state.");
        // Do not call the closed window's adoption callback.
        Check(!TryAdopt(tracker, attempt, _ => adoptedCount++),
            "Completion after shutdown must be rejected, not adopted.");
        Check(adoptedCount == 0, "Completion after shutdown must not touch closed UI.");
        Check(tracker.CompileAsync(ticket).GetAwaiter().GetResult().Superseded,
            "Work requested before shutdown must not restart after it.");
        try { tracker.Request(); throw new Exception("Request after shutdown must be rejected."); }
        catch (ObjectDisposedException) { }
        tracker.Dispose();
    }

    private static void FailureKeepsOldStateAndRetries(string parent)
    {
        var directory = NewDirectory(parent, "BgFailure");
        try
        {
            WriteSource(directory, "Broken.cs", "public class Broken { this is broken; }");
            CheckUnloaded(FailKeepRetryAndRelease(directory, out var before, out var after),
                "Retried user code must unload after release.");
            Check(before == after, "A failed background compile must not change the active registrations.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static WeakReference FailKeepRetryAndRelease(string directory, out int before, out int after)
    {
        using var owner = new ProjectComponents();
        before = owner.Registry.Types.Count;
        using var tracker = new UserCodeCompileTracker(directory);
        var ticket = tracker.Request();
        var failed = tracker.CompileAsync(ticket).GetAwaiter().GetResult();
        Check(!failed.Superseded && !failed.Canceled && failed.Result?.Success == false
            && failed.Result.Diagnostics.Any(d => d.IsError),
            "Broken sources must surface as failed results with error diagnostics.");
        UserCodeCompileTracker.Release(failed.Result);
        after = owner.Registry.Types.Count;
        WriteSource(directory, "Broken.cs", string.Format(TinyTemplate, "Fixed"));
        var retry = tracker.Request();
        var recovered = tracker.CompileAsync(retry).GetAwaiter().GetResult();
        Check(recovered.Result?.Success == true && tracker.IsCurrent(recovered.Ticket),
            "Fixing the source must allow the next ticket to succeed.");
        var unload = TrackForUnload(recovered.Result!);
        UserCodeCompileTracker.Release(recovered.Result);
        return unload;
    }

    private static void LiveSceneEditsSurviveAdoption(string parent)
    {
        var directory = NewDirectory(parent, "BgLiveEdit");
        try
        {
            WriteSource(directory, "Player.cs",
                "using PureEngine.Core;\nnamespace Game;\npublic class Player\n{\n    [Inspector] public int Health = 10;\n}\n");
            AdoptLiveScene(directory);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void AdoptLiveScene(string directory)
    {
        using var owner = new ProjectComponents();
        using var tracker = new UserCodeCompileTracker(directory);
        using var services = GameSession.Create(GameServices.Configure);
        var first = tracker.CompileAsync(tracker.Request()).GetAwaiter().GetResult();
        Check(first.Result?.Success == true, "Live-edit fixture must compile.");
        // Keep the global registration unchanged and migrate by building the same candidate registry as adoption.
        var oldRegistry = owner.CreateCandidateRegistry(first.Result);
        var scene = new Scene();
        var item = scene.AddEmpty();
        item.Rename("Player");
        var oldType = first.Result!.AttachableTypes.Single();
        // Attach directly with the same creation as adoption to avoid polluting the global registration (the CanAttach precheck reads the global registration).
        item.Attach(services.Factory(oldType));
        oldType.GetField("Health")!.SetValue(item.Components.Single(), 73);

        WriteSource(directory, "Player.cs",
            "using PureEngine.Core;\nnamespace Game;\npublic class Player\n{\n    [Inspector] public int Health = 10;\n    [Inspector] public int Added = 42;\n}\n");
        var ticket = tracker.Request();
        var compile = tracker.CompileAsync(ticket);
        // Change Inspector values during compilation. Adopt from the current scene.
        oldType.GetField("Health")!.SetValue(item.Components.Single(), 81);
        var attempt = compile.GetAwaiter().GetResult();
        Check(attempt.Result?.Success == true && tracker.IsCurrent(attempt.Ticket),
            "Recompile with a new member must succeed for the latest ticket.");
        var newRegistry = owner.CreateCandidateRegistry(attempt.Result);
        var migrated = SceneCodeMigrator.Migrate(scene, oldRegistry, newRegistry, services.Factory);
        var migratedComponent = migrated.Objects.Single().Components.Single();
        Check((int)migratedComponent.GetType().GetField("Health")!.GetValue(migratedComponent)! == 81
            && (int)migratedComponent.GetType().GetField("Added")!.GetValue(migratedComponent)! == 42,
            "Adoption must migrate the live scene: edits made during compilation win, new members use initializers.");
        UserCodeCompileTracker.Release(first.Result);
        UserCodeCompileTracker.Release(attempt.Result);
    }

    private static void UiResponsiveWhileCompiling(string parent)
    {
        var directory = NewDirectory(parent, "BgResponsive");
        try
        {
            WriteSource(directory, "Probe.cs", string.Format(TinyTemplate, "ResponsiveProbe"));
            CheckUnloaded(RespondWhileCompiling(directory),
                "Responsiveness probe user code must unload after release.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static WeakReference RespondWhileCompiling(string directory)
    {
        var gate = new Gate();
        using var tracker = new UserCodeCompileTracker(directory, (root, cancellationToken) =>
            Task.Run(() => GatedCompile(directory, gate, cancellationToken), cancellationToken));
        var pending = tracker.CompileAsync(tracker.Request());
        SpinUntil(() => Volatile.Read(ref gate.Invocations) == 1, "Responsiveness probe must start.");
        Check(!pending.IsCompleted, "The compile must stay in flight while its gate is closed.");
        // Responsiveness check: work posted to the UI must run ahead of the in-flight background work.
        var uiRan = false;
        Dispatcher.UIThread.Post(() => uiRan = true);
        Pump();
        Check(uiRan, "The UI thread must process posted work while a compile is in flight.");
        Check(!pending.IsCompleted, "UI work must not complete the gated background compile.");
        gate.Open.SetResult(true);
        var finished = pending.GetAwaiter().GetResult();
        Check(finished.Result?.Success == true, "Gated compile must succeed once opened.");
        var unload = TrackForUnload(finished.Result!);
        UserCodeCompileTracker.Release(finished.Result);
        return unload;
    }

    private static void CancelBeforeStart(string parent)
    {
        var directory = NewDirectory(parent, "BgCancel");
        try
        {
            WriteSource(directory, "Probe.cs", string.Format(TinyTemplate, "CancelProbe"));
            using var tracker = new UserCodeCompileTracker(directory);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var attempt = tracker.CompileAsync(tracker.Request(), cancelled.Token).GetAwaiter().GetResult();
            Check(attempt.Canceled && !attempt.Superseded && attempt.Result is null,
                "Cancellation before start must skip compilation without producing adoptable state.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void FaultIsObserved(string parent)
    {
        var directory = NewDirectory(parent, "BgFault");
        try
        {
            using var owner = new ProjectComponents();
            var before = owner.Registry.Types.Count;
            using var tracker = new UserCodeCompileTracker(directory,
                (_, _) => Task.FromException<UserCodeCompileResult>(
                    new InvalidOperationException("boom-fault")));
            try
            {
                _ = tracker.CompileAsync(tracker.Request()).GetAwaiter().GetResult();
                throw new Exception("Background faults must reach the awaiting caller.");
            }
            catch (InvalidOperationException error)
            {
                Check(error.Message == "boom-fault", "Unexpected background fault payload.");
            }
            Check(owner.Registry.Types.Count == before,
                "An observed background fault must leave the active registrations unchanged.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
