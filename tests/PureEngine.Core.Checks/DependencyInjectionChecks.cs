using PureEngine.Core;
using PureEngine.Core.Attributes;
using PureEngine.Editor;

/// <summary>
/// Core の生成用 factory 対応を MS DI なしで確認する。Core が知るのは Func&lt;Type, object&gt; のみ。
/// </summary>
static class DependencyInjectionChecks
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
        RegisterWithoutNewConstraint();
        NormalNew();
        FactoryThroughAllPaths();
        InspectorRestoredAfterCtor();
        FactoryContractViolations();
        FactoryFailureIsReported();
        NoFallbackToParameterless();
        RuntimePreparationFailure();
        NormalStopOrder();
        TryAttachPolicy();
        Console.WriteLine("PASS: constructor factory for restore/clone/runtime/attach, inspector timing, contract, failures, and disposal order.");
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

    private static void RegisterWithoutNewConstraint()
    {
        var registry = new ComponentRegistry();
        registry.Register<CtorProbe>("di.probe");
        Check(registry.GetType("di.probe") == typeof(CtorProbe), "Injectable type registration failed.");
        Reject<ArgumentException>(() => registry.Register<CtorProbe>("other"));
    }

    private static void NormalNew()
    {
        var service = new FakeDep("manual");
        var direct = new CtorProbe(service);
        Check(ReferenceEquals(direct.Service, service), "Plain new must keep the given dependency.");
        Check(direct.Value == 0 && direct.StartedValue == -1, "New instance must start from initializers.");
    }

    private static void FactoryThroughAllPaths()
    {
        DiTracking.Reset();
        var registry = RegistryFor((typeof(CtorProbe), "di.probe"));
        var serializer = new SceneSerializer(registry);
        var shared = new FakeDep("shared");
        Func<Type, object> factory = type => new CtorProbe(shared);

        var source = new Scene();
        var item = source.AddEmpty();
        item.Rename("Player");
        item.Attach(new CtorProbe(new FakeDep("authoring")) { Value = 5 });

        var clone = serializer.Clone(source, factory);
        var cloneCopy = clone.Objects[0].GetComponent<CtorProbe>()!;
        Check(cloneCopy.Value == 5 && ReferenceEquals(cloneCopy.Service, shared),
            "Clone must restore Inspector data into a factory instance.");

        var yaml = serializer.Serialize(source);
        var restored = serializer.Deserialize(yaml, factory);
        var restoredCopy = restored.Objects[0].GetComponent<CtorProbe>()!;
        Check(restoredCopy.Value == 5 && ReferenceEquals(restoredCopy.Service, shared),
            "Deserialize must restore Inspector data into a factory instance.");

        using (var runtime = new SceneRuntime(source, registry, factory))
        {
            var copy = runtime.Scene.Objects[0].GetComponent<CtorProbe>()!;
            Check(ReferenceEquals(copy.Service, shared) && copy.Value == 5 && copy.Starts == 0,
                "Runtime must use the factory before Start.");
            runtime.Start();
            Check(copy.Starts == 1 && copy.StartedValue == 5, "Start must see restored Inspector data.");
            runtime.Step(0);
            Check(copy.Updates == 1, "Update must run after injected Start.");
        }

        var target = new SceneObject("Drop");
        Check(ComponentAssets.CanAttach(target, typeof(PureEngine.Editor.Samples.PlayerStats)),
            "Existing asset policy must stay intact.");
        var attachTarget = new SceneObject("Attach");
        var localRegistry = RegistryFor((typeof(CtorProbe), "di.probe"));
        Check(localRegistry.GetType("di.probe") == typeof(CtorProbe), "Local registry must resolve.");
        var attached = new CtorProbe(shared);
        attachTarget.Attach(attached);
        Check(ReferenceEquals(attachTarget.GetComponent<CtorProbe>(), attached), "Attach must keep the instance.");
    }

    private static void InspectorRestoredAfterCtor()
    {
        var registry = RegistryFor((typeof(CtorProbe), "di.probe"));
        var serializer = new SceneSerializer(registry);
        var shared = new FakeDep("shared");
        var source = new Scene();
        source.AddEmpty().Attach(new CtorProbe(new FakeDep("authoring")) { Value = 41 });
        var clone = serializer.Clone(source, type => new CtorProbe(shared));
        var copy = clone.Objects[0].GetComponent<CtorProbe>()!;
        Check(copy.CtorObservedValue == 0, "Constructor must not see YAML values.");
        using var runtime = new SceneRuntime(source, registry, type => new CtorProbe(shared));
        var running = runtime.Scene.Objects[0].GetComponent<CtorProbe>()!;
        runtime.Start();
        Check(running.CtorObservedValue == 0 && running.StartedValue == 41,
            "Save-based initialization belongs in Start, not the constructor.");
    }

    private static void FactoryContractViolations()
    {
        var registry = RegistryFor((typeof(CtorProbe), "di.probe"));
        var serializer = new SceneSerializer(registry);
        var source = new Scene();
        var item = source.AddEmpty();
        item.Rename("Holder");
        item.Attach(new CtorProbe(new FakeDep("authoring")) { Value = 1 });
        var yaml = serializer.Serialize(source);

        var nullError = Reject<InvalidOperationException>(() => serializer.Deserialize(yaml, _ => null!));
        Check(nullError.Message.Contains("di.probe") && nullError.Message.Contains(nameof(CtorProbe)),
            "Null factory results must identify the component.");

        var wrongError = Reject<InvalidOperationException>(() => serializer.Deserialize(yaml, _ => new object()));
        Check(wrongError.Message.Contains("di.probe") && wrongError.Message.Contains(nameof(CtorProbe)),
            "Wrong-type factory results must identify the component.");

        var derivedError = Reject<InvalidOperationException>(() => serializer.Deserialize(yaml, _ => new DerivedProbe(new FakeDep("x"))));
        Check(derivedError.Message.Contains("di.probe"), "Derived instances must be rejected as non-exact types.");
    }

    private static void FactoryFailureIsReported()
    {
        DiTracking.Reset();
        var registry = RegistryFor((typeof(TrackedProbe), "di.first"), (typeof(NeedMissing), "di.missing"));
        var serializer = new SceneSerializer(registry);
        var scene = new Scene();
        var first = scene.AddEmpty();
        first.Rename("First");
        first.Attach(new TrackedProbe { Tag = "first" });
        var second = scene.AddEmpty();
        second.Rename("Second");
        second.Attach(new NeedMissing());
        var yaml = serializer.Serialize(scene);

        var original = new InvalidOperationException("Unable to resolve service for type 'MissingDep'.");
        var error = Reject<InvalidOperationException>(() => serializer.Deserialize(yaml, type =>
        {
            if (type == typeof(NeedMissing)) throw original;
            return new TrackedProbe();
        }));
        Check(error.Message.Contains("di.missing") && error.Message.Contains(nameof(NeedMissing))
            && error.Message.Contains("Second") && ReferenceEquals(error.InnerException, original),
            "Factory failures must identify object, typeId and type while keeping the original.");
        Check(DiTracking.Released.SequenceEqual(["first"]),
            "Failed restore must dispose created components in reverse order.");
    }

    private static void NoFallbackToParameterless()
    {
        var registry = RegistryFor((typeof(DualProbe), "di.dual"));
        var serializer = new SceneSerializer(registry);
        var scene = new Scene();
        scene.AddEmpty().Attach(new DualProbe());
        var yaml = serializer.Serialize(scene);
        // DualProbe has a parameterless constructor, but a failing factory must still fail.
        var error = Reject<InvalidOperationException>(() => serializer.Deserialize(yaml,
            _ => throw new InvalidOperationException("factory boom")));
        Check(error.InnerException?.Message == "factory boom", "Failing factories must not fall back to parameterless creation.");
    }

    private static void RuntimePreparationFailure()
    {
        DiTracking.Reset();
        var registry = RegistryFor((typeof(TrackedProbe), "di.first"), (typeof(NeedMissing), "di.missing"));
        var source = new Scene();
        var first = source.AddEmpty();
        first.Rename("First");
        first.Attach(new TrackedProbe { Tag = "first" });
        var second = source.AddEmpty();
        second.Rename("Second");
        var missing = new NeedMissing();
        second.Attach(missing);

        var original = new InvalidOperationException("Unable to resolve service.");
        var error = Reject<InvalidOperationException>(() => new SceneRuntime(source, registry, type =>
        {
            if (type == typeof(NeedMissing)) throw original;
            return new TrackedProbe();
        }));
        Check(error.Message.Contains(nameof(NeedMissing)) && ReferenceEquals(error.InnerException, original),
            "Runtime preparation must report the failing component with its original error.");
        Check(DiTracking.Released.SequenceEqual(["first"]), "Runtime preparation must dispose clones without Destroy.");
        Check(missing.Starts == 0, "Preparation failure must call no Start.");
    }

    private static void NormalStopOrder()
    {
        var registry = RegistryFor((typeof(TrackedProbe), "di.tracked"));
        var source = new Scene();
        source.AddEmpty().Attach(new TrackedProbe { Tag = "run" });
        var shared = new FakeDep("shared");
        using var runtime = new SceneRuntime(source, registry, type => new TrackedProbe { Service = shared });
        var copy = runtime.Scene.Objects[0].GetComponent<TrackedProbe>()!;
        runtime.Start();
        runtime.Step(0);
        Check(copy.Starts == 1 && copy.Updates == 1, "Injected runtime must start and update.");
        runtime.Stop();
        Check(copy.Destroys == 1 && copy.Disposes == 1, "Stop must destroy and dispose once.");
        Check(copy.DestroyOrder < copy.DisposeOrder, "Dispose must run after Destroy.");
        runtime.Stop();
        Check(copy.Destroys == 1 && copy.Disposes == 1, "Duplicate Stop must stay a no-op.");
    }

    private static void TryAttachPolicy()
    {
        var target = new SceneObject("Target");
        var calls = 0;
        Func<Type, object> counting = type => { calls++; return Activator.CreateInstance(type)!; };
        Check(!PureEngine.Editor.ComponentAssets.TryAttach(null, typeof(CtorProbe), counting) && calls == 0,
            "Attach without a target must not call the factory.");
        Check(!PureEngine.Editor.ComponentAssets.TryAttach(target, typeof(CtorProbe), counting) && calls == 0,
            "Unlisted types must be rejected without calling the factory.");

        var listed = new SceneObject("Listed");
        var assetType = typeof(PureEngine.Editor.Samples.PlayerStats);
        Check(PureEngine.Editor.ComponentAssets.TryAttach(listed, assetType, counting) && calls == 1,
            "Listed assets must attach through the factory path.");
        Check(listed.GetComponent<PureEngine.Editor.Samples.PlayerStats>() is not null,
            "Factory attach must produce the requested exact type.");
        Check(!PureEngine.Editor.ComponentAssets.TryAttach(listed, assetType, counting) && calls == 1,
            "Duplicate attach must be rejected without calling the factory.");

        var failure = new SceneObject("Failure");
        var boom = new InvalidOperationException("Unable to resolve service.");
        var attachError = Reject<InvalidOperationException>(() => PureEngine.Editor.ComponentAssets.TryAttach(
            failure, assetType, _ => throw boom));
        Check(ReferenceEquals(attachError.InnerException, boom) && failure.Components.Count == 0,
            "Failed edit attach must report and leave no incomplete component.");

        var wrong = new SceneObject("Wrong");
        Reject<InvalidOperationException>(() => PureEngine.Editor.ComponentAssets.TryAttach(
            wrong, assetType, _ => new object()));
        Check(wrong.Components.Count == 0, "Wrong-type factory results must leave no component.");
    }

    private sealed class FakeDep
    {
        public string Name;
        public FakeDep(string name = "") => Name = name;
    }

    private class CtorProbe
    {
        public readonly FakeDep Service;
        public int CtorObservedValue;
        public int StartedValue = -1;
        public int Starts, Updates;
        [Inspector] public int Value;

        public CtorProbe(FakeDep service)
        {
            Service = service;
            CtorObservedValue = Value;
        }

        [Start] private void Begin() { Starts++; StartedValue = Value; }
        [Update] private void Tick(float dt) => Updates++;
    }

    private sealed class DerivedProbe : CtorProbe
    {
        public DerivedProbe(FakeDep service) : base(service) { }
    }

    private sealed class DualProbe
    {
        public DualProbe() { }
        public DualProbe(FakeDep _) { }
    }

    private sealed class NeedMissing
    {
        public int Starts;
        [Start] private void Begin() => Starts++;
    }

    private sealed class TrackedProbe : IDisposable
    {
        [Inspector] public string Tag = "";
        public FakeDep? Service;
        public int Starts, Updates, Destroys, Disposes;
        public long DestroyOrder = -1, DisposeOrder = -1;
        private static long _sequence;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) throw new InvalidOperationException("Dispose must run once.");
            _disposed = true;
            Disposes++;
            DisposeOrder = ++_sequence;
            DiTracking.Released.Add(Tag);
        }

        [Start] private void Begin() => Starts++;
        [Update] private void Tick() => Updates++;
        [Destroy] private void End() { Destroys++; DestroyOrder = ++_sequence; }
    }

    private static class DiTracking
    {
        public static readonly List<string> Released = [];
        public static void Reset() => Released.Clear();
    }
}
