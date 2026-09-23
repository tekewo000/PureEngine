using System.Numerics;
using PureEngine.Core;
using PureEngine.Core.Components;

static class UiButtonChecks
{
    public static void Run()
    {
        ButtonRoundTrip();
        Requirements();
        Visuals();
        HitButtonRules();
        RuntimeDispatch();
        Console.WriteLine("PASS: Button save/clone, requirements, subscriptions, visuals, overlap/parent/cancel/disable hit rules, and runtime click dispatch.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static ComponentRegistry ButtonRegistry()
    {
        var registry = new ComponentRegistry();
        registry.Register<Transform>("core.transform");
        registry.Register<UiElement>("core.ui-element");
        registry.Register<global::Image>("core.image");
        registry.Register<Button>("core.button");
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

    private static void RuntimeDispatch()
    {
        var registry = ButtonRegistry();
        var scene = new Scene();
        var source = scene.AddEmpty();
        var authoringButton = new Button();
        source.Attach(authoringButton);
        var authoringCalls = 0;
        authoringButton.Clicked += _ => authoringCalls++;

        using var runtime = new SceneRuntime(scene, registry);
        var item = runtime.Scene.Objects.Single();
        var button = item.GetComponent<Button>()!;
        var calls = 0;
        void Count(UiClickContext context)
        {
            Check(ReferenceEquals(context.Scene, runtime.Scene) && ReferenceEquals(context.ButtonObject, item),
                "Context must contain the execution Scene and Button object.");
            calls++;
        }
        button.Clicked += Count;
        runtime.EnqueueButtonClick(item.Id);
        runtime.Start();
        runtime.Step(0);
        Check(calls == 0, "Pre-Start input must be discarded.");
        runtime.EnqueueButtonClick(item.Id);
        Check(calls == 0, "Input must wait for Step.");
        runtime.Step(0);
        Check(calls == 1 && authoringCalls == 0, "Runtime must notify once without copying authoring subscriptions.");
        runtime.Step(0);
        Check(calls == 1, "Input must not repeat.");

        var otherCalls = 0;
        button.Clicked += _ => otherCalls++;
        runtime.EnqueueButtonClick(item.Id);
        runtime.Step(0);
        Check(calls == 2 && otherCalls == 1, "Multiple subscriptions must be supported.");
        button.Clicked -= Count;
        runtime.EnqueueButtonClick(item.Id);
        runtime.Step(0);
        Check(calls == 2 && otherCalls == 2, "Unsubscription must remove only that callback.");
        button.Interactable = false;
        runtime.EnqueueButtonClick(item.Id);
        runtime.Step(0);
        Check(otherCalls == 2, "Disabled buttons must not notify.");
        button.Interactable = true;

        var serializer = new SceneSerializer(registry);
        var saved = serializer.Serialize(runtime.Scene);
        Check(!saved.Contains("Clicked"), "Subscriptions must not be serialized.");
        foreach (var fresh in new[] { serializer.Clone(runtime.Scene), serializer.Deserialize(saved) })
        {
            ((IUiButtonHandler)fresh.Objects.Single().GetComponent<Button>()!).OnClick(new UiClickContext(fresh, fresh.Objects.Single()));
        }
        Check(otherCalls == 2 && calls == 2, "Save/load and Clone must not retain subscriptions.");

        using var replay = new SceneRuntime(scene, registry);
        replay.Start();
        replay.EnqueueButtonClick(source.Id);
        replay.Step(0);
        Check(replay.Errors.Count == 0 && authoringCalls == 0, "Replay without subscriptions must do nothing.");

        button.Clicked += _ => throw new ApplicationException("click boom");
        runtime.EnqueueButtonClick(item.Id);
        runtime.EnqueueButtonClick(item.Id);
        runtime.Step(0);
        Check(!runtime.IsRunning && runtime.Errors.Count == 1 && runtime.Errors[0].ComponentType == typeof(Button)
            && runtime.Errors[0].MethodName == nameof(IUiButtonHandler.OnClick) && otherCalls == 3,
            "Callback exceptions must report Button.OnClick, stop safely, and discard remaining input.");

        using var removing = new SceneRuntime(scene, registry);
        var removed = removing.Scene.Objects.Single();
        var removeCalls = 0;
        removed.GetComponent<Button>()!.Clicked += context =>
        {
            removeCalls++;
            context.Scene.Remove(context.ButtonObject);
        };
        removing.Start();
        removing.EnqueueButtonClick(removed.Id);
        removing.EnqueueButtonClick(removed.Id);
        removing.Step(0);
        Check(removeCalls == 1 && removing.IsRunning && removing.Scene.Objects.Count == 0,
            "Deletion during a click must discard remaining input for that object.");

        using var stopping = new SceneRuntime(scene, registry);
        var stopped = stopping.Scene.Objects.Single();
        var stopCalls = 0;
        stopped.GetComponent<Button>()!.Clicked += _ => { stopCalls++; stopping.Stop(); };
        stopping.Start();
        stopping.EnqueueButtonClick(stopped.Id);
        stopping.EnqueueButtonClick(stopped.Id);
        stopping.Step(0);
        stopping.EnqueueButtonClick(stopped.Id);
        Check(!stopping.IsRunning && stopCalls == 1,
            "Stop during a click must discard queued and subsequent input.");
    }
}
