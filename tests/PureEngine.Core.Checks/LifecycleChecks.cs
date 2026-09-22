using PureEngine.Core;

static class LifecycleChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static T Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    public static void Run()
    {
        NormalStartUpdateStop();
        PreparationFailures();
        StartMidFailure();
        TerminationExceptions();
        DuplicateStopDispose();
        DestroyIsDispose();
        PreparationCleanupErrors();
        ReplayAndSeparation();
        Console.WriteLine("PASS: normal start/update/stop, preparation/start failures, termination exceptions, duplicate stop/dispose, replay, and play separation.");
    }

    private static ComponentRegistry RegistryFor(params (Type Type, string Id)[] entries)
    {
        var registry = new ComponentRegistry();
        foreach (var (type, id) in entries)
        {
            var method = typeof(ComponentRegistry).GetMethod(nameof(ComponentRegistry.Register))!
                .MakeGenericMethod(type);
            method.Invoke(registry, [id]);
        }
        return registry;
    }

    private static void NormalStartUpdateStop()
    {
        var registry = RegistryFor((typeof(LifecycleDisposableProbe), "lifecycle.disposable"));
        var source = new Scene();
        source.AddEmpty().Attach(new LifecycleDisposableProbe());
        using var runtime = new SceneRuntime(source, registry);
        var copy = runtime.Scene.Objects[0].GetComponent<LifecycleDisposableProbe>()!;
        runtime.Start();
        runtime.Step(0.1f);
        runtime.Step(0.1f);
        Check(copy.Starts == 1 && copy.Updates == 2, "Normal Start/Update counts are wrong.");
        Check(copy.Destroys == 0 && copy.Disposes == 0, "Termination ran before Stop.");
        Check(runtime.IsRunning && runtime.Errors.Count == 0, "Running runtime must have no errors.");
        runtime.Stop();
        Check(copy.Starts == 1 && copy.Updates == 2, "Stop must halt updates.");
        Check(copy.Destroys == 1 && copy.Disposes == 1, "Stop must destroy and dispose once.");
        Check(copy.DestroyOrder < copy.DisposeOrder, "Dispose must run after Destroy.");
        Check(!runtime.IsRunning && runtime.Scene.Objects.Count == 0 && runtime.Errors.Count == 0,
            "Stopped runtime must release objects without errors.");
    }

    private static void PreparationFailures()
    {
        // Invalid declaration: constructor rejects before any Start/Destroy/Dispose.
        {
            var registry = new ComponentRegistry();
            var source = new Scene();
            var good = new LifecycleDisposableProbe();
            source.AddEmpty().Attach(good);
            source.AddEmpty().Attach(new LifecycleBadStatic());
            Reject<InvalidOperationException>(() => { using var runtime = new SceneRuntime(source, registry); });
            Check(good.Starts == 0 && good.Destroys == 0 && good.Disposes == 0,
                "Preparation failure must call no lifecycle and no disposal for source instances.");
        }

        // YAML restore failure: already-created IDisposable is released reverse-creation, no Start/Destroy.
        // Runtime Clone uses the same Restore path; a valid source always restores, so the
        // failure release is proven here via Deserialize with later invalid data.
        {
            var registry = RegistryFor(
                (typeof(LifecycleDisposableProbe), "lifecycle.disposable"),
                (typeof(LifecycleIntProbe), "lifecycle.int"));
            var serializer = new SceneSerializer(registry);
            var scene = new Scene();
            var item = scene.AddEmpty();
            item.Rename("First");
            item.Attach(new LifecycleDisposableProbe());
            var second = scene.AddEmpty();
            second.Rename("Second");
            second.Attach(new LifecycleIntProbe { Count = 1 });
            var yaml = serializer.Serialize(scene);
            LifecycleDisposableProbe.CreatedDisposes = 0;
            Reject<InvalidDataException>(() => serializer.Deserialize(yaml.Replace("Count: 1", "Count: wrong")));
            Check(LifecycleDisposableProbe.CreatedDisposes == 1,
                "Failed restore must dispose already-created components.");
        }
    }

    private static void StartMidFailure()
    {
        var registry = RegistryFor((typeof(LifecycleDisposableProbe), "lifecycle.disposable"));
        var source = new Scene();
        // Deterministic Start order via priorities: bad runs first, the rest must be suppressed.
        var tags = new[] { "bad", "later", "last" };
        var priorities = new[] { -10, 0, 10 };
        for (var i = 0; i < 3; i++)
        {
            var item = source.AddEmpty();
            var probe = new LifecycleDisposableProbe { Tag = tags[i] };
            item.Attach(probe);
            item.SetStartPriority(probe, priorities[i]);
        }
        using var runtime = new SceneRuntime(source, registry);
        var copies = runtime.Scene.Objects.Select(o => o.GetComponent<LifecycleDisposableProbe>()!).ToArray();
        var failure = new ApplicationException("start boom");
        copies[0].OnStart = () => throw failure;
        runtime.Start();
        Check(!runtime.IsRunning, "Start failure must stop execution.");
        Check(copies[0].Starts == 1 && copies[1].Starts == 0 && copies[2].Starts == 0,
            "Start failure must suppress the remaining Starts after the first failure.");
        // The failing batch is suppressed, but termination covers Started/Failed/Unstarted all once.
        Check(copies.All(p => p.Destroys == 1 && p.Disposes == 1),
            "Start failure must still destroy and dispose every accepted component once.");
        Check(runtime.Errors.Count == 1 && runtime.Errors[0].Exception == failure
            && runtime.Errors[0].MethodName == "Begin",
            "Start failure must be reported with the lifecycle method name.");
        var error = runtime.Errors[0];
        Check(error.ObjectId != Guid.Empty && !string.IsNullOrEmpty(error.ObjectName)
            && error.ComponentType == typeof(LifecycleDisposableProbe), "Start error lost diagnostic context.");
    }

    private static void TerminationExceptions()
    {
        // One Destroy failure and one Dispose failure must not stop remaining cleanup.
        {
            var registry = RegistryFor((typeof(LifecycleDisposableProbe), "lifecycle.disposable"));
            var source = new Scene();
            for (var i = 0; i < 3; i++) source.AddEmpty().Attach(new LifecycleDisposableProbe { Tag = "P" + i });
            using var runtime = new SceneRuntime(source, registry);
            var copies = runtime.Scene.Objects.Select(o => o.GetComponent<LifecycleDisposableProbe>()!).ToArray();
            copies[0].OnDestroy = () => throw new ApplicationException("destroy boom");
            copies[1].OnDispose = () => throw new ApplicationException("dispose boom");
            runtime.Start();
            runtime.Step(0);
            runtime.Stop();
            Check(copies.All(p => p.Destroys == 1 && p.Disposes == 1),
                "Termination exceptions must still run every Destroy/Dispose once.");
            Check(runtime.Errors.Count == 2, "Destroy and Dispose failures must both be reported.");
            Check(runtime.Errors.Any(e => e.MethodName == "End"), "Destroy failure method name was lost.");
            Check(runtime.Errors.Any(e => e.MethodName == nameof(IDisposable.Dispose)), "Dispose failure method name was lost.");
            Check(runtime.Errors.All(e => e.ObjectId != Guid.Empty && e.ComponentType == typeof(LifecycleDisposableProbe)),
                "Termination errors lost diagnostic context.");
        }

        // Removal-time Dispose failure is reported but does not stop unrelated updates.
        {
            var registry = RegistryFor((typeof(LifecycleDisposableProbe), "lifecycle.disposable"));
            var source = new Scene();
            source.AddEmpty().Attach(new LifecycleDisposableProbe { Tag = "keep" });
            source.AddEmpty().Attach(new LifecycleDisposableProbe { Tag = "drop" });
            using var runtime = new SceneRuntime(source, registry);
            runtime.Start();
            var keep = runtime.Scene.Objects[0].GetComponent<LifecycleDisposableProbe>()!;
            var drop = runtime.Scene.Objects[1].GetComponent<LifecycleDisposableProbe>()!;
            drop.OnDispose = () => throw new ApplicationException("remove dispose boom");
            runtime.Scene.Remove(runtime.Scene.Objects[1]);
            runtime.Step(0);
            Check(drop.Destroys == 1 && drop.Disposes == 1, "Removed component was not terminated once.");
            Check(runtime.Errors.Count == 1 && runtime.Errors[0].MethodName == nameof(IDisposable.Dispose),
                "Removal Dispose failure must be reported.");
            Check(runtime.IsRunning && keep.Updates == 1, "Removal cleanup failure must not stop unrelated updates.");
        }
    }

    private static void DuplicateStopDispose()
    {
        var registry = RegistryFor((typeof(LifecycleDisposableProbe), "lifecycle.disposable"));
        var source = new Scene();
        source.AddEmpty().Attach(new LifecycleDisposableProbe());
        var runtime = new SceneRuntime(source, registry);
        var copy = runtime.Scene.Objects[0].GetComponent<LifecycleDisposableProbe>()!;
        runtime.Start();
        runtime.Step(0);
        runtime.Stop();
        var errors = runtime.Errors.Count;
        runtime.Stop();
        runtime.Dispose();
        runtime.Dispose();
        Check(copy.Destroys == 1 && copy.Disposes == 1, "Duplicate Stop/Dispose must not repeat termination.");
        Check(runtime.Errors.Count == errors, "Duplicate Stop/Dispose must not add errors.");
        Check(!runtime.IsRunning, "Stopped runtime must stay stopped.");
        Reject<InvalidOperationException>(() => runtime.Step(0));
        Reject<InvalidOperationException>(() => runtime.Start());
        Reject<InvalidOperationException>(() => runtime.Scene.AddEmpty());
        runtime.Dispose();

        // Dispose before Start also terminates once and stays idempotent.
        {
            var innerRegistry = RegistryFor((typeof(LifecycleDisposableProbe), "lifecycle.disposable"));
            var innerSource = new Scene();
            innerSource.AddEmpty().Attach(new LifecycleDisposableProbe());
            var inner = new SceneRuntime(innerSource, innerRegistry);
            var pending = inner.Scene.Objects[0].GetComponent<LifecycleDisposableProbe>()!;
            inner.Dispose();
            inner.Dispose();
            inner.Stop();
            Check(pending.Starts == 0 && pending.Destroys == 1 && pending.Disposes == 1,
                "Pre-Start Dispose must release once without Start.");
        }
    }

    private static void DestroyIsDispose()
    {
        foreach (var mode in new[] { "stop", "remove", "before-start" })
        foreach (var throws in new[] { false, true })
        {
            using var runtime = new SceneRuntime(new Scene(), new ComponentRegistry());
            var probes = new AliasDisposeProbe[]
            {
                new PublicAliasDisposeProbe(), new ExplicitAliasDisposeProbe(), new DerivedAliasDisposeProbe()
            };
            var items = probes.Select(probe =>
            {
                probe.Failure = throws ? new ApplicationException("alias dispose failed") : null;
                var item = runtime.Scene.AddEmpty();
                item.Attach(probe);
                return item;
            }).ToArray();
            if (mode != "before-start") runtime.Start();
            if (mode == "remove")
            {
                foreach (var item in items) runtime.Scene.Remove(item);
                runtime.Step(0);
            }
            runtime.Stop();
            runtime.Dispose();
            Check(probes.All(probe => probe.Calls == 1),
                $"Destroy/Dispose aliases must execute once ({mode}, throws={throws}).");
            Check(runtime.Errors.Count == (throws ? probes.Length : 0), "Alias failures must be reported once.");
            if (throws)
                Check(probes.All(probe => runtime.Errors.Count(error => error.Exception == probe.Failure) == 1),
                    "Alias failure must preserve the original exception.");
        }
    }

    private static readonly int[] ExpectedCleanupOrder = [3, 2, 1];

    private static void PreparationCleanupErrors()
    {
        var registry = RegistryFor((typeof(PreparationCleanupProbe), "cleanup"),
            (typeof(PreparationSetterProbe), "setter"));
        var source = new Scene();
        for (var i = 1; i <= 3; i++) source.AddEmpty().Attach(new PreparationCleanupProbe { Id = i });
        source.AddEmpty().Attach(new PreparationSetterProbe());
        var serializer = new SceneSerializer(registry);
        var yaml = serializer.Serialize(source);
        var original = new ApplicationException("restore failed");
        PreparationSetterProbe.Failure = original;
        try
        {
            // Exercise both direct scene loading and Runtime's Clone preparation.
            foreach (var restore in new Action[]
                { () => serializer.Deserialize(yaml), () => { using var runtime = new SceneRuntime(source, registry); } })
            {
                PreparationCleanupProbe.Released.Clear();
                var error = Reject<AggregateException>(restore);
                Check(error.InnerExceptions.Count == 3 && error.InnerExceptions[0].InnerException == original,
                    "Preparation error must be retained before both cleanup failures.");
                Check(error.InnerExceptions[1] == PreparationCleanupProbe.ThirdFailure
                    && error.InnerExceptions[2] == PreparationCleanupProbe.SecondFailure,
                    "Cleanup errors must retain their identity and reverse-creation order.");
                Check(PreparationCleanupProbe.Released.SequenceEqual(ExpectedCleanupOrder),
                    "Cleanup must continue in reverse order after disposal failures.");
            }
        }
        finally { PreparationSetterProbe.Failure = null; }
    }

    private abstract class AliasDisposeProbe
    {
        public int Calls;
        public Exception? Failure;
        protected void Release() { Calls++; if (Failure is not null) throw Failure; }
    }

    private class PublicAliasDisposeProbe : AliasDisposeProbe, IDisposable
    {
        [Destroy] public virtual void Dispose() => Release();
    }

    private sealed class DerivedAliasDisposeProbe : PublicAliasDisposeProbe
    {
        public override void Dispose() => Release();
    }

    private sealed class ExplicitAliasDisposeProbe : AliasDisposeProbe, IDisposable
    {
        [Destroy] void IDisposable.Dispose() => Release();
    }

    private sealed class PreparationCleanupProbe : IDisposable
    {
        [Inspector] public int Id;
        public static readonly List<int> Released = [];
        public static readonly Exception ThirdFailure = new ApplicationException("third cleanup failed");
        public static readonly Exception SecondFailure = new ApplicationException("second cleanup failed");
        public void Dispose()
        {
            Released.Add(Id);
            if (Id == 3) throw ThirdFailure;
            if (Id == 2) throw SecondFailure;
        }
    }

    private sealed class PreparationSetterProbe
    {
        public static Exception? Failure;
#pragma warning disable CA1822 // Reflection tests require these lifecycle/Inspector members to remain instance members.
        [Inspector] public int Value { get => 0; set { if (Failure is not null) throw Failure; } }
#pragma warning restore CA1822
    }

    private static void ReplayAndSeparation()
    {
        var registry = RegistryFor((typeof(LifecycleDisposableProbe), "lifecycle.disposable"));
        var source = new Scene();
        var item = source.AddEmpty();
        item.Rename("Player");
        var original = new LifecycleDisposableProbe { Value = 41 };
        item.Attach(original);
        LifecycleDisposableProbe? firstCopy;
        using (var first = new SceneRuntime(source, registry))
        {
            firstCopy = first.Scene.Objects[0].GetComponent<LifecycleDisposableProbe>()!;
            Check(!ReferenceEquals(firstCopy, original), "Play must use a new instance, not the authoring instance.");
            Check(firstCopy.Value == 41, "Play must inherit Inspector data.");
            first.Start();
            first.Step(0.1f);
            Check(firstCopy.Value == 42 && original.Value == 41, "Play mutation leaked into authoring data.");
            // Runtime-side structural edits must not leak into the source scene.
            var added = first.Scene.AddEmpty();
            added.Attach(new LifecycleDisposableProbe());
            first.Scene.Remove(first.Scene.Objects[0]);
            Check(source.Objects.Count == 1 && source.Objects[0] == item,
                "Play add/remove leaked into the authoring scene.");
            first.Stop();
            Check(firstCopy.Disposes == 1, "Stopped copy must be disposed.");
            Check(source.Objects.Count == 1 && item.GetComponent<LifecycleDisposableProbe>() == original
                && original.Value == 41 && original.Starts == 0,
                "Stop must leave authoring data and instances untouched.");
        }
        Check(firstCopy!.Disposes == 1, "First run state must not be reused.");
        using (var replay = new SceneRuntime(source, registry))
        {
            var secondCopy = replay.Scene.Objects[0].GetComponent<LifecycleDisposableProbe>()!;
            Check(!ReferenceEquals(secondCopy, firstCopy) && !ReferenceEquals(secondCopy, original),
                "Replay must use another new instance.");
            Check(secondCopy.Value == 41 && secondCopy.Starts == 0 && secondCopy.Disposes == 0,
                "Replay must not carry previous execution state.");
            Check(replay.Scene.Objects[0].Id == item.Id && replay.Scene.Objects[0].Name == "Player",
                "Replay must preserve identity.");
            replay.Start();
            replay.Step(0.1f);
            Check(secondCopy.Starts == 1 && secondCopy.Updates == 1, "Replay must start fresh.");
        }
        Check(original.Value == 41 && original.Starts == 0 && original.Disposes == 0,
            "Authoring instance must never gain execution state.");
    }

    private sealed class LifecycleBadStatic
    {
        [Update] public static void Tick() { }
    }

    private sealed class LifecycleIntProbe
    {
        [Inspector] public int Count;
    }
}

public sealed class LifecycleDisposableProbe : IDisposable
{
    [Inspector] public int Value;
    public string Tag = "";
    public int Starts, Updates, Destroys, Disposes;
    public long DestroyOrder = -1, DisposeOrder = -1;
    public Action? OnStart, OnUpdate, OnDestroy, OnDispose;
    public static int CreatedDisposes { get; set; }
    private static long _sequence;
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) throw new InvalidOperationException("Dispose must run once.");
        _disposed = true;
        Disposes++;
        DisposeOrder = ++_sequence;
        // Tracks serializer/runtime preparation-failure releases separately from lifecycle copies.
        CreatedDisposes++;
        OnDispose?.Invoke();
    }

    [Start] private void Begin() { Starts++; OnStart?.Invoke(); }
    [Update] private void Tick(float _) { Updates++; Value++; OnUpdate?.Invoke(); }
    [Destroy] private void End() { Destroys++; DestroyOrder = ++_sequence; OnDestroy?.Invoke(); }
}
