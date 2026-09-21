using PureEngine.Core;
using PureEngine.Core.Attributes;

static class SceneRuntimeChecks
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

    private static (Scene Source, SceneRuntime Runtime, RuntimeProbe[] Probes) Create(int count)
    {
        var registry = new ComponentRegistry();
        registry.Register<RuntimeProbe>("runtime.probe");
        var source = new Scene();
        for (var i = 0; i < count; i++) source.AddEmpty().Attach(new RuntimeProbe());
        var runtime = new SceneRuntime(source, registry);
        return (source, runtime, runtime.Scene.Objects.Select(item => item.GetComponent<RuntimeProbe>()!).ToArray());
    }

    public static void Run()
    {
        CopyAndOrder();
        AddAndRemove();
        FailuresAndStop();
        SchemaAndInheritance();
        Console.WriteLine("PASS: lifecycle order, dt, runtime copy/replay, deferred mutations, cleanup, failures, reentrancy, and inherited declarations.");
    }

    private static void CopyAndOrder()
    {
        var registry = new ComponentRegistry();
        registry.Register<RuntimeProbe>("runtime.probe");
        var source = new Scene();
        var item = source.AddEmpty();
        item.Rename("Unsaved player");
        var original = new RuntimeProbe { Value = 41, Text = "001", Enabled = false, Seconds = 1.25f, Hidden = 999 };
        item.Attach(original);
        using (var runtime = new SceneRuntime(source, registry))
        {
            var copy = runtime.Scene.Objects[0].GetComponent<RuntimeProbe>()!;
            Check(copy != original && runtime.Scene != source && runtime.Scene.Objects[0] != item
                && runtime.Scene.Objects[0].Id == item.Id && runtime.Scene.Objects[0].Name == item.Name,
                "Runtime must copy identity into new objects.");
            Check(copy.Value == 41 && copy.Text == "001" && !copy.Enabled && copy.Seconds == 1.25f
                && copy.Hidden == 7 && copy.Starts == 0, "Only Inspector data should be copied, before Start.");
            Reject<InvalidOperationException>(() => runtime.Step(0));
            runtime.Start();
            runtime.Step(0.125f);
            Check(copy.Starts == 1 && copy.Updates == 1 && copy.LastDelta == 0.125f, "Lifecycle counts/dt are wrong.");
            Check(original.Value == 41 && original.Starts == 0 && original.Updates == 0, "Runtime mutated authoring component.");
            Reject<InvalidOperationException>(runtime.Start);
            foreach (var invalid in new[] { -1f, float.NaN, float.PositiveInfinity })
                Reject<ArgumentOutOfRangeException>(() => runtime.Step(invalid));
            Check(copy.Updates == 1 && runtime.IsRunning, "Invalid dt must not execute a frame or stop the runtime.");
            runtime.Stop();
            runtime.Stop();
            Check(copy.Destroys == 1 && !runtime.IsRunning && runtime.Scene.Objects.Count == 0, "Stop must destroy once and release objects.");
            Reject<InvalidOperationException>(() => runtime.Step(0));
            Reject<InvalidOperationException>(() => runtime.Scene.AddEmpty());
            Reject<InvalidOperationException>(runtime.Start);
        }
        using (var replay = new SceneRuntime(source, registry))
        {
            var copy = replay.Scene.Objects[0].GetComponent<RuntimeProbe>()!;
            Check(copy.Value == 41 && copy.Hidden == 7 && copy.Starts == 0, "Replay must reset runtime-only state.");
            replay.Start();
            Check(copy.Starts == 1, "Replay must call Start on a new instance.");
        }

        var (_, ordered, probes) = Create(3);
        using (ordered)
        {
            foreach (var probe in probes)
                probe.OnUpdate = () => Check(probes.All(p => p.Starts == 1), "Update ran before all Starts.");
            var noDelta = new NoDeltaProbe();
            ordered.Scene.Objects[0].Attach(noDelta);
            ordered.Start();
            ordered.Step(0);
            ordered.Step(0.5f);
            Check(probes.All(p => p.Starts == 1 && p.Updates == 2) && noDelta.Updates == 2,
                "Start once and both Update signatures must work.");
        }
    }

    private static void AddAndRemove()
    {
        var (_, runtime, probes) = Create(2);
        using (runtime)
        {
            var objects = runtime.Scene.Objects.ToArray();
            RuntimeProbe? added = null;
            // Whichever callback runs first deletes the other: this does not depend on equal-priority ordering.
            for (var i = 0; i < probes.Length; i++)
            {
                var index = i;
                probes[i].OnUpdate = () =>
                {
                    if (added is not null) return;
                    Check(runtime.Scene.Remove(objects[1 - index]), "First removal must be accepted.");
                    Check(!runtime.Scene.Remove(objects[1 - index]), "Duplicate removal must be ignored.");
                    Check(probes[1 - index].Destroys == 0, "Destroy ran inside Update instead of at frame end.");
                    Reject<InvalidOperationException>(() => objects[1 - index].Attach(new NoDeltaProbe()));
                    added = new RuntimeProbe();
                    runtime.Scene.AddEmpty().Attach(added);
                };
            }
            runtime.Start();
            runtime.Step(0.1f);
            Check(probes.Sum(p => p.Updates) == 1 && probes.Sum(p => p.Destroys) == 1,
                "Reserved deletion must suppress the remaining Update and destroy at frame end.");
            Check(added is { Starts: 0, Updates: 0 } && runtime.Scene.Objects.Count == 2,
                "New component joined the frame in which it was added.");
            runtime.Step(0.2f);
            Check(added is { Starts: 1, Updates: 1 }, "Addition must start and update on the next frame.");
            var destroyed = probes.Single(p => p.Destroys == 1);
            var fresh = runtime.Scene.AddEmpty();
            Reject<InvalidOperationException>(() => fresh.Attach(destroyed));
            Check(fresh.Components.Count == 0, "Destroyed instances must not start a second lifetime in this runtime.");
            var late = new NoDeltaProbe();
            runtime.Scene.Objects[0].Attach(late);
            runtime.Step(0.3f);
            Check(late.Updates == 1, "Attach on an existing object must join next Step.");
        }

        var (_, beforeStart, initial) = Create(2);
        using (beforeStart)
        {
            var objects = beforeStart.Scene.Objects.ToArray();
            RuntimeProbe? fromStart = null;
            for (var i = 0; i < initial.Length; i++)
            {
                var index = i;
                initial[i].OnStart = () =>
                {
                    beforeStart.Scene.Remove(objects[1 - index]);
                    fromStart = new RuntimeProbe();
                    beforeStart.Scene.AddEmpty().Attach(fromStart);
                };
            }
            beforeStart.Start();
            Check(initial.Sum(p => p.Starts) == 1 && initial.Sum(p => p.Destroys) == 1,
                "Removal before Start must skip Start but still Destroy the accepted instance.");
            Check(fromStart is { Starts: 0 }, "Start-created component must wait for Step.");
            var child = new RuntimeProbe();
            fromStart!.OnStart = () => beforeStart.Scene.AddEmpty().Attach(child);
            beforeStart.Step(0);
            Check(fromStart.Updates == 1 && child.Starts == 0 && child.Updates == 0,
                "Nested Start addition joined the current batch.");
            beforeStart.Step(0);
            Check(child.Starts == 1 && child.Updates == 1, "Nested addition did not join the next batch.");
        }

        var (_, pendingRemoval, pending) = Create(1);
        using (pendingRemoval)
        {
            pendingRemoval.Scene.Remove(pendingRemoval.Scene.Objects[0]);
            pendingRemoval.Start();
            Check(pending[0].Starts == 0 && pending[0].Destroys == 1, "Pre-start removal leaked an accepted instance.");
        }

        using var empty = new SceneRuntime(new Scene(), new ComponentRegistry());
        var a = empty.Scene.AddEmpty();
        var b = empty.Scene.AddEmpty();
        var shared = new RuntimeProbe();
        a.Attach(shared);
        Reject<InvalidOperationException>(() => b.Attach(shared));
        Reject<InvalidOperationException>(() => b.Attach(new BadStatic()));
        Check(b.Components.Count == 0, "Rejected attachment mutated the component list.");
        Check(!empty.Scene.Remove(new SceneObject("foreign")), "Foreign objects must not be removed.");
        empty.Scene.Remove(b);
        empty.Start();
        empty.Step(0);
        Check(empty.Scene.Objects.Count == 1 && shared.Updates == 1, "Empty/data-only objects need no Update entry.");

        var (_, selfRemoval, self) = Create(1);
        using (selfRemoval)
        {
            self[0].OnStart = () => selfRemoval.Scene.Remove(selfRemoval.Scene.Objects[0]);
            selfRemoval.Start();
            selfRemoval.Step(0);
            Check(self[0].Starts == 1 && self[0].Updates == 0 && self[0].Destroys == 1,
                "Self-removal during Start must skip Update and destroy once.");
        }
    }

    private static void FailuresAndStop()
    {
        foreach (var phase in new[] { "Start", "Update" })
        {
            var (_, runtime, probes) = Create(3);
            using (runtime)
            {
                var failure = new ApplicationException("original failure");
                foreach (var probe in probes)
                {
                    if (phase == "Start") probe.OnStart = () => throw failure;
                    else probe.OnUpdate = () => throw failure;
                    probe.OnDestroy = () => throw new ApplicationException("cleanup failure");
                }
                runtime.Start();
                if (phase == "Update") runtime.Step(0.1f);
                Check(!runtime.IsRunning && probes.Sum(p => phase == "Start" ? p.Starts : p.Updates) == 1,
                    "Lifecycle failure did not stop the rest of the batch.");
                Check(probes.All(p => p.Destroys == 1) && runtime.Errors.Count == 4,
                    "Cleanup must include unstarted instances and continue after Destroy failures.");
                var error = runtime.Errors[0];
                Check(error.Exception == failure && error.ComponentType == typeof(RuntimeProbe)
                    && error.ObjectId != Guid.Empty && !string.IsNullOrEmpty(error.ObjectName)
                    && error.MethodName == (phase == "Start" ? "Begin" : "Tick"), "Diagnostic context was lost.");
                runtime.Stop();
                Check(runtime.Errors.Count == 4 && probes.All(p => p.Destroys == 1), "Stop repeated cleanup.");
            }
        }

        foreach (var phase in new[] { "Start", "Update", "Destroy" })
        {
            var (_, runtime, probes) = Create(2);
            using (runtime)
            {
                foreach (var probe in probes)
                {
                    if (phase == "Start") probe.OnStart = runtime.Stop;
                    if (phase == "Update") probe.OnUpdate = runtime.Stop;
                    probe.OnDestroy = runtime.Stop;
                }
                runtime.Start();
                if (phase == "Update") runtime.Step(0);
                if (phase == "Destroy")
                {
                    runtime.Scene.Remove(runtime.Scene.Objects[0]);
                    runtime.Step(0);
                }
                Check(!runtime.IsRunning && runtime.Errors.Count == 0 && probes.All(p => p.Destroys == 1),
                    "Stop within a callback must finish that callback and clean up once.");
                Check(phase != "Start" || probes.Sum(p => p.Starts) == 1, "Stop did not suppress later Starts.");
                Check(phase != "Update" || probes.Sum(p => p.Updates) == 1, "Stop did not suppress later Updates.");
            }
        }

        var (_, disposedBeforeStart, notStarted) = Create(1);
        disposedBeforeStart.Dispose();
        disposedBeforeStart.Dispose();
        Check(notStarted[0].Starts == 0 && notStarted[0].Destroys == 1, "Dispose before Start must release owned components.");

        var (_, nested, nestedProbes) = Create(1);
        using (nested)
        {
            nestedProbes[0].OnUpdate = () => nested.Step(0);
            nested.Start();
            nested.Step(0);
            Check(!nested.IsRunning && nested.Errors.Single().Exception is InvalidOperationException,
                "A nested Step must fail the callback, not recurse.");
        }

        var (_, destruction, cleanupProbes) = Create(3);
        using (destruction)
        {
            var item = destruction.Scene.Objects[0];
            cleanupProbes[0].OnDestroy = () => destruction.Scene.AddEmpty();
            cleanupProbes[1].OnDestroy = () => destruction.Scene.Remove(item);
            cleanupProbes[2].OnDestroy = () => item.Attach(new NoDeltaProbe());
            destruction.Start();
            destruction.Stop();
            Check(destruction.Errors.Count == 3 && cleanupProbes.All(p => p.Destroys == 1),
                "Destruction mutations must be rejected without interrupting cleanup.");
        }

        var (_, deletedFailure, deleting) = Create(2);
        using (deletedFailure)
        {
            deleting[0].OnDestroy = () => throw new ApplicationException("delete failure");
            deletedFailure.Start();
            deletedFailure.Scene.Remove(deletedFailure.Scene.Objects[0]);
            deletedFailure.Step(0);
            Check(deletedFailure.Errors.Count == 1 && deletedFailure.IsRunning && deleting[1].Updates == 1,
                "A deletion cleanup failure must be reported without preventing unrelated updates.");
        }
    }

    private static void SchemaAndInheritance()
    {
        foreach (var type in new[] { typeof(BadStatic), typeof(BadReturn), typeof(BadAsync), typeof(BadGeneric),
                     typeof(BadParameter), typeof(BadRefParameter), typeof(BadStartParameter), typeof(BadDestroyReturn),
                     typeof(DuplicateMethods), typeof(HiddenDuplicate), typeof(AbstractLifecycle) })
        {
            var error = Reject<InvalidOperationException>(() => ComponentSchema.GetUpdateMethod(type));
            Check(error.Message.Contains(type.FullName!) && error.Message.Contains("method", StringComparison.OrdinalIgnoreCase),
                "Invalid declarations must identify the type and reason.");
        }

        var badSource = new Scene();
        badSource.AddEmpty().Attach(new RuntimeProbe());
        badSource.AddEmpty().Attach(new BadStatic());
        Reject<InvalidOperationException>(() => new SceneRuntime(badSource, new ComponentRegistry()));
        Check(badSource.Objects[0].GetComponent<RuntimeProbe>()!.Starts == 0,
            "Invalid scene must be rejected before any Start.");

        using var runtime = new SceneRuntime(new Scene(), new ComponentRegistry());
        var derived = new DerivedLifecycle();
        runtime.Scene.AddEmpty().Attach(derived);
        runtime.Start();
        runtime.Step(0);
        runtime.Stop();
        Check(derived.Starts == 1 && derived.BaseUpdates == 0 && derived.DerivedUpdates == 1 && derived.Destroys == 1,
            "Private base methods and inherited virtual attributes must execute once with the final override.");
    }

    private sealed class NoDeltaProbe
    {
        public int Updates;
        [Update] private void Tick() => Updates++;
    }
    private sealed class BadStatic { [Update] public static void Tick() { } }
    private sealed class BadReturn { [Update] public int Tick() => 0; }
    private sealed class BadAsync { [Update] public async void Tick() => await Task.Yield(); }
    private sealed class BadGeneric { [Update] public void Tick<T>() { } }
    private sealed class BadParameter { [Update] public void Tick(int dt) { } }
    private sealed class BadRefParameter { [Update] public void Tick(ref float dt) { } }
    private sealed class BadStartParameter { [Start] public void Begin(float dt) { } }
    private sealed class BadDestroyReturn { [Destroy] public Task End() => Task.CompletedTask; }
    private sealed class DuplicateMethods { [Update] public void A() { } [Update] public void B() { } }
    private class HiddenBase { [Update] private void Tick() { } }
    private sealed class HiddenDuplicate : HiddenBase { [Update] private void Tick() { } }
    private abstract class AbstractLifecycle { [Update] public abstract void Tick(); }
    private class BaseLifecycle
    {
        public int Starts, BaseUpdates, Destroys;
        [Start] private void Begin() => Starts++;
        [Update] protected virtual void Tick() => BaseUpdates++;
        [Destroy] private void End() => Destroys++;
    }
    private sealed class DerivedLifecycle : BaseLifecycle
    {
        public int DerivedUpdates;
        protected override void Tick() => DerivedUpdates++;
    }
}

public sealed class RuntimeProbe
{
    [Inspector] public int Value { get; set; }
    [Inspector] public string? Text = "text";
    [Inspector] public bool Enabled = true;
    [Inspector] public float Seconds;
    public int Hidden = 7;
    public int Starts, Updates, Destroys;
    public float LastDelta;
    public Action? OnStart, OnUpdate, OnDestroy;
    [Start] private void Begin() { Starts++; OnStart?.Invoke(); }
    [Update] private void Tick(float dt) { Updates++; Value++; LastDelta = dt; OnUpdate?.Invoke(); }
    [Destroy] private void End() { Destroys++; OnDestroy?.Invoke(); }
}
