using PureEngine.Core;
using YamlDotNet.RepresentationModel;

static class SceneComposableReferenceChecks
{
    public static void Run()
    {
        var registry = new ComponentRegistry();
        registry.Register<Holder>("composable.holder");
        registry.Register<Target>("composable.target");
        var serializer = new SceneSerializer(registry);
        var scene = new Scene();
        var owner = scene.AddEmpty();
        var targetObject = scene.AddEmpty();
        targetObject.SetParent(owner);
        var target = new Target();
        targetObject.Attach(target);
        var value = new ReferenceValue { Target = target, Object = targetObject, Number = 17 };
        var holder = new Holder
        {
            Rows = [new() { ["a.b[]"] = [value] }, new() { ["a.b[]"] = [value] }],
            Optional = new ReferenceParent { Value = value },
            Matrix = new ReferenceValue[1, 2],
            Jagged = [[target]],
            Embedded = new EmbeddedValue { Value = value },
        };
        holder.Matrix[0, 1] = value;
        owner.Attach(holder);
        var ownerId = owner.GetComponentId(holder);
        var targetId = targetObject.GetComponentId(target);
        Check(SceneReferenceTypes.IsSupportedInspectorType(typeof(Holder), registry), "Registered holders must remain references.");
        Check(SceneReferenceTypes.IsSupportedInspectorType(typeof(List<Dictionary<string, ReferenceValue?[]>>), registry),
            "Nested reference containers and nullable structs must be supported.");
        Check(SceneReferenceTypes.ContainsAssetExternalReference(typeof(ReferenceValue[,,]), registry),
            "Multidimensional values must not bypass asset external-reference restrictions.");
        Check(!SceneReferenceTypes.IsSupportedInspectorType(typeof(Dictionary<int, ReferenceValue>), registry),
            "Non-string dictionary keys must remain unsupported.");

        var yaml = serializer.Serialize(scene);
        Check(yaml.Contains("dimensions:") && yaml.Contains("items:"), "Scene arrays must use the shared shape format.");
        var loaded = serializer.Deserialize(yaml);
        AssertResolved(loaded);
        var clone = serializer.Clone(scene);
        AssertResolved(clone);
        Check(!ReferenceEquals(clone.Objects[0].GetComponent<Holder>()!.Rows, holder.Rows),
            "Nested containers must be copied, not shared.");
        CheckPlayAndPrefab(scene, registry);

        scene.Remove(targetObject);
        Check(holder.Rows[1]["a.b[]"][0]!.Value.Target is null && holder.Rows[1]["a.b[]"][0]!.Value.Number == 17,
            "Nulling must write back nullable structs in arrays nested inside dictionary/list rows.");
        Check(holder.Optional!.Value.Value.Target is null && holder.Optional.Value.Value.Number == 17,
            "Nulling must write back through nested nullable struct properties.");
        Check(holder.Matrix[0, 1].Target is null && holder.Matrix[0, 1].Number == 17,
            "Nulling must preserve multidimensional struct cells.");
        Check(holder.Embedded.Value.Target is null, "Existing embedded classes must still contain mutable struct values.");
        Check(holder.Jagged[0][0] is null, "Jagged array references must be nulled.");
        var rowPath = SceneReferenceStore.DictionaryPath("Rows[1]", "a.b[]") + "[0].Target";
        Check(scene.References.TryGetMissing(ownerId, rowPath, out var missing) && missing == targetId,
            "Nested Missing paths must preserve escaped dictionary keys.");
        Check(scene.References.TryGetMissing(ownerId, "Matrix[0,1].Target", out missing) && missing == targetId,
            "Multidimensional Missing paths must use coordinates.");

        holder.Rows.RemoveAt(0);
        scene.References.RemoveSequenceElement(ownerId, "Rows", 0);
        var firstPath = SceneReferenceStore.DictionaryPath("Rows[0]", "a.b[]") + "[0].Target";
        Check(scene.References.TryGetMissing(ownerId, firstPath, out missing) && missing == targetId
            && !scene.References.TryGetMissing(ownerId, rowPath, out _),
            "Removing a parent row must shift all descendant Missing paths.");
        holder.Rows[0]["renamed"] = holder.Rows[0]["a.b[]"];
        holder.Rows[0].Remove("a.b[]");
        scene.References.RenameDictionaryKey(ownerId, "Rows[0]", "a.b[]", "renamed");
        var renamedPath = "Rows[0][renamed][0].Target";
        Check(scene.References.TryGetMissing(ownerId, renamedPath, out missing) && missing == targetId
            && !scene.References.TryGetMissing(ownerId, firstPath, out _),
            "Renaming a parent key must retain nested Missing identities.");
        var missingYaml = serializer.Serialize(scene);
        var missingCopy = serializer.Clone(scene);
        Check(missingCopy.References.TryGetMissing(ownerId, renamedPath, out missing) && missing == targetId,
            "Cloning must retain nested Missing identities.");
        var reopened = serializer.Deserialize(missingYaml);
        Check(serializer.Serialize(reopened) == missingYaml, "Nested Missing save/load must be stable.");

        holder.Matrix = new ReferenceValue[3, 0];
        scene.References.RemovePathsForMember(ownerId, "Matrix");
        var empty = serializer.Deserialize(serializer.Serialize(scene));
        var emptyMatrix = empty.Objects[0].GetComponent<Holder>()!.Matrix;
        Check(emptyMatrix.GetLength(0) == 3 && emptyMatrix.GetLength(1) == 0, "Empty arrays must retain every dimension.");
        RejectNullStruct(serializer);
        CheckPathBoundaries();
        CheckRankMigration();
        Console.WriteLine("PASS: composable scene references, struct writeback, rectangular arrays, Missing paths, prefab copies, Play, and rank migration.");
    }

