using PureEngine.Core;

internal static class HierarchyLifetimeChecks
{
    public static void Run()
    {
        var source = new Scene();
        var controller = source.AddEmpty();
        controller.Attach(new Probe());
        controller.SetUpdatePriority(controller.GetComponent<Probe>()!, -1);
        var parent = source.AddEmpty();
        parent.Attach(new Probe());
        var child = source.AddEmpty();
        child.Attach(new Probe());
        child.SetParent(parent);
        var survivor = source.AddEmpty();
        survivor.Attach(new Probe());
        var foreign = new Scene().AddEmpty();
        Reject(() => survivor.SetParent(foreign));
        Reject(() => survivor.SetParent(new SceneObject("Standalone")));
        Check(survivor.Parent is null && foreign.Children.Count == 0, "Rejected cross-scene parenting must not change either tree.");

        var registry = new ComponentRegistry();
        registry.Register<Probe>("checks.hierarchy");
        using var runtime = new SceneRuntime(source, registry);
        var live = runtime.Scene.Objects;
        var parentCopy = live.Single(item => item.Id == parent.Id);
        var childCopy = live.Single(item => item.Id == child.Id);
        var survivorCopy = live.Single(item => item.Id == survivor.Id);
        var callback = live.Single(item => item.Id == controller.Id).GetComponent<Probe>()!;
        var parentProbe = parentCopy.GetComponent<Probe>()!;
        var childProbe = childCopy.GetComponent<Probe>()!;
        var survivorProbe = survivorCopy.GetComponent<Probe>()!;
        callback.OnUpdate = () =>
        {
            Check(runtime.Scene.Remove(parentCopy), "Removal should capture the subtree once.");
            Check(!runtime.Scene.Remove(childCopy), "A captured child must already be reserved.");
            Reject(() => survivorCopy.SetParent(parentCopy));
            Reject(() => childCopy.SetParent(null));
            Reject(() => childCopy.SetSiblingIndex(0));
            Reject(() => runtime.Scene.AddEmpty().SetParent(parentCopy));
            callback.OnUpdate = null;
        };
        parentProbe.OnDestroy = () => Reject(() => survivorCopy.SetParent(live[0]));
        runtime.Start();
        runtime.Step(0);
        Check(!live.Contains(parentCopy) && !live.Contains(childCopy) && live.Contains(survivorCopy), "Only the reserved subtree may disappear.");
        Check(parentProbe.Updates == 0 && childProbe.Updates == 0 && survivorProbe.Updates == 1, "Reserved objects must skip the remaining updates.");
        Check(parentProbe.Destroys == 1 && childProbe.Destroys == 1 && parentProbe.Disposals == 1 && childProbe.Disposals == 1, "Subtree cleanup must be single-shot.");
        Reject(() => survivorCopy.SetParent(parentCopy));
        runtime.Step(0);
        Check(survivorProbe.Updates == 2 && live.Contains(survivorCopy), "Survivor must keep updating while remaining in the Scene.");
        runtime.Stop();
        Reject(() => survivorCopy.SetParent(null));
        Check(parentProbe.Destroys == 1 && childProbe.Disposals == 1 && survivorProbe.Destroys == 1 && survivorProbe.Disposals == 1,
            "Stop must not dispose removed components twice.");
        Console.WriteLine("PASS: hierarchy ownership, removal reservations, reparent rejection and single-shot runtime subtree cleanup.");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Invalid hierarchy mutation was accepted.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public sealed class Probe : IDisposable
    {
        public int Updates, Destroys, Disposals;
        public Action? OnUpdate, OnDestroy;
        [Update] public void Tick() { Updates++; OnUpdate?.Invoke(); }
        [Destroy] public void End() { Destroys++; OnDestroy?.Invoke(); }
        public void Dispose() => Disposals++;
    }
}
