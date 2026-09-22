using PureEngine.Core;
using YamlDotNet.Core;

static class PriorityChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static T Reject<T>(Action action, string? message = null) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException(message ?? $"Expected {typeof(T).Name}.");
    }

    public static void Run()
    {
        DefaultsAndIndependence();
        Ordering();
        Persistence();
        CloneAndSeparation();
        RuntimeMutations();
        TimingBoundaries();
        Console.WriteLine("PASS: priority defaults, independence, ordering, persistence, clone, dynamic add/remove/stop/failures, and timing.");
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

    private static void DefaultsAndIndependence()
    {
        var scene = new Scene();
        var a = scene.AddEmpty();
        var b = scene.AddEmpty();
        var first = new PriorityFullProbe();
        var second = new PriorityFullProbe();
        a.Attach(first);
        b.Attach(second);

        Check(a.GetStartPriority(first) == 0 && a.GetUpdatePriority(first) == 0 && a.GetDestroyPriority(first) == 0,
            "Initial priorities must be 0.");
        a.SetStartPriority(first, -5);
        a.SetUpdatePriority(first, 10);
        a.SetDestroyPriority(first, -1);
        Check(a.GetStartPriority(first) == -5 && a.GetUpdatePriority(first) == 10 && a.GetDestroyPriority(first) == -1,
            "Negative and independent priorities were not stored.");
        Check(b.GetStartPriority(second) == 0 && b.GetUpdatePriority(second) == 0 && b.GetDestroyPriority(second) == 0,
            "Same type on another object must keep independent defaults.");

        b.SetStartPriority(second, 7);
        Check(a.GetStartPriority(first) == -5 && b.GetStartPriority(second) == 7,
            "Per-attachment Start priorities interfered.");

        var dataOnly = new PriorityDataOnly();
        a.Attach(dataOnly);
        Reject<InvalidOperationException>(() => a.SetStartPriority(dataOnly, 1), "Missing lifecycle was accepted.");
        Reject<InvalidOperationException>(() => a.SetUpdatePriority(dataOnly, 1), "Missing lifecycle was accepted.");
        Reject<InvalidOperationException>(() => a.SetDestroyPriority(dataOnly, 1), "Missing lifecycle was accepted.");
        Reject<InvalidOperationException>(() => a.GetStartPriority(new PriorityFullProbe()), "Foreign component was accepted.");
        Reject<ArgumentNullException>(() => a.GetStartPriority(null!), "Null component was accepted.");

        var startOnly = new PriorityStartOnly();
        var holder = scene.AddEmpty();
        holder.Attach(startOnly);
        Reject<InvalidOperationException>(() => holder.SetUpdatePriority(startOnly, 1), "Update priority on Start-only was accepted.");
        Reject<InvalidOperationException>(() => holder.SetDestroyPriority(startOnly, 1), "Destroy priority on Start-only was accepted.");
    }

    private static void Ordering()
    {
        var registry = RegistryFor((typeof(PriorityFullProbe), "prio.full"));
        // Start/Update use independent priorities across objects.
        var source = new Scene();
        var log = new List<string>();
        var a = source.AddEmpty(); a.Rename("A");
        var b = source.AddEmpty(); b.Rename("B");
        var c = source.AddEmpty(); c.Rename("C");
        var pa = new PriorityFullProbe();
        var pb = new PriorityFullProbe();
        var pc = new PriorityFullProbe();
        a.Attach(pa); b.Attach(pb); c.Attach(pc);
        a.SetStartPriority(pa, 10); b.SetStartPriority(pb, -5); c.SetStartPriority(pc, 0);
        a.SetUpdatePriority(pa, -1); b.SetUpdatePriority(pb, 5); c.SetUpdatePriority(pc, 0);
        a.SetDestroyPriority(pa, 3); b.SetDestroyPriority(pb, -2); c.SetDestroyPriority(pc, 1);
        using (var runtime = new SceneRuntime(source, registry))
        {
            var copies = runtime.Scene.Objects.Select(o => o.GetComponent<PriorityFullProbe>()!).ToArray();
            copies[0].Tag = "A"; copies[1].Tag = "B"; copies[2].Tag = "C";
            foreach (var p in copies)
            {
                p.OnStart = () => log.Add("Start:" + p.Tag);
                p.OnUpdate = () =>
                {
                    Check(copies.All(q => q.Starts == 1), "Update ran before all Starts.");
                    log.Add("Update:" + p.Tag);
                };
                p.OnDestroy = () => log.Add("Destroy:" + p.Tag);
            }
            runtime.Start();
            Check(log.Count == 3 && log[0] == "Start:B" && log[1] == "Start:C" && log[2] == "Start:A",
                "Start must run ascending across objects: " + string.Join(",", log));
            log.Clear();
            runtime.Step(0);
            Check(log.Count == 3 && log[0] == "Update:A" && log[1] == "Update:C" && log[2] == "Update:B",
                "Update must use its own priorities: " + string.Join(",", log));
            log.Clear();
            runtime.Scene.Remove(runtime.Scene.Objects[0]);
            runtime.Scene.Remove(runtime.Scene.Objects[1]);
            runtime.Scene.Remove(runtime.Scene.Objects[2]);
            runtime.Step(0);
            Check(log.Count == 3 && log[0] == "Destroy:B" && log[1] == "Destroy:C" && log[2] == "Destroy:A",
                "Frame-end Destroy must be ordered across removed objects: " + string.Join(",", log));
        }

        // Stop destroys all remaining in Destroy order.
        {
            var stopSource = new Scene();
            foreach (var (tag, priority) in new[] { ("X", 5), ("Y", -10), ("Z", 0) })
            {
                var item = stopSource.AddEmpty();
                var probe = new PriorityFullProbe();
                item.Attach(probe);
                item.SetDestroyPriority(probe, priority);
            }
            using var runtime = new SceneRuntime(stopSource, registry);
            var copies = runtime.Scene.Objects.Select(o => o.GetComponent<PriorityFullProbe>()!).ToArray();
            var tags = new[] { "X", "Y", "Z" };
            for (var i = 0; i < copies.Length; i++) copies[i].Tag = tags[i];
            var stopLog = new List<string>();
            foreach (var p in copies) p.OnDestroy = () => stopLog.Add(p.Tag);
            runtime.Start();
            runtime.Step(0);
            runtime.Stop();
            Check(stopLog.Count == 3 && stopLog[0] == "Y" && stopLog[1] == "Z" && stopLog[2] == "X",
                "Stop Destroy must be ordered across objects: " + string.Join(",", stopLog));
        }

        // Equal priorities execute but their order is not asserted.
        {
            var equalSource = new Scene();
            foreach (var _ in new[] { 1, 2, 3 })
                equalSource.AddEmpty().Attach(new PriorityFullProbe());
            using var runtime = new SceneRuntime(equalSource, registry);
            runtime.Start();
            runtime.Step(0);
            var copies = runtime.Scene.Objects.Select(o => o.GetComponent<PriorityFullProbe>()!).ToArray();
            Check(copies.All(p => p.Starts == 1 && p.Updates == 1), "Equal priorities must all execute.");
            runtime.Stop();
            Check(copies.All(p => p.Destroys == 1), "Equal Destroy priorities must all execute.");
        }

        // Additions during the current Start batch wait, even with the smallest priority.
        {
            var batchSource = new Scene();
            PriorityFullProbe? late = null;
            var firstProbe = new PriorityFullProbe { Tag = "first" };
            var secondProbe = new PriorityFullProbe { Tag = "second" };
            batchSource.AddEmpty().Attach(firstProbe);
            batchSource.AddEmpty().Attach(secondProbe);
            using var runtime = new SceneRuntime(batchSource, registry);
            var runtimeFirst = runtime.Scene.Objects[0].GetComponent<PriorityFullProbe>()!;
            runtimeFirst.OnStart = () =>
            {
                late = new PriorityFullProbe { Tag = "late" };
                runtime.Scene.AddEmpty().Attach(late);
                runtime.Scene.Objects[2].SetStartPriority(late, -1000);
                runtime.Scene.Objects[2].SetUpdatePriority(late, -1000);
            };
            runtime.Start();
            Check(late is { Starts: 0 }, "Priority must not interrupt the current Start batch.");
            runtime.Step(0);
            Check(late is { Starts: 1, Updates: 1 }, "Late addition must join the next frame.");
        }
    }

    private static void Persistence()
    {
        var registry = RegistryFor((typeof(PriorityFullProbe), "prio.full"), (typeof(PriorityDataOnly), "prio.data"));
        var serializer = new SceneSerializer(registry);
        var scene = new Scene();
        var item = scene.AddEmpty();
        item.Rename("Holder");
        var probe = new PriorityFullProbe { Count = 3 };
        item.Attach(probe);
        item.SetStartPriority(probe, -7);
        item.SetUpdatePriority(probe, 42);
        item.SetDestroyPriority(probe, -1);
        var yaml = serializer.Serialize(scene);
        Check(yaml.Contains("priorities") && yaml.Contains("start: -7") && yaml.Contains("update: 42") && yaml.Contains("destroy: -1"),
            "Priorities must be saved as attach settings:\n" + yaml);
        Check(!yaml.Contains("Start Priority"), "Display names must not leak into YAML.");
        var restored = serializer.Deserialize(yaml);
        var copy = restored.Objects[0].GetComponent<PriorityFullProbe>()!;
        Check(restored.Objects[0].GetStartPriority(copy) == -7
            && restored.Objects[0].GetUpdatePriority(copy) == 42
            && restored.Objects[0].GetDestroyPriority(copy) == -1, "Priorities did not survive.");
        Check(serializer.Serialize(restored) == yaml, "Priority round-trip changed output.");

        // Old scenes without priorities load as 0.
        var legacy = """
            version: 1
            objects:
            - id: 11111111-1111-1111-1111-111111111111
              name: Legacy
              components:
              - typeId: prio.full
                values:
                  Count: 1
            """;
        var legacyScene = serializer.Deserialize(legacy);
        var legacyProbe = legacyScene.Objects[0].GetComponent<PriorityFullProbe>()!;
        Check(legacyScene.Objects[0].GetStartPriority(legacyProbe) == 0
            && legacyScene.Objects[0].GetUpdatePriority(legacyProbe) == 0
            && legacyScene.Objects[0].GetDestroyPriority(legacyProbe) == 0, "Legacy scene must default to 0.");
        Check(!serializer.Serialize(legacyScene).Contains("priorities"), "All-zero priorities must stay omitted.");

        static void RejectData(Action action, string message)
        {
            try { action(); }
            catch (Exception error) when (error is InvalidDataException or YamlException or ArgumentException or InvalidOperationException)
            { return; }
            throw new InvalidOperationException(message);
        }
        RejectData(() => serializer.Deserialize(yaml.Replace("start: -7", "start: wrong")), "Invalid int accepted.");
        RejectData(() => serializer.Deserialize(yaml.Replace("start: -7", "start: 2147483648")), "Int overflow accepted.");
        RejectData(() => serializer.Deserialize(yaml.Replace("start: -7", "start: [1, 2]")), "Collection accepted as priority.");
        RejectData(() => serializer.Deserialize(yaml.Replace("start: -7", "start: 1.5")), "Float accepted as priority.");
        RejectData(() => serializer.Deserialize(yaml.Replace("start: -7", "bogus: 1")), "Unknown priority accepted.");
        RejectData(() => serializer.Deserialize(yaml.Replace("priorities:", "priorities: 5\n    ignored:")), "Scalar priorities accepted.");
        RejectData(() =>
        {
            var bad = new Scene();
            var holder = bad.AddEmpty();
            holder.Attach(new PriorityDataOnly());
            var captured = serializer.Serialize(bad);
            serializer.Deserialize(captured.Replace("values: {}", "values: {}\n    priorities:\n      start: 1"));
        }, "Priority for a missing lifecycle was discarded.");
    }

    private static void CloneAndSeparation()
    {
        var registry = RegistryFor((typeof(PriorityFullProbe), "prio.full"));
        var serializer = new SceneSerializer(registry);
        var source = new Scene();
        var item = source.AddEmpty();
        var probe = new PriorityFullProbe { Count = 9 };
        item.Attach(probe);
        item.SetStartPriority(probe, 4);
        item.SetUpdatePriority(probe, -4);
        item.SetDestroyPriority(probe, 2);
        var clone = serializer.Clone(source);
        var cloned = clone.Objects[0].GetComponent<PriorityFullProbe>()!;
        Check(clone.Objects[0].GetStartPriority(cloned) == 4
            && clone.Objects[0].GetUpdatePriority(cloned) == -4
            && clone.Objects[0].GetDestroyPriority(cloned) == 2, "Clone must carry priorities.");
        Check(cloned.Count == 9 && !ReferenceEquals(cloned, probe), "Clone must copy Inspector data into new instances.");

        using var runtime = new SceneRuntime(source, registry);
        var runtimeProbe = runtime.Scene.Objects[0].GetComponent<PriorityFullProbe>()!;
        Check(runtime.Scene.Objects[0].GetStartPriority(runtimeProbe) == 4, "Runtime must start from authoring priorities.");
        source.Objects[0].SetStartPriority(probe, 99);
        Check(runtime.Scene.Objects[0].GetStartPriority(runtimeProbe) == 4
            && source.Objects[0].GetStartPriority(probe) == 99, "Authoring edits must not leak into the running copy.");
        runtime.Scene.Objects[0].SetDestroyPriority(runtimeProbe, -50);
        Check(source.Objects[0].GetDestroyPriority(probe) == 2
            && runtime.Scene.Objects[0].GetDestroyPriority(runtimeProbe) == -50, "Runtime edits must not leak into authoring.");
    }

    private static void RuntimeMutations()
    {
        var registry = RegistryFor((typeof(PriorityFullProbe), "prio.full"));
        // Dynamic addition sets priorities before its first Start and joins ordered Updates.
        {
            var source = new Scene();
            source.AddEmpty().Attach(new PriorityFullProbe());
            source.AddEmpty().Attach(new PriorityFullProbe());
            source.Objects[0].SetUpdatePriority(source.Objects[0].Components[0], 0);
            source.Objects[1].SetUpdatePriority(source.Objects[1].Components[0], 10);
            using var runtime = new SceneRuntime(source, registry);
            var baseCopies = runtime.Scene.Objects.Select(o => o.GetComponent<PriorityFullProbe>()!).ToArray();
            baseCopies[0].Tag = "base0";
            baseCopies[1].Tag = "base10";
            runtime.Start();
            var late = new PriorityFullProbe();
            runtime.Scene.AddEmpty().Attach(late);
            runtime.Scene.Objects[2].SetStartPriority(late, -3);
            runtime.Scene.Objects[2].SetUpdatePriority(late, -5);
            runtime.Scene.Objects[2].SetDestroyPriority(late, 7);
            late.Tag = "late";
            var order = new List<string>();
            foreach (var obj in runtime.Scene.Objects)
            {
                var p = obj.GetComponent<PriorityFullProbe>()!;
                p.OnUpdate = () => order.Add(p.Tag);
            }
            runtime.Step(0);
            Check(late.Starts == 1 && late.Updates == 1, "Dynamic component did not start on the next frame.");
            Check(order.Count == 3 && order[0] == "late" && order[1] == "base0" && order[2] == "base10",
                "Dynamic Update must join the global order: " + string.Join(",", order));
        }

        // Removal reservation suppresses Start/Update and destroys once at frame end.
        {
            var source = new Scene();
            source.AddEmpty().Attach(new PriorityFullProbe());
            source.AddEmpty().Attach(new PriorityFullProbe());
            using var runtime = new SceneRuntime(source, registry);
            runtime.Start();
            var keep = runtime.Scene.Objects[0].GetComponent<PriorityFullProbe>()!;
            var drop = runtime.Scene.Objects[1].GetComponent<PriorityFullProbe>()!;
            runtime.Scene.Remove(runtime.Scene.Objects[1]);
            runtime.Step(0);
            Check(drop.Starts == 1 && drop.Updates == 0 && drop.Destroys == 1 && keep.Updates == 1,
                "Reserved removal must skip Update and destroy once.");
        }

        // Exceptions stop the run but still destroy everything once, in priority order.
        {
            var source = new Scene();
            foreach (var (tag, priority) in new[] { ("ok", 0), ("bad", -5), ("last", 5) })
            {
                var obj = source.AddEmpty();
                var probe = new PriorityFullProbe();
                obj.Attach(probe);
                obj.SetStartPriority(probe, priority);
                obj.SetDestroyPriority(probe, priority);
            }
            using var runtime = new SceneRuntime(source, registry);
            var copies = runtime.Scene.Objects.Select(o => o.GetComponent<PriorityFullProbe>()!).ToArray();
            var tags = new[] { "ok", "bad", "last" };
            for (var i = 0; i < copies.Length; i++) copies[i].Tag = tags[i];
            var destroys = new List<string>();
            foreach (var p in copies)
            {
                if (p.Tag == "bad") p.OnStart = () => throw new ApplicationException("boom");
                p.OnDestroy = () => destroys.Add(p.Tag);
            }
            runtime.Start();
            Check(!runtime.IsRunning && runtime.Errors.Count == 1, "Start failure must stop and clean up all.");
            Check(destroys.Count == 3 && destroys[0] == "bad" && destroys[1] == "ok" && destroys[2] == "last",
                "Failure cleanup must stay ordered: " + string.Join(",", destroys));
        }
    }

    private static void TimingBoundaries()
    {
        var registry = RegistryFor((typeof(PriorityFullProbe), "prio.full"));
        var source = new Scene();
        source.AddEmpty().Attach(new PriorityFullProbe { Tag = "one" });
        using var runtime = new SceneRuntime(source, registry);
        var item = runtime.Scene.Objects[0];
        var probe = item.GetComponent<PriorityFullProbe>()!;
        item.SetStartPriority(probe, 1);
        item.SetUpdatePriority(probe, 2);
        item.SetDestroyPriority(probe, 3);
        runtime.Start();
        Reject<InvalidOperationException>(() => item.SetStartPriority(probe, 9), "Post-Start change was accepted.");
        Reject<InvalidOperationException>(() => item.SetUpdatePriority(probe, 9), "Post-Start change was accepted.");
        item.SetDestroyPriority(probe, -9);
        Check(item.GetDestroyPriority(probe) == -9, "Pre-Destroy change must be accepted.");
        runtime.Step(0);
        Reject<InvalidOperationException>(() => item.SetStartPriority(probe, 10), "Running change was accepted.");
        runtime.Stop();
        Reject<InvalidOperationException>(() => item.SetDestroyPriority(probe, 0), "Post-Stop change was accepted.");
        Reject<InvalidOperationException>(() => runtime.Scene.AddEmpty(), "Post-Stop add was accepted.");

        // Destroy callbacks reject priority edits without interrupting cleanup.
        {
            var nested = new Scene();
            nested.AddEmpty().Attach(new PriorityFullProbe());
            nested.AddEmpty().Attach(new PriorityFullProbe());
            using var inner = new SceneRuntime(nested, registry);
            inner.Start();
            var first = inner.Scene.Objects[0].GetComponent<PriorityFullProbe>()!;
            var second = inner.Scene.Objects[1].GetComponent<PriorityFullProbe>()!;
            first.OnDestroy = () => inner.Scene.Objects[1].SetDestroyPriority(second, 5);
            inner.Stop();
            Check(inner.Errors.Count == 1 && first.Destroys == 1 && second.Destroys == 1,
                "Destruction edits must be rejected without interrupting cleanup.");
        }

        // Removed owners reject Start/Update edits but accept Destroy edits until frame end.
        {
            var removal = new Scene();
            removal.AddEmpty().Attach(new PriorityFullProbe());
            using var outer = new SceneRuntime(removal, registry);
            outer.Start();
            var target = outer.Scene.Objects[0];
            var targetProbe = target.GetComponent<PriorityFullProbe>()!;
            outer.Scene.Remove(target);
            Reject<InvalidOperationException>(() => target.SetStartPriority(targetProbe, 1), "Removed Start edit was accepted.");
            Reject<InvalidOperationException>(() => target.SetUpdatePriority(targetProbe, 1), "Removed Update edit was accepted.");
            target.SetDestroyPriority(targetProbe, 8);
            Check(target.GetDestroyPriority(targetProbe) == 8, "Removed Destroy edit must be accepted.");
            outer.Step(0);
            Check(targetProbe.Destroys == 1, "Removed component was not destroyed.");
        }
    }

    private sealed class PriorityStartOnly
    {
        [Start] private void Begin() { }
    }

    private sealed class PriorityDataOnly
    {
        public int Value = 0;
    }
}

public sealed class PriorityFullProbe
{
    [Inspector] public int Count;
    public string Tag = "";
    public int Starts, Updates, Destroys;
    public Action? OnStart, OnUpdate, OnDestroy;
    [Start] private void Begin() { Starts++; OnStart?.Invoke(); }
    [Update] private void Tick() { Updates++; OnUpdate?.Invoke(); }
    [Destroy] private void End() { Destroys++; OnDestroy?.Invoke(); }
}
