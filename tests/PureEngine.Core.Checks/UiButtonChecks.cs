using System.Numerics;
using PureEngine.Core;
using PureEngine.Core.Components;
using YamlDotNet.Core;

static class UiButtonChecks
{
    public static void Run()
    {
        ButtonRoundTrip();
        Requirements();
        Validation();
        Visuals();
        HitButtonRules();
        RuntimeDispatch();
        DeferredHandlerStart();
        Console.WriteLine("PASS: Button save/clone, requirements, handler validation, visuals, overlap/parent/cancel/disable hit rules, and runtime click dispatch.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or YamlException or ArgumentException or InvalidOperationException)
        { return; }
        throw new InvalidOperationException(message);
    }

    private static ComponentRegistry ButtonRegistry()
    {
        var registry = new ComponentRegistry();
        registry.Register<Transform>("core.transform");
        registry.Register<UiElement>("core.ui-element");
        registry.Register<global::Image>("core.image");
        registry.Register<Button>("core.button");
        registry.Register<ClickCounter>("checks.click-counter");
        registry.Register<SecondHandler>("checks.second-handler");
        registry.Register<ThrowingHandler>("checks.throwing-handler");
        registry.Register<SelfRemovingHandler>("checks.self-removing-handler");
        registry.Register<StoppingHandler>("checks.stopping-handler");
        return registry;
    }

    private static SceneObject AddButton(Scene scene, string name, Vector3 position, Vector2 size, int order, bool interactable)
    {
        var item = scene.AddEmpty();
        item.Rename(name);
        item.Attach(new Transform { LocalPosition = position });
        item.Attach(new UiElement { Pivot = Vector2.Zero, SizeDelta = size });
        item.Attach(new global::Image { Sprite = new Sprite(Guid.NewGuid()), Color = Vector4.One, Order = order });
        item.Attach(new Button { Interactable = interactable });
        return item;
    }

    private static void ButtonRoundTrip()
    {
        var registry = ButtonRegistry();
        var serializer = new SceneSerializer(registry);
        var scene = new Scene();
        var item = scene.AddEmpty();
        item.Rename("Play button");
        item.Attach(new Transform());
        item.Attach(new UiElement());
        item.Attach(new Button { Interactable = false });
        var yaml = serializer.Serialize(scene);
        Check(yaml.Contains("core.button") && yaml.Contains("Interactable"),
            "Button save must carry typeId and Interactable.");
        var restored = serializer.Deserialize(yaml);
        var copy = restored.Objects[0].GetComponent<Button>()!;
        Check(!copy.Interactable, "Button Interactable did not survive.");
        Check(serializer.Serialize(restored) == yaml, "Button save/load changed output.");

        var fresh = new Scene();
        var freshItem = fresh.AddEmpty();
        freshItem.Rename("Default");
        freshItem.Attach(new Button());
        Check(freshItem.GetComponent<Button>()!.Interactable, "Button must default to Interactable.");

        var clone = serializer.Clone(scene);
        var cloneButton = clone.Objects[0].GetComponent<Button>()!;
        Check(!cloneButton.Interactable && !ReferenceEquals(cloneButton, item.GetComponent<Button>()),
            "Clone must separate Button instances.");
        item.GetComponent<Button>()!.Interactable = true;
        Check(!cloneButton.Interactable, "Editing the source after Clone must not leak into the clone.");
    }

    private static void Requirements()
    {
        var lone = new SceneObject("Lone");
        lone.Attach(new Button());
        Check(UiComponentRequirements.GetMissing(lone).SequenceEqual([nameof(Transform), nameof(UiElement)]),
            "Button alone must require Transform and UiElement.");
        lone.Attach(new Transform());
        Check(UiComponentRequirements.GetMissing(lone).SequenceEqual([nameof(UiElement)]),
            "Button with Transform must still require UiElement.");
        lone.Attach(new UiElement());
        Check(UiComponentRequirements.GetMissing(lone).Count == 0,
            "Button with Transform and UiElement must be complete without an Image.");
    }

    private static void Validation()
    {
        var scene = new Scene();
        var plain = scene.AddEmpty();
        plain.Rename("Plain");
        plain.Attach(new Button());
        UiButtonValidation.Validate(scene);
        Check(UiButtonValidation.CountHandlers(plain) == 0, "Button without handlers must count zero.");

        plain.Attach(new ClickCounter());
        UiButtonValidation.Validate(scene);
        Check(UiButtonValidation.CountHandlers(plain) == 1, "Single handler must validate.");

        plain.Attach(new SecondHandler());
        Reject(() => UiButtonValidation.Validate(scene), "Multiple handlers must be rejected.");
        try { UiButtonValidation.Validate(scene); }
        catch (InvalidOperationException error)
        {
            Check(error.Message.Contains("Plain") && error.Message.Contains('2'),
                $"Validation error must name the object and count, got '{error.Message}'.");
        }
        Reject(() => { using var unused = new SceneRuntime(scene, ButtonRegistry()); }, "Runtime preparation must reject multiple handlers and refuse to start.");
    }

    private static void Visuals()
    {
        var tints = new[] { UiButtonVisuals.TintFor(UiButtonVisualState.Normal), UiButtonVisuals.TintFor(UiButtonVisualState.Hover),
            UiButtonVisuals.TintFor(UiButtonVisualState.Pressed), UiButtonVisuals.TintFor(UiButtonVisualState.Disabled) };
        Check(tints.Distinct().Count() == 4, "Normal/hover/pressed/disabled tints must all differ.");
        var saved = new Vector4(0.2f, 0.4f, 0.6f, 0.8f);
        var applied = UiButtonVisuals.ApplyTint(saved, UiButtonVisualState.Hover);
        Check(saved == new Vector4(0.2f, 0.4f, 0.6f, 0.8f), "Visuals must not rewrite the saved Image.Color.");
        Check(UiButtonVisuals.ApplyTint(saved, UiButtonVisualState.Normal) == saved, "Normal must keep the saved color.");
        Check(applied != saved, "Hover must change the displayed color.");
        Check(UiButtonVisuals.Resolve(false, true, true) == UiButtonVisualState.Disabled, "Disabled must win over pressed/hover.");
        Check(UiButtonVisuals.Resolve(true, true, true) == UiButtonVisualState.Pressed, "Pressed must win over hover.");
        Check(UiButtonVisuals.Resolve(true, false, true) == UiButtonVisualState.Hover, "Hover must apply when only hovered.");
        Check(UiButtonVisuals.Resolve(true, false, false) == UiButtonVisualState.Normal, "Idle must be normal.");
    }

    private static SceneObject? HitAt(Scene scene, Vector2 viewport, Vector2 point)
    {
        var entries = SceneViewMath.EnumerateLayouts(scene, viewport);
        return SceneViewMath.HitTest(entries, viewport, Vector2.Zero, 1f, point,
            item => item.GetComponent<Button>() is { Interactable: true });
    }

    private static void HitButtonRules()
    {
        var viewport = new Vector2(400, 200);
        var scene = new Scene();
        var back = AddButton(scene, "Back", new Vector3(10, 20, 0), new Vector2(100, 40), 0, true);
        var front = AddButton(scene, "Front", new Vector3(10, 20, 0), new Vector2(100, 40), 5, true);
        Check(HitAt(scene, viewport, new Vector2(50, 40)) == front, "Overlapping buttons must hit only the front target.");
        front.GetComponent<Button>()!.Interactable = false;
        Check(HitAt(scene, viewport, new Vector2(50, 40)) == back, "Disabling the front button must fall through to the next.");
        Check(HitAt(scene, viewport, new Vector2(500, 500)) is null, "Outside release must hit nothing.");
        front.GetComponent<Button>()!.Interactable = true;

        var zero = AddButton(scene, "Zero", new Vector3(200, 50, 0), new Vector2(0, 40), 10, true);
        Check(HitAt(scene, viewport, new Vector2(200, 60)) is null || HitAt(scene, viewport, new Vector2(200, 60)) != zero,
            "Zero-size buttons must not be hit.");
        var flat = AddButton(scene, "Flat", new Vector3(200, 100, 0), new Vector2(40, 40), 10, true);
        flat.GetComponent<Transform>()!.LocalScale = new Vector3(0, 1, 1);
        Check(HitAt(scene, viewport, new Vector2(210, 110)) != flat, "Degenerate transforms must not be hit.");

        var parent = scene.AddEmpty();
        parent.Rename("Parent");
        parent.Attach(new Transform { LocalPosition = new Vector3(50, 50, 0), LocalScale = new Vector3(2, 2, 1) });
        parent.Attach(new UiElement { Pivot = Vector2.Zero, SizeDelta = new Vector2(200, 100) });
        parent.Attach(new Button { Interactable = false });
        var child = scene.AddEmpty();
        child.Rename("Child");
        child.SetParent(parent);
        child.Attach(new Transform { LocalPosition = new Vector3(10, 10, 0) });
        child.Attach(new UiElement { Pivot = Vector2.Zero, SizeDelta = new Vector2(20, 10) });
        child.Attach(new Button());
        var entries = SceneViewMath.EnumerateLayouts(scene, viewport);
        var childEntry = entries.First(entry => ReferenceEquals(entry.Object, child));
        var centerLocal = new Vector2(10, 5);
        var plane = new Matrix3x2(childEntry.WorldScene.M11, childEntry.WorldScene.M12,
            childEntry.WorldScene.M21, childEntry.WorldScene.M22,
            childEntry.WorldScene.M41, childEntry.WorldScene.M42);
        var centerView = Vector2.Transform(centerLocal, plane);
        Check(HitAt(scene, viewport, centerView) == child, "Parent transforms must move the hit area with the visuals.");
        Check(parent.GetComponent<Button>()!.Interactable == false && HitAt(scene, viewport, centerView) == child,
            "Disabling only the parent Button must not disable the child Button.");

        var imageless = scene.AddEmpty();
        imageless.Rename("Imageless");
        imageless.Attach(new Transform { LocalPosition = new Vector3(300, 50, 0) });
        imageless.Attach(new UiElement { Pivot = Vector2.Zero, SizeDelta = new Vector2(40, 20) });
        imageless.Attach(new Button());
        Check(HitAt(scene, viewport, new Vector2(310, 55)) == imageless, "Buttons without an Image must still be hittable.");

        var cover = scene.AddEmpty();
        cover.Rename("Cover");
        cover.Attach(new Transform { LocalPosition = new Vector3(300, 50, 0) });
        cover.Attach(new UiElement { Pivot = Vector2.Zero, SizeDelta = new Vector2(40, 20) });
        cover.Attach(new global::Image { Sprite = new Sprite(Guid.NewGuid()), Order = 100 });
        Check(HitAt(scene, viewport, new Vector2(310, 55)) == imageless,
            "Non-button Images must not block Button input; only Buttons participate in Game hit testing.");
    }

    private sealed class ClickCounter : IUiButtonHandler
    {
        public int Calls;
        public Guid LastId;
        public UiClickContext LastContext;
        public void OnClick(UiClickContext context)
        {
            Calls++;
            LastId = context.ButtonObject.Id;
            LastContext = context;
        }
    }

    private sealed class SecondHandler : IUiButtonHandler
    {
        public void OnClick(UiClickContext _) { }
    }

    private sealed class StartedHandler : IUiButtonHandler
    {
        public bool Started;
        public int Calls;
        [Start] private void Start() => Started = true;
        public void OnClick(UiClickContext _)
        {
            Check(Started, "A dynamically attached handler must Start before OnClick.");
            Calls++;
        }
    }

    private sealed class AttachingHandler : IUiButtonHandler
    {
        public void OnClick(UiClickContext context) =>
            context.Scene.Objects.Single(item => item.Name == "Target").Attach(new StartedHandler());
    }

    private static void DeferredHandlerStart()
    {
        var registry = ButtonRegistry();
        registry.Register<AttachingHandler>("checks.attaching-handler");
        var scene = new Scene();
        var source = scene.AddEmpty();
        source.Attach(new Button());
        source.Attach(new AttachingHandler());
        var target = scene.AddEmpty();
        target.Rename("Target");
        target.Attach(new Button());
        using var runtime = new SceneRuntime(scene, registry);
        runtime.Start();
        var runtimeTarget = runtime.Scene.Objects.Single(item => item.Id == target.Id);
        runtime.EnqueueButtonClick(source.Id);
        runtime.EnqueueButtonClick(target.Id);
        runtime.Step(0);
        var handler = runtimeTarget.GetComponent<StartedHandler>()!;
        Check(runtime.IsRunning && runtime.Errors.Count == 0 && handler.Calls == 0,
            "Clicks for handlers attached during dispatch must wait for their Start batch.");
        runtime.Step(0);
        Check(handler.Started && handler.Calls == 1, "Deferred clicks must run once after Start.");
    }

    private sealed class ThrowingHandler : IUiButtonHandler
    {
        public void OnClick(UiClickContext _) => throw new ApplicationException("click boom");
    }

    private sealed class SelfRemovingHandler : IUiButtonHandler
    {
        public void OnClick(UiClickContext context) => context.Scene.Remove(context.ButtonObject);
    }

    private sealed class StoppingHandler : IUiButtonHandler
    {
        public static Action? RequestStop;
        public void OnClick(UiClickContext _) => RequestStop?.Invoke();
    }

    private static void RuntimeDispatch()
    {
        var registry = ButtonRegistry();
        var scene = new Scene();
        var item = scene.AddEmpty();
        item.Rename("Click me");
        item.Attach(new Transform());
        item.Attach(new UiElement { Pivot = Vector2.Zero, SizeDelta = new Vector2(100, 40) });
        item.Attach(new Button());
        var counter = new ClickCounter();
        item.Attach(counter);

        using var runtime = new SceneRuntime(scene, registry);
        runtime.Start();
        var runtimeCounter = runtime.Scene.Objects.First(candidate => candidate.Name == "Click me").GetComponent<ClickCounter>()!;
        Check(runtimeCounter.Calls == 0, "Start must not flush clicks before the first Step.");
        runtime.EnqueueButtonClick(item.Id);
        Check(runtimeCounter.Calls == 0, "Enqueued clicks must wait for the update boundary, not run during editing.");
        runtime.Step(1f / 60f);
        Check(runtimeCounter.Calls == 1, "Step must dispatch one click once.");
        Check(runtimeCounter.LastId == item.Id, "Context must carry the Button SceneObject.");
        Check(ReferenceEquals(runtimeCounter.LastContext.Scene, runtime.Scene), "Context must carry the execution Scene.");
        Check(!ReferenceEquals(runtimeCounter.LastContext.ButtonObject, item), "Context must use the execution instance, not the authoring one.");
        runtime.Step(1f / 60f);
        Check(runtimeCounter.Calls == 1, "Steps without input must not repeat notifications.");
        Check(counter.Calls == 0, "Running must not mutate the authoring scene.");

        using var empty = new SceneRuntime(new Scene(), registry);
        empty.Start();
        empty.Step(1f / 60f);
        Check(empty.Errors.Count == 0, "Zero handlers must do nothing without errors.");

        var badScene = new Scene();
        var bad = badScene.AddEmpty();
        bad.Rename("Bad");
        bad.Attach(new Transform());
        bad.Attach(new UiElement());
        bad.Attach(new Button());
        bad.Attach(new ThrowingHandler());
        using var throwing = new SceneRuntime(badScene, registry);
        throwing.Start();
        throwing.EnqueueButtonClick(bad.Id);
        throwing.Step(1f / 60f);
        Check(!throwing.IsRunning && throwing.Errors.Count == 1 && throwing.Errors[0].MethodName == nameof(IUiButtonHandler.OnClick),
            "Handler exceptions must stop safely and report OnClick.");

        var removeScene = new Scene();
        var removeItem = removeScene.AddEmpty();
        removeItem.Rename("Remove");
        removeItem.Attach(new Transform());
        removeItem.Attach(new UiElement());
        removeItem.Attach(new Button());
        var remover = new SelfRemovingHandler();
        removeItem.Attach(remover);
        using var removing = new SceneRuntime(removeScene, registry);
        removing.Start();
        removing.EnqueueButtonClick(removeItem.Id);
        removing.Step(1f / 60f);
        Check(removing.IsRunning && removing.Errors.Count == 0, "Deletion during clicks must follow runtime removal rules without errors.");

        var stopScene = new Scene();
        var stopItem = stopScene.AddEmpty();
        stopItem.Rename("Stop");
        stopItem.Attach(new Transform());
        stopItem.Attach(new UiElement());
        stopItem.Attach(new Button());
        stopItem.Attach(new StoppingHandler());
        var other = stopScene.AddEmpty();
        other.Rename("Other");
        other.Attach(new Transform());
        other.Attach(new UiElement());
        other.Attach(new Button());
        var otherCounter = new ClickCounter();
        other.Attach(otherCounter);
        using var stopping = new SceneRuntime(stopScene, registry);
        stopping.Start();
        var stoppedOther = stopping.Scene.Objects.First(candidate => candidate.Name == "Other").GetComponent<ClickCounter>()!;
        StoppingHandler.RequestStop = stopping.Stop;
        try
        {
            stopping.EnqueueButtonClick(stopItem.Id);
            stopping.EnqueueButtonClick(other.Id);
            stopping.Step(1f / 60f);
        }
        finally
        {
            StoppingHandler.RequestStop = null;
        }
        Check(!stopping.IsRunning && stoppedOther.Calls == 0, "Stop during clicks must drop the remaining stale input.");

        var disabledScene = new Scene();
        var disabledItem = disabledScene.AddEmpty();
        disabledItem.Rename("Disabled");
        disabledItem.Attach(new Transform());
        disabledItem.Attach(new UiElement());
        disabledItem.Attach(new Button { Interactable = false });
        var disabledCounter = new ClickCounter();
        disabledItem.Attach(disabledCounter);
        using var disabled = new SceneRuntime(disabledScene, registry);
        disabled.Start();
        var disabledRuntime = disabled.Scene.Objects.First(candidate => candidate.Name == "Disabled").GetComponent<ClickCounter>()!;
        disabled.EnqueueButtonClick(disabledItem.Id);
        disabled.Step(1f / 60f);
        Check(disabledRuntime.Calls == 0 && disabled.IsRunning, "Interactable=false must be skipped without stopping.");

        using var beforeStart = new SceneRuntime(scene, registry);
        beforeStart.EnqueueButtonClick(item.Id);
        beforeStart.Start();
        beforeStart.Step(1f / 60f);
        var beforeCounter = beforeStart.Scene.Objects.First(candidate => candidate.Name == "Click me").GetComponent<ClickCounter>()!;
        Check(beforeCounter.Calls == 0, "Clicks queued before Start must never call editing or pre-Start handlers.");
    }
}
