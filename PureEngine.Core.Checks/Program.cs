using PureEngine.Core;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var scene = new Scene();
var first = scene.AddEmpty();
var second = scene.AddEmpty();
Check(first.Name == "Empty" && second.Name == "Empty (1)", "New names must not collide.");
Check(first.Id != second.Id, "Objects need stable, distinct identities.");
var originalId = first.Id;
var changes = 0;
first.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SceneObject.Name)) changes++; };
first.Rename("  Player  ");
first.Rename("Player");
Check(first.Name == "Player" && first.Id == originalId && changes == 1, "Rename must notify once without changing identity.");
try
{
    first.Rename("   ");
    throw new InvalidOperationException("Empty names must be rejected.");
}
catch (ArgumentException) { }
Check(first.Name == "Player", "Rejected edits must preserve the name.");
second.Rename("Player");
Check(scene.Remove(first) && scene.Objects.Count == 1 && scene.Objects[0] == second,
    "Deletion must use identity even when names match.");
Check(!scene.Remove(first), "Removing an absent object must not change the scene.");
Check(scene.AddEmpty().Name == "Empty", "Available default names should be reused.");
foreach (var item in scene.Objects.ToArray()) scene.Remove(item);
Check(scene.Objects.Count == 0, "The final object can be removed.");

// Attach / GetComponent: plain C# instances, one per exact type.
var probe = new SceneObject("Probe");
var player = new PlayerController { Hp = 10 };
probe.Attach(player);
Check(probe.GetComponent<PlayerController>() == player, "GetComponent must return the attached instance.");
Check(probe.GetComponent<EnemyController>() is null, "GetComponent must return null when absent.");
Check(probe.Components.Count == 1 && ReferenceEquals(probe.Components[0], player),
    "Components must expose attached instances in attach order.");
try
{
    probe.Attach(new PlayerController());
    throw new InvalidOperationException("Attaching the same exact type twice must be rejected.");
}
catch (InvalidOperationException) { }
Check(probe.Components.Count == 1, "Rejected attach must not change components.");
try
{
    probe.Attach(null!);
    throw new InvalidOperationException("Null attach must be rejected.");
}
catch (ArgumentNullException) { }

// Inheritance: Derived is retrievable as Base; Base and Derived are different exact types.
var probe2 = new SceneObject("Probe2");
var derived = new DerivedComponent();
probe2.Attach(derived);
Check(probe2.GetComponent<DerivedComponent>() == derived, "GetComponent must find the exact type.");
Check(probe2.GetComponent<BaseComponent>() == derived, "GetComponent must find derived instances via their base type.");
probe2.Attach(new BaseComponent());
Check(probe2.Components.Count == 2, "Base and Derived are different exact types and may coexist.");

// Schema: detect Inspector targets and lifecycle methods from a sample.
var sample = new SampleBehaviour();
var holder = new SceneObject("Sample");
holder.Attach(sample);
Check(holder.GetComponent<SampleBehaviour>() == sample, "GetComponent must return the sample instance.");

var inspectorNames = ComponentSchema.GetInspectorMembers(typeof(SampleBehaviour)).Select(m => m.Name).ToArray();
Check(inspectorNames.Length == 2 && inspectorNames.Contains("Hp") && inspectorNames.Contains("Title"),
    "Inspector must detect only public readable/writable members with the attribute.");

Check(ComponentSchema.GetStartMethod(typeof(SampleBehaviour)) is not null, "Start must be detected.");
var update = ComponentSchema.GetUpdateMethod(typeof(SampleBehaviour));
Check(update is not null, "Update must be detected.");
Check(update!.GetParameters() is { Length: 1 } ps && ps[0].ParameterType == typeof(float),
    "Update must take a single float parameter.");
Check(ComponentSchema.GetDestroyMethod(typeof(SampleBehaviour)) is null, "Destroy must be absent when not defined.");
try
{
    ComponentSchema.GetUpdateMethod(typeof(DuplicatedUpdate));
    throw new InvalidOperationException("Multiple Update methods must be rejected.");
}
catch (InvalidOperationException) { }

// Both editor drop surfaces use this catalog and attachment policy.
var dropTarget = new SceneObject("Drop target");
var otherTarget = new SceneObject("Other target");
var assetType = typeof(PureEngine.Editor.Samples.PlayerStats);
Check(!PureEngine.Editor.ComponentAssets.TryAttach(null, assetType), "A drop without an object must be rejected.");
Check(!PureEngine.Editor.ComponentAssets.TryAttach(dropTarget, null), "An unrelated drag must be rejected.");
Check(!PureEngine.Editor.ComponentAssets.TryAttach(dropTarget, typeof(PlayerController)), "Unlisted classes must be rejected.");
Check(PureEngine.Editor.ComponentAssets.TryAttach(dropTarget, assetType), "An Assets class must attach to its drop target.");
Check(!PureEngine.Editor.ComponentAssets.TryAttach(dropTarget, assetType) && dropTarget.Components.Count == 1,
    "Repeated drops must not duplicate a component.");
Check(PureEngine.Editor.ComponentAssets.TryAttach(otherTarget, assetType)
    && !ReferenceEquals(dropTarget.Components[0], otherTarget.Components[0]),
    "Each object must receive its own component instance.");
Check(PureEngine.Editor.ComponentAssets.TryAttach(dropTarget, typeof(PureEngine.Editor.Samples.RoundSettings))
    && dropTarget.Components.Count == 2, "Different classes must coexist on the drop target.");

Console.WriteLine("PASS: add, rename, validation, identity, notifications, removal, attach, get-component, schema, and asset drop policy.");

sealed class PlayerController
{
    public int Hp { get; set; }
}

sealed class EnemyController
{
}

class BaseComponent
{
}

sealed class DerivedComponent : BaseComponent
{
}

sealed class SampleBehaviour
{
    [Inspector] public int Hp { get; set; }
    [Inspector] public string Title = "";
#pragma warning disable CS0649 // Never assigned: intentional negative case for detection.
    public int Hidden;
#pragma warning restore CS0649
#pragma warning disable CS0169 // Never used: intentional negative case for detection.
    [Inspector] private int Secret;
#pragma warning restore CS0169
    [Inspector] public int ReadOnlyProp { get; }

    [Start] private void OnStart() { }

    [Update] private void Tick(float dt) { }
}

sealed class DuplicatedUpdate
{
    [Update] public void A() { }
    [Update] public void B() { }
}
