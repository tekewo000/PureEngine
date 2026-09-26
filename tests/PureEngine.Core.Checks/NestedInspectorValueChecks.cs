using PureEngine.Core;
using YamlDotNet.Serialization;

static class NestedInspectorValueChecks
{
    public static void Run()
    {
        Dictionary<string, List<ValueProbe?[,]>> source = new()
        {
            ["values"] = [new ValueProbe?[,] { { new() { Numbers = [3, 5], Name = "value" }, null } }],
        };
        var type = source.GetType();
        Check(InspectorValueTypes.IsSupportedType(type), "Nested containers and nullable structs must be supported.");
        var stored = InspectorValueTypes.ToStorable(source, type);
        var yaml = new SerializerBuilder().Build().Serialize(stored);
        var raw = new DeserializerBuilder().Build().Deserialize<object>(yaml);
        var restored = (Dictionary<string, List<ValueProbe?[,]>>)InspectorValueTypes.FromStorable(raw, type, "Values")!;
        var cloned = (Dictionary<string, List<ValueProbe?[,]>>)InspectorValueTypes.FromStorable(source, type, "Values")!;
        source["values"][0][0, 0]!.Value.Numbers.Add(8);
        Check(restored["values"][0][0, 0]!.Value.Numbers.SequenceEqual([3, 5])
            && cloned["values"][0][0, 0]!.Value.Numbers.SequenceEqual([3, 5]),
            "YAML restore and typed clone must deep-copy struct-owned collections.");
        Check(restored["values"][0][0, 1] is null && restored["values"][0][0, 0]!.Value.Name == "value",
            "Nullable structs and writable struct properties must round-trip.");

        int[][] jagged = [[1, 2], [], [3]];
        var jaggedCopy = (int[][])InspectorValueTypes.FromStorable(
            InspectorValueTypes.ToStorable(jagged, typeof(int[][])), typeof(int[][]), "Jagged")!;
        Check(jaggedCopy[0].SequenceEqual([1, 2]) && jaggedCopy[1].Length == 0,
            "Jagged arrays must retain the legacy nested sequence representation.");
        Check(InspectorValueTypes.ToStorable(jagged[0], typeof(int[])) is List<object?>,
            "One-dimensional arrays must retain the legacy sequence representation.");

        int[][] shapes = [[2, 0, 3], [0, 4, 2], [2, 3, 0], [2, 1, 3, 2]];
        foreach (var lengths in shapes)
        {
            var array = Array.CreateInstance(typeof(int), lengths);
            var arrayType = array.GetType();
            var arrayYaml = new SerializerBuilder().Build().Serialize(InspectorValueTypes.ToStorable(array, arrayType));
            var arrayRaw = new DeserializerBuilder().Build().Deserialize<object>(arrayYaml);
            var copy = (Array)InspectorValueTypes.FromStorable(arrayRaw, arrayType, "Grid")!;
            Check(Enumerable.Range(0, lengths.Length).All(index => copy.GetLength(index) == lengths[index]),
                "Every dimension, including dimensions after a zero, must survive YAML.");
        }
        var paths = new List<string>();
        InspectorArrayShape.Capture(new int[2, 2], "Grid", (value, path) => { paths.Add(path); return value; });
        Check(paths.SequenceEqual(["Grid[0,0]", "Grid[0,1]", "Grid[1,0]", "Grid[1,1]"]),
            "Array callbacks must use row-major coordinate paths.");

        Reject(() => InspectorValueTypes.FromStorable(null, typeof(ValueProbe), "Value"));
        Check(InspectorValueTypes.FromStorable(null, typeof(ValueProbe?), "Value") is null,
            "Nullable struct nulls must remain valid.");
        Reject(() => InspectorValueTypes.FromStorable(Shape([-1, 2], []), typeof(int[,]), "Grid"));
        Reject(() => InspectorValueTypes.FromStorable(Shape([2], []), typeof(int[,]), "Grid"));
        Reject(() => InspectorValueTypes.FromStorable(Shape([2, 2], [1]), typeof(int[,]), "Grid"));
        Reject(() => InspectorValueTypes.FromStorable(Shape([int.MaxValue, int.MaxValue], []), typeof(int[,]), "Grid"));
        Reject(() => InspectorValueTypes.FromStorable(Shape([0, int.MaxValue], []), typeof(int[,]), "Grid"));
        Reject(() => InspectorValueTypes.FromStorable(new List<object?> { 1, 2 }, typeof(int[,]), "Grid"));
        Reject(() => InspectorValueTypes.ToStorable(Array.CreateInstance(typeof(int), [2, 2], [1, 0]), typeof(int[,])));

        var cyclic = new RecursiveProbe();
        cyclic.Children.Add(cyclic);
        Reject(() => InspectorValueTypes.ToStorable(cyclic, typeof(RecursiveProbe)));
        Reject(() => InspectorValueTypes.FromStorable(cyclic, typeof(RecursiveProbe), "Cycle"));
        var cyclicRaw = new List<object?>();
        cyclicRaw.Add(cyclicRaw);
        Reject(() => InspectorValueTypes.FromStorable(cyclicRaw, typeof(int[][]), "Cycle"));
        Check(!InspectorValueTypes.IsSupportedType(typeof(Dictionary<int, int>))
            && !InspectorValueTypes.IsSupportedType(typeof(DateTime)),
            "Unsupported dictionary keys and framework structs must remain unsupported.");
        CheckDataAssets();
    }