    private static void CheckRankMigration()
    {
        var before = new ComponentRegistry();
        before.Register<RankTwoHolder>("composable.rank");
        var after = new ComponentRegistry();
        after.Register<RankThreeHolder>("composable.rank");
        var scene = new Scene();
        scene.AddEmpty().Attach(new RankTwoHolder());
        try { _ = SceneCodeMigrator.Migrate(scene, before, after); }
        catch (InvalidDataException error) when (error.Message.Contains("type change", StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("Migration must reject array rank changes before attempting restore.");
    }

    private static void CheckPlayAndPrefab(Scene source, ComponentRegistry registry)
    {
        using (var runtime = new SceneRuntime(source, registry))
        {
            runtime.Start();
            AssertResolved(runtime.Scene);
            var live = runtime.Scene.Objects[0].GetComponent<Holder>()!;
            live.Rows[0].Clear();
            Check(source.Objects[0].GetComponent<Holder>()!.Rows[0].Count == 1,
                "Play mutations must not leak into nested authoring containers.");
            runtime.Stop();
        }
        var serializer = new PrefabSerializer(registry);
        var yaml = serializer.Serialize(source, source.Objects[0], Guid.NewGuid());
        var document = PrefabSerializer.Parse(yaml);
        var placedScene = new Scene();
        var placed = serializer.Instantiate(placedScene, document);
        AssertResolved(placedScene);
        Check(placed.Id != source.Objects[0].Id
            && placed.Children[0].GetComponentId(placed.Children[0].GetComponent<Target>()!)
                != source.Objects[1].GetComponentId(source.Objects[1].GetComponent<Target>()!),
            "Prefab placement must reconnect nested references to fresh component identities.");
        placedScene.Remove(placed.Children[0]);
        var missing = placedScene.References.MissingCount;
        var missingPrefab = serializer.Capture(placedScene, placed);
        missingPrefab.Id = Guid.NewGuid();
        var missingScene = new Scene();
        var missingRoot = serializer.Instantiate(missingScene, missingPrefab);
        Check(missingScene.References.MissingCount == missing
            && missingRoot.GetComponent<Holder>()!.Matrix[0, 1].Target is null,
            "Prefab capture and placement must preserve nested Missing slots.");
    }

    private static void AssertResolved(Scene scene)
    {
        var holder = scene.Objects[0].GetComponent<Holder>()!;
        var target = scene.Objects[1].GetComponent<Target>()!;
        Check(ReferenceEquals(holder.Rows[1]["a.b[]"][0]!.Value.Target, target)
            && ReferenceEquals(holder.Optional!.Value.Value.Target, target)
            && ReferenceEquals(holder.Matrix[0, 1].Target, target)
            && ReferenceEquals(holder.Jagged[0][0], target)
            && ReferenceEquals(holder.Embedded.Value.Object, scene.Objects[1]),
            "Recursive reference dispatch must preserve component and object identities.");
    }

    private static void RejectNullStruct(SceneSerializer serializer)
    {
        var scene = new Scene();
        scene.AddEmpty().Attach(new Holder());
        var yaml = serializer.Serialize(scene);
        var stream = new YamlStream();
        using var reader = new StringReader(yaml);
        stream.Load(reader);
        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        var objects = (YamlSequenceNode)root.Children["objects"];
        var components = (YamlSequenceNode)((YamlMappingNode)objects.Children[0]).Children["components"];
        var values = (YamlMappingNode)((YamlMappingNode)components.Children[0]).Children["values"];
        values.Children["Required"] = new YamlScalarNode("null");
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        try { _ = serializer.Deserialize(writer.ToString()); }
        catch (InvalidDataException error) when (error.Message.Contains("null is not allowed", StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("Null reference-bearing structs must not silently become default values.");
    }

    private static void CheckPathBoundaries()
    {
        var store = new SceneReferenceStore();
        var owner = Guid.NewGuid();
        var target = Guid.NewGuid();
        store.SetMissing(owner, "Rows[10].Nested[0].Target", target);
        store.SetMissing(owner, "RowsExtra[1].Target", target);
        store.SetLegacy(owner, "Rows[10].Nested[0].Target", new object());
        store.RemoveSequenceElement(owner, "Rows", 1);
        Check(store.TryGetMissing(owner, "Rows[9].Nested[0].Target", out _)
            && store.TryGetLegacy(owner, "Rows[9].Nested[0].Target", out _)
            && store.TryGetMissing(owner, "RowsExtra[1].Target", out _),
            "Row shifts must handle multi-digit indices and preserve unrelated paths.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public sealed class Target;

    public struct ReferenceValue
    {
        [Inspector] public Target? Target { get; set; }
        [Inspector] public SceneObject? Object { get; set; }
        [Inspector] public int Number { get; set; }
    }

    public struct ReferenceParent
    {
        [Inspector] public ReferenceValue Value { get; set; }
    }

    public sealed class EmbeddedValue
    {
        [Inspector] public ReferenceValue Value { get; set; }
    }

    public sealed class Holder
    {
        [Inspector] public List<Dictionary<string, ReferenceValue?[]>> Rows { get; set; } = [];
        [Inspector] public ReferenceParent? Optional { get; set; }
        [Inspector] public ReferenceValue Required { get; set; }
        [Inspector] public ReferenceValue[,] Matrix { get; set; } = new ReferenceValue[0, 2];
        [Inspector] public Target?[][] Jagged { get; set; } = [];
        [Inspector] public EmbeddedValue Embedded { get; set; } = new();
    }

    public sealed class RankTwoHolder
    {
        [Inspector] public SceneObject?[,] Cells { get; set; } = new SceneObject?[0, 2];
    }

    public sealed class RankThreeHolder
    {
        [Inspector] public SceneObject?[,,] Cells { get; set; } = new SceneObject?[0, 2, 3];
    }
}
