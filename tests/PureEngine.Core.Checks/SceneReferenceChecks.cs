using PureEngine.Core;
using YamlDotNet.Core;

static class SceneReferenceChecks
{
    public static void Run()
    {
        static void Check(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
        static void Reject(Action action, string message)
        {
            try { action(); }
            catch (Exception error) when (error is InvalidDataException or YamlException or ArgumentException or InvalidOperationException)
            { return; }
            throw new Exception(message);
        }

        var registry = new ComponentRegistry();
        registry.Register<RefHolder>("checks.holder");
        registry.Register<RefTarget>("checks.target");
        registry.Register<Transform>("core.transform");
        registry.Register<Button>("core.button");
        var serializer = new SceneSerializer(registry);

        var scene = new Scene();
        var holderObject = scene.AddEmpty();
        holderObject.Rename("Holder");
        var targetObject = scene.AddEmpty();
        targetObject.Rename("Target");
        var target = new RefTarget { Value = 7 };
        targetObject.Attach(target);
        var button = new Button { Interactable = false };
        targetObject.Attach(button);
        var holder = new RefHolder();
        holderObject.Attach(holder);
        var holderId = holderObject.GetComponentId(holder);
        var targetId = targetObject.GetComponentId(target);
        var buttonId = targetObject.GetComponentId(button);
        Check(holderId != Guid.Empty && targetId != Guid.Empty && buttonId != Guid.Empty, "Components need engine IDs.");
        Check(holderId != targetId && targetId != buttonId && holderId != buttonId, "Component IDs must be distinct.");
        Check(targetObject.Id != targetId && holderObject.Id != holderId, "Object and component IDs must differ.");

        holder.Single = target;
        holder.Button = button;
        holder.Owner = targetObject;
        var yaml = serializer.Serialize(scene);
        Check(yaml.Contains("version: 3") && yaml.Contains("ref:"), "Version 3 must save references as { ref: }.");
        var restored = serializer.Deserialize(yaml);
        var restoredHolder = restored.Objects[0].GetComponent<RefHolder>()!;
        var restoredTarget = restored.Objects[1].GetComponent<RefTarget>()!;
        var restoredButton = restored.Objects[1].GetComponent<Button>()!;
        Check(ReferenceEquals(restoredHolder.Single, restoredTarget), "Single component reference did not survive.");
        Check(ReferenceEquals(restoredHolder.Button, restoredButton), "Button reference did not survive.");
        Check(ReferenceEquals(restoredHolder.Owner, restored.Objects[1]), "SceneObject reference did not survive.");
        Check(restored.Objects[0].GetComponentId(restoredHolder) == holderId, "Component IDs must survive save/load.");
        Check(serializer.Serialize(restored) == yaml, "Reference save/load changed output.");

        holder.Tags = [target, null];
        holder.Map = new Dictionary<string, RefTarget?>(StringComparer.Ordinal) { ["first"] = target, ["empty"] = null };
        holder.Self = holder;
        var peerObject = scene.AddEmpty();
        peerObject.Rename("Peer");
        var peer = new RefHolder { Single = target, Peer = holder };
        peerObject.Attach(peer);
        holder.Peer = peer;
        var forwardYaml = serializer.Serialize(scene);
        var forwardRestored = serializer.Deserialize(forwardYaml);
        var forwardHolder = forwardRestored.Objects[0].GetComponent<RefHolder>()!;
        Check(forwardHolder.Tags.Length == 2 && ReferenceEquals(forwardHolder.Tags[0], forwardRestored.Objects[1].GetComponent<RefTarget>()!)
            && forwardHolder.Tags[1] is null, "Array reference round-trip failed.");
        Check(forwardHolder.Map["first"] is not null && forwardHolder.Map["empty"] is null, "Dictionary reference round-trip failed.");
        Check(ReferenceEquals(forwardHolder.Self, forwardHolder), "Self reference did not survive.");
        Check(ReferenceEquals(forwardHolder.Peer, forwardRestored.Objects[2].GetComponent<RefHolder>()!)
            && ReferenceEquals(forwardRestored.Objects[2].GetComponent<RefHolder>()!.Peer, forwardHolder), "Mutual reference did not survive.");

        var customScene = new Scene();
        var customHolderObject = customScene.AddEmpty();
        customHolderObject.Rename("CustomHolder");
        var customTargetObject = customScene.AddEmpty();
        customTargetObject.Rename("CustomTarget");
        var customTarget = new RefTarget { Value = 3 };
        customTargetObject.Attach(customTarget);
        var customHolder = new RefHolder
        {
            Config = new RefConfig { Primary = customTarget, Owner = customTargetObject, Extras = [customTarget] },
        };
        customHolderObject.Attach(customHolder);
        var customYaml = serializer.Serialize(customScene);
        var customRestored = serializer.Deserialize(customYaml);
        var customCopy = customRestored.Objects[0].GetComponent<RefHolder>()!;
        Check(ReferenceEquals(customCopy.Config!.Primary, customRestored.Objects[1].GetComponent<RefTarget>()!), "Nested reference did not survive.");
        Check(ReferenceEquals(customCopy.Config.Owner, customRestored.Objects[1]), "Nested SceneObject reference did not survive.");
        Check(customCopy.Config.Extras.Count == 1 && ReferenceEquals(customCopy.Config.Extras[0], customRestored.Objects[1].GetComponent<RefTarget>()!),
            "Nested list reference did not survive.");

        Reject(() => serializer.Deserialize(yaml.Replace(buttonId.ToString("D"), targetId.ToString("D"))),
            "Type-mismatched reference was accepted.");
        var otherScene = new Scene();
        var foreignObject = otherScene.AddEmpty();
        var foreignTarget = new RefTarget();
        foreignObject.Attach(foreignTarget);
        holder.Single = foreignTarget;
        Reject(() => serializer.Serialize(scene), "Cross-scene reference was saved.");
        holder.Single = target;
        var detached = new RefTarget();
        var detachedHolder = new RefHolder { Single = detached };
        var detachedObject = new Scene();
        var detachedItem = detachedObject.AddEmpty();
        detachedItem.Attach(detachedHolder);
        var detachedSerializer = new SceneSerializer(registry);
        Reject(() => detachedSerializer.Serialize(detachedObject), "Detached Component must not silently become an embedded value.");
        Reject(() => serializer.Deserialize(yaml.Replace($"ref: {targetId:D}", "ref: invalid")), "Malformed reference was accepted.");
        Reject(() => serializer.Deserialize(yaml.Replace($"ref: {targetObject.Id:D}", $"ref: {targetId:D}")), "Component ID in SceneObject slot was accepted.");
        Reject(() => serializer.Deserialize(yaml.Replace($"ref: {targetId:D}", $"ref: {targetObject.Id:D}")), "SceneObject ID in Component slot was accepted.");
        var inlineYaml = yaml.Replace($"ref: {buttonId:D}", "Interactable: true");
        Reject(() => serializer.Deserialize(inlineYaml), "Version 3 inline Component value was accepted.");
        var legacyInline = serializer.Deserialize(inlineYaml.Replace("version: 3", "version: 2"), out var migrated);
        Check(migrated && legacyInline.References.HasLegacy && legacyInline.Objects[0].GetComponent<RefHolder>()!.Button is null,
            "Old inline Component values must remain unresolved and preserved.");
        Reject(() => serializer.Serialize(legacyInline), "Old inline Component data was silently discarded.");
        Reject(() => serializer.Clone(legacyInline), "Clone must not silently discard unresolved legacy data.");
        Check(!SceneReferenceTypes.IsSupportedInspectorType(typeof(InvalidConfig), registry), "A reference must not mask an unsupported sibling value.");
        Check(!SceneReferenceTypes.IsSupportedInspectorType(typeof(RecursiveConfig), registry), "Embedded recursion must be rejected even when a sibling is a reference.");
        Check(!SceneReferenceTypes.IsSupportedInspectorType(typeof(List<List<RefTarget>>), registry), "Nested containers must remain unsupported.");

        scene.Remove(targetObject);
        Check(holder.Single is null && holder.Button is null && holder.Owner is null, "Deletion must null live references.");
        Check(scene.References.TryGetMissing(holderId, "Single", out var missingSingle) && missingSingle == targetId, "Deletion must keep Single ID.");
        Check(scene.References.TryGetMissing(holderId, "Button", out var missingButton) && missingButton == buttonId, "Deletion must keep Button ID.");
        Check(scene.References.TryGetMissing(holderId, "Owner", out var missingOwner) && missingOwner == targetObject.Id, "Deletion must keep SceneObject ID.");
        var missingSaved = serializer.Serialize(scene);
        Check(missingSaved.Contains(targetId.ToString("D")) && missingSaved.Contains(buttonId.ToString("D")), "Missing IDs must survive saving.");
        var missingRestored = serializer.Deserialize(missingSaved);
        var missingHolder = missingRestored.Objects[0].GetComponent<RefHolder>()!;
        Check(missingHolder.Single is null && missingHolder.Button is null && missingHolder.Owner is null, "Missing must load as null.");
        Check(missingHolder.Tags[0] is null && missingHolder.Map["first"] is null, "Container Missing must load as null.");
        var missingPeer = missingRestored.Objects.First(item => item.Name == "Peer").GetComponent<RefHolder>()!;
        Check(missingPeer.Single is null, "Peer Missing must load as null.");
        Check(missingRestored.References.MissingCount == 6, "Missing IDs must survive reopening.");
        var replacement = new RefTarget();
        missingRestored.AddNamed("Replacement").Attach(replacement);
        missingHolder.Single = replacement;
        _ = serializer.Serialize(missingRestored);
        missingHolder.Single = null;
        _ = serializer.Serialize(missingRestored);
        Check(!missingRestored.References.TryGetMissing(holderId, "Single", out _), "A reassignment must retire the old Missing ID.");
        var reloadedOriginal = serializer.Deserialize(yaml);
        Check(ReferenceEquals(reloadedOriginal.Objects[0].GetComponent<RefHolder>()!.Single,
            reloadedOriginal.Objects[1].GetComponent<RefTarget>()!), "Same-ID reload must reconnect.");

        var clone = serializer.Clone(scene);
        Check(clone.Objects.Count == scene.Objects.Count, "Clone must preserve object count.");
        foreach (var item in scene.Objects)
        {
            var clonedItem = clone.Objects.First(candidate => candidate.Id == item.Id);
            foreach (var component in item.Components)
            {
                var clonedComponent = clonedItem.Components.First(candidate => candidate.GetType() == component.GetType());
                Check(item.GetComponentId(component) == clonedItem.GetComponentId(clonedComponent), "Clone must preserve component IDs.");
                Check(!ReferenceEquals(component, clonedComponent), "Clone must separate instances.");
            }
        }
        var cloneHolder = clone.Objects.First(item => item.Name == "Holder").GetComponent<RefHolder>()!;
        Check(cloneHolder.Single is null, "Clone must resolve Missing to null in the clone scene.");
        var liveScene = new Scene();
        var liveHolderObject = liveScene.AddEmpty();
        liveHolderObject.Rename("LiveHolder");
        var liveTargetObject = liveScene.AddEmpty();
        liveTargetObject.Rename("LiveTarget");
        var liveTarget = new RefTarget();
        liveTargetObject.Attach(liveTarget);
        var liveHolder = new RefHolder { Single = liveTarget };
        liveHolderObject.Attach(liveHolder);
        var liveClone = serializer.Clone(liveScene);
        var liveCloneHolder = liveClone.Objects[0].GetComponent<RefHolder>()!;
        var liveCloneTarget = liveClone.Objects[1].GetComponent<RefTarget>()!;
        Check(ReferenceEquals(liveCloneHolder.Single, liveCloneTarget) && !ReferenceEquals(liveCloneTarget, liveTarget),
            "Clone must resolve references to the clone target.");

        using var runtime = new SceneRuntime(liveScene, registry);
        runtime.Start();
        Check(runtime.Scene.Objects[0].GetComponent<RefHolder>()!.Single is not null, "Runtime must connect before Start.");
        runtime.Stop();
        var runtimeButtonScene = new Scene();
        var buttonHolderObject = runtimeButtonScene.AddEmpty();
        buttonHolderObject.Rename("ButtonHolder");
        var buttonTargetObject = runtimeButtonScene.AddEmpty();
        buttonTargetObject.Rename("ButtonTarget");
        var runtimeButton = new Button();
        buttonTargetObject.Attach(runtimeButton);
        var buttonHolder = new RefHolder { Button = runtimeButton };
        buttonHolderObject.Attach(buttonHolder);
        using var buttonRuntime = new SceneRuntime(runtimeButtonScene, registry);
        var clicks = 0;
        UiClickContext received = default;
        var receivedClick = false;
        buttonRuntime.Scene.Objects[1].GetComponent<Button>()!.Clicked += context => { clicks++; received = context; receivedClick = true; };
        buttonRuntime.Start();
        var liveButton = buttonRuntime.Scene.Objects[1].GetComponent<Button>()!;
        Check(ReferenceEquals(buttonRuntime.Scene.Objects[0].GetComponent<RefHolder>()!.Button, liveButton),
            "Runtime Button reference must be the live instance.");
        buttonRuntime.EnqueueButtonClick(buttonTargetObject.Id);
        buttonRuntime.Step(0.016f);
        Check(clicks == 1 && receivedClick && ReferenceEquals(received.ButtonObject, buttonRuntime.Scene.Objects[1]),
            "Click subscription must reach the live Button.");
        liveButton.Interactable = false;
        Check(buttonRuntime.Scene.Objects[0].GetComponent<RefHolder>()!.Button!.Interactable == false,
            "Interactable change must reach the live Button.");
        var editButton = runtimeButtonScene.Objects[1].GetComponent<Button>()!;
        Check(!ReferenceEquals(editButton, liveButton), "Play must separate edit and live Button instances.");
        buttonRuntime.Stop();

        var legacyScene = new Scene();
        var legacyObject = legacyScene.AddEmpty();
        legacyObject.Rename("Legacy");
        var legacyHolder = new RefHolder();
        legacyObject.Attach(legacyHolder);
        var legacyHolderId = legacyObject.GetComponentId(legacyHolder);
        legacyScene.References.SetLegacy(legacyHolderId, "Single", new Dictionary<string, object?> { ["obsolete"] = 1 });
        var legacySerializer = new SceneSerializer(registry);
        Reject(() => legacySerializer.Serialize(legacyScene), "Destructive save with legacy must be refused.");
        var legacyChanged = legacyScene.References.HasLegacy;
        Check(legacyChanged, "Legacy must be reported.");
        legacyScene.References.ClearLegacy(legacyHolderId, "Single");
        var clearedYaml = legacySerializer.Serialize(legacyScene);
        Check(!clearedYaml.Contains("obsolete"), "Explicit discard must allow saving.");

        registry.Register<ReadProbe>("checks.read-probe");
        var stoppingScene = new Scene();
        var stoppingTarget = new RefTarget();
        stoppingScene.AddNamed("Target").Attach(stoppingTarget);
        stoppingScene.AddNamed("Probe").Attach(new ReadProbe { Target = stoppingTarget });
        using var stoppingRuntime = new SceneRuntime(stoppingScene, registry);
        stoppingRuntime.Start();
        var probe = stoppingRuntime.Scene.Objects[1].GetComponent<ReadProbe>()!;
        probe.Reads = 0;
        stoppingRuntime.Stop();
        Check(probe.Reads == 0, "Stop must not re-read Inspector values on disposed components.");

        var paths = new SceneReferenceStore();
        var firstPath = SceneReferenceStore.DictionaryPath("Map", "first");
        var secondPath = SceneReferenceStore.DictionaryPath("Map", "first].Nested");
        paths.SetMissing(holderId, firstPath, targetId);
        paths.SetMissing(holderId, secondPath, buttonId);
        paths.RemovePathsForMember(holderId, firstPath);
        Check(paths.TryGetMissing(holderId, secondPath, out var retained) && retained == buttonId,
            "Dictionary keys containing path punctuation must not collide with nested slot paths.");

        Console.WriteLine("PASS: SceneObject/Component ID references, validation, Missing, Clone separation, and Play connection.");
    }

    public sealed class RefTarget
    {
        [Inspector] public int Value { get; set; }
    }

    public sealed class ReadProbe
    {
        private RefTarget? _target;
        public int Reads { get; set; }
        [Inspector] public RefTarget? Target { get { Reads++; return _target; } set => _target = value; }
    }

    public sealed class InvalidConfig
    {
        [Inspector] public RefTarget? Target { get; set; }
        [Inspector] public DateTime Unsupported { get; set; }
    }

    public sealed class RecursiveConfig
    {
        [Inspector] public RefTarget? Target { get; set; }
        [Inspector] public RecursiveConfig? Next { get; set; }
    }

    public sealed class RefConfig
    {
        [Inspector] public RefTarget? Primary { get; set; }
        [Inspector] public SceneObject? Owner { get; set; }
        [Inspector] public List<RefTarget?> Extras { get; set; } = [];
    }

    public sealed class RefHolder
    {
        [Inspector] public RefTarget? Single { get; set; }
        [Inspector] public Button? Button { get; set; }
        [Inspector] public SceneObject? Owner { get; set; }
        [Inspector] public RefTarget?[] Tags { get; set; } = [];
        [Inspector] public Dictionary<string, RefTarget?> Map { get; set; } = [];
        [Inspector] public RefHolder? Self { get; set; }
        [Inspector] public RefHolder? Peer { get; set; }
        [Inspector] public RefConfig? Config { get; set; }
    }
}