    private static void CheckDataAssets()
    {
        var registry = new ComponentRegistry();
        registry.Register<ValueAsset>("checks.nested-value-asset");
        registry.Register<ExternalAsset>("checks.nested-external-asset");
        registry.Register<ExternalSceneAsset>("checks.nested-scene-asset");
        var serializer = new DataAssetSerializer(registry);
        var source = new ValueAsset
        {
            Values = new() { ["grid"] = [new ValueProbe?[,] { { new() { Name = "asset", Numbers = [7, 9] }, null } }] },
        };
        var id = Guid.NewGuid();
        var yaml = serializer.Serialize(source, id);
        var (instance, restoredId) = serializer.Deserialize(yaml, out var changed);
        var copy = (ValueAsset)instance;
        Check(restoredId == id && !changed && serializer.Serialize(copy, id) == yaml,
            "Nested value assets must preserve identity and stable YAML.");
        Check(copy.Values["grid"][0][0, 0]!.Value.Numbers.SequenceEqual([7, 9])
            && copy.Values["grid"][0][0, 1] is null && copy.Empty.GetLength(2) == 4,
            "Data assets must preserve nullable structs, nested containers, and empty rectangular shapes.");
        var root = Path.Combine(Path.GetTempPath(), $"NestedValueAssets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "Values.pure.asset.yaml"), yaml);
            var store = DataAssetStore.ScanFolder(root, registry, out var diagnostics);
            Check(diagnostics.Count == 0, "Nested value assets must load without diagnostics.");
            var clone = store.Clone(registry);
            store.Get<ValueAsset>(id).Values["grid"][0][0, 0]!.Value.Numbers.Add(11);
            Check(clone.Get<ValueAsset>(id).Values["grid"][0][0, 0]!.Value.Numbers.SequenceEqual([7, 9]),
                "DataAssetStore.Clone must deep-copy references inside structs and nested containers.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
        Check(!DataAssetDescriptor.TryCreate(typeof(ExternalAsset), registry, out _, out _)
            && !DataAssetDescriptor.TryCreate(typeof(ExternalSceneAsset), registry, out _, out _),
            "Data assets must reject nested external asset and scene references even when currently null.");
        Reject(() => serializer.Serialize(new ExternalAsset(), Guid.NewGuid()));
        Reject(() => serializer.Serialize(new ExternalSceneAsset(), Guid.NewGuid()));
    }

    private static Dictionary<string, object?> Shape(int[] dimensions, object?[] items) =>
        new() { ["dimensions"] = dimensions, ["items"] = items };

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Malformed or unsupported values must fail with InvalidDataException.");
    }

    public struct ValueProbe
    {
        [Inspector] public List<int> Numbers { get; set; }
        [Inspector] public string Name { get; set; }
    }

    public sealed class RecursiveProbe
    {
        [Inspector] public List<RecursiveProbe> Children { get; set; } = [];
    }

    [DataAsset]
    public sealed class ValueAsset
    {
        [Inspector] public Dictionary<string, List<ValueProbe?[,]>> Values { get; set; } = [];
        [Inspector] public int[,,] Empty { get; set; } = new int[2, 0, 4];
    }

    public struct AssetReferenceValue
    {
        [Inspector] public ValueAsset? Asset { get; set; }
    }

    public struct SceneReferenceValue
    {
        [Inspector] public SceneObject? Target { get; set; }
    }

    [DataAsset]
    public sealed class ExternalAsset
    {
        [Inspector] public List<Dictionary<string, AssetReferenceValue?[,]>> Values { get; set; } = [];
    }

    [DataAsset]
    public sealed class ExternalSceneAsset
    {
        [Inspector] public Dictionary<string, List<SceneReferenceValue?[,]>> Values { get; set; } = [];
    }
}
