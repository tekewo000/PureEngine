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

    /// <summary>プール側の進行を待つ。成功経路は時刻に依存せず、打切りは失敗扱いの上限である。</summary>
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

    /// <summary>実コンパイルの結果。読み込みコードを持つため、不採用時の解放検証に使える。</summary>
    private static UserCodeCompileResult RealCompile(string directory)
    {
        var result = UserCodeCompiler.CompileProject(directory);
        Check(result.Success, "Background check fixture failed to compile: "
            + string.Join(" / ", result.Diagnostics.Select(UserCodeCompiler.FormatDiagnostic)));
        return result;
    }

    /// <summary>完了順をテスト側で制御する門。開くまでプール上で待機し、実コンパイルを返す。</summary>
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
        return UserCodeCompiler.CompileProject(directory);
    }

    /// <summary>文書化した採用手順の検査用再現。生成管理の重複実装ではなく、呼び出し側の契約確認である。</summary>
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
        // 時刻待ちではなく、参照切断後の回収を繰り返し要求する。
        // 読込から解放までの作業は別メソッドに隔離し、戻り後は弱参照以外が残らないようにする。
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
            return Task.FromResult(UserCodeCompiler.CompileProject(root));
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
            var unloads = ReverseOverlapAndRelease(directory);
            CheckUnloaded(unloads[0], "Rejected oldest user code must be released.");
            CheckUnloaded(unloads[1], "Rejected older user code must be released.");
            CheckUnloaded(unloads[2], "Adopted user code must unload after its owner releases it.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static WeakReference[] ReverseOverlapAndRelease(string directory)
    {
        // 起動順に門を割り当てる。起動自体を直列化して対応付けを決定論にする。
        var queue = new ConcurrentQueue<Gate>([new Gate(), new Gate(), new Gate()]);
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
        SpinUntil(() => order.Count == 2, "Second background compile must start.");
        var third = tracker.Request();
        var run3 = tracker.CompileAsync(third);
        SpinUntil(() => order.Count == 3, "Third background compile must start.");
        var started = order.ToArray();
        Check(started.Length == 3, "Three overlapped compiles must be in flight.");

        // 完了順を逆転させる：最新から完了し、古い順に後から完了する。
        var adopted = new List<UserCodeCompileResult>();
        started[2].Open.SetResult(true);
        var attempt3 = run3.GetAwaiter().GetResult();
        Check(TryAdopt(tracker, attempt3, adopted.Add), "The latest completion must be adopted.");
        Check(adopted.Count == 1, "Only the latest result must be adopted.");
        Check(TrackForUnload(adopted[0]).IsAlive, "Adopted user code must stay alive while owned.");
        started[1].Open.SetResult(true);
        var attempt2 = run2.GetAwaiter().GetResult();
        var unload2 = TrackForUnload(attempt2.Result!);
        Check(!TryAdopt(tracker, attempt2, adopted.Add) && adopted.Count == 1,
            "An older result completing later must not overwrite the latest.");
        started[0].Open.SetResult(true);
        var attempt1 = run1.GetAwaiter().GetResult();
        var unload1 = TrackForUnload(attempt1.Result!);
        Check(!TryAdopt(tracker, attempt1, adopted.Add) && adopted.Count == 1,
            "The oldest result completing last must be rejected.");
        var unload3 = TrackForUnload(adopted[0]);
        UserCodeCompileTracker.Release(adopted[0]);
        return [unload1, unload2, unload3];
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
            var unloads = SwitchProjectsAndRelease(directoryA, directoryB);
            CheckUnloaded(unloads[0], "Late results from the previous project must be released.");
            CheckUnloaded(unloads[1], "The new project's user code must unload after release.");
        }
        finally
        {
            Directory.Delete(directoryA, recursive: true);
            Directory.Delete(directoryB, recursive: true);
        }
    }

    private static WeakReference[] SwitchProjectsAndRelease(string directoryA, string directoryB)
    {
        var gateA = new Gate();
        var trackerA = new UserCodeCompileTracker(directoryA, (root, cancellationToken) =>
            Task.Run(() => GatedCompile(directoryA, gateA, cancellationToken), cancellationToken));
        var ticketA = trackerA.Request();
        var compileA = trackerA.CompileAsync(ticketA);
        SpinUntil(() => Volatile.Read(ref gateA.Invocations) == 1, "Previous project compile must start.");
        // プロジェクト切替：旧束縛を破棄し、新しい束縛を作る。
        trackerA.Dispose();
        using var trackerB = new UserCodeCompileTracker(directoryB);
        gateA.Open.SetResult(true);
        var attemptA = compileA.GetAwaiter().GetResult();
        Check(!trackerA.IsCurrent(attemptA.Ticket), "Tickets must expire when their project is switched away.");
        var unloadA = TrackForUnload(attemptA.Result!);
        UserCodeCompileTracker.Release(attemptA.Result);
        trackerA.Dispose();

        var adopted = new List<UserCodeCompileResult>();
        var attemptB = trackerB.CompileAsync(trackerB.Request()).GetAwaiter().GetResult();
        Check(TryAdopt(trackerB, attemptB, adopted.Add)
            && adopted[0].AttachableTypes.Single().Name == "Beta",
            "Switching projects must not disturb the new project's adoption.");
        var unloadB = TrackForUnload(adopted[0]);
        UserCodeCompileTracker.Release(adopted[0]);
        return [unloadA, unloadB];
    }

    private static void ShutdownTouchesNoUi(string parent)
    {
        var directory = NewDirectory(parent, "BgShutdown");
        try
        {
            WriteSource(directory, "Probe.cs", string.Format(TinyTemplate, "ShutdownProbe"));
            CheckUnloaded(ShutdownAndRelease(directory),
                "Results completing after shutdown must be released.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static WeakReference ShutdownAndRelease(string directory)
    {
        var gate = new Gate();
        var tracker = new UserCodeCompileTracker(directory, (root, cancellationToken) =>
            Task.Run(() => GatedCompile(directory, gate, cancellationToken), cancellationToken));
        var ticket = tracker.Request();
        var compile = tracker.CompileAsync(ticket);
        SpinUntil(() => Volatile.Read(ref gate.Invocations) == 1, "Shutdown probe compile must start.");
        // 終了：画面が閉じた後に完了通知が来ても、閉じた画面や別プロジェクトを変更しない。
        tracker.Dispose();
        var adoptedCount = 0;
        gate.Open.SetResult(true);
        var attempt = compile.GetAwaiter().GetResult();
        Check(!tracker.IsCurrent(attempt.Ticket), "Shutdown must invalidate every pending ticket.");
        var unload = TrackForUnload(attempt.Result!);
        // 閉じた画面の採用コールバックは呼ばない。結果だけ解放する。
        Check(!TryAdopt(tracker, attempt, _ => adoptedCount++),
            "Completion after shutdown must be rejected, not adopted.");
        Check(adoptedCount == 0, "Completion after shutdown must not touch closed UI.");
        Check(tracker.CompileAsync(ticket).GetAwaiter().GetResult().Superseded,
            "Work requested before shutdown must not restart after it.");
        try { tracker.Request(); throw new Exception("Request after shutdown must be rejected."); }
        catch (ObjectDisposedException) { }
        tracker.Dispose();
        return unload;
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
        // 全体登録は変えず、採用時と同じ候補Registryの組立てで移行する。
        var oldRegistry = owner.CreateCandidateRegistry(first.Result);
        var scene = new Scene();
        var item = scene.AddEmpty();
        item.Rename("Player");
        var oldType = first.Result!.AttachableTypes.Single();
        // 全体登録を汚さないため、採用時と同じ生成で直接アタッチする（CanAttachの事前確認は全体登録を見る）。
        item.Attach(services.Factory(oldType));
        oldType.GetField("Health")!.SetValue(item.Components.Single(), 73);

        WriteSource(directory, "Player.cs",
            "using PureEngine.Core;\nnamespace Game;\npublic class Player\n{\n    [Inspector] public int Health = 10;\n    [Inspector] public int Added = 42;\n}\n");
        var ticket = tracker.Request();
        var compile = tracker.CompileAsync(ticket);
        // コンパイル中にInspector値を変更する。採用は現在のSceneから行う。
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
        // 応答確認の方法：処理中のバックグラウンド作業とは別にUIへ投稿した作業が先に実行されること。
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
