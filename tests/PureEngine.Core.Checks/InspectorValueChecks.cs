using System.Globalization;
using System.Numerics;
using PureEngine.Core;
using YamlDotNet.Core;

static class InspectorValueChecks
{
    public static void Run()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        static void Reject(Action action, string message)
        {
            try { action(); }
            catch (Exception error) when (error is InvalidDataException or YamlException or ArgumentException or InvalidOperationException)
            { return; }
            throw new Exception(message);
        }

        var registry = new ComponentRegistry();
        registry.Register<ExtendedProbe>("checks.extended");
        registry.Register<Transform>("core.transform");
        var serializer = new SceneSerializer(registry);

        var scene = new Scene();
        var item = scene.AddEmpty();
        item.Rename("Extended");
        var sample = new ExtendedProbe
        {
            Ratio = 2.5,
            Position = new Vector3(1, 2, 3),
            Direction = new Vector2(4, -5),
            Color = new Vector4(1, 2, 3, 4),
            Rotation = new Quaternion(0, 0, 0, 1),
            Target = new Transform { LocalPosition = new Vector3(7, 8, 9) },
            Scores = [10, -20, 30],
            Tags = ["a", "b"],
            EmptyList = [],
            Counts = new Dictionary<string, int> { ["alice"] = 3, ["bob"] = -1 },
            Offsets = new Dictionary<string, Vector3> { ["home"] = new Vector3(1, 0, 0) },
            Maybe = 42,
            Missing = null,
            NullList = null,
            NullDict = null,
            NullTransform = null,
            Level = Difficulty.Normal,
            Access = Permissions.Read | Permissions.Write,
            MaybeLevel = Difficulty.Hard,
            MissingLevel = null,
            Stages = [Difficulty.Easy, Difficulty.Hard],
            Spawns = new Dictionary<string, Difficulty> { ["gate"] = Difficulty.Easy },
        };
        item.Attach(sample);
        var transformItem = scene.AddEmpty();
        transformItem.Rename("Mover");
        transformItem.Attach(new Transform { LocalPosition = new Vector3(1, 2, 3), LocalRotation = Quaternion.Identity, LocalScale = Vector3.One });

        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var yaml = serializer.Serialize(scene);
            Check(yaml.Contains("LocalPosition") && yaml.Contains("Scores"), "Extended YAML must contain vectors and collections.");
            var restored = serializer.Deserialize(yaml);
            var copy = restored.Objects[0].GetComponent<ExtendedProbe>()!;
            Check(copy.Ratio == 2.5, "double did not survive.");
            Check(copy.Position == new Vector3(1, 2, 3), "Vector3 did not survive.");
            Check(copy.Direction == new Vector2(4, -5), "Vector2 did not survive.");
            Check(copy.Color == new Vector4(1, 2, 3, 4), "Vector4 did not survive.");
            Check(copy.Rotation == new Quaternion(0, 0, 0, 1), "Quaternion did not survive.");
            Check(copy.Target is not null && copy.Target.LocalPosition == new Vector3(7, 8, 9), "Transform member did not survive.");
            Check(copy.Scores.SequenceEqual([10, -20, 30]), "int array did not survive.");
            Check(copy.Tags.SequenceEqual(["a", "b"]), "List<string> did not survive.");
            Check(copy.EmptyList.Count == 0, "Empty list did not survive.");
            Check(copy.Counts["alice"] == 3 && copy.Counts["bob"] == -1, "Dictionary<string,int> did not survive.");
            Check(copy.Offsets["home"] == new Vector3(1, 0, 0), "Dictionary<string,Vector3> did not survive.");
            Check(copy.Maybe == 42 && copy.Missing is null, "Nullable did not survive.");
            Check(copy.NullList is null && copy.NullDict is null && copy.NullTransform is null, "Null collections/Transform did not survive.");
            Check(copy.Level == Difficulty.Normal, "Enum did not survive.");
            Check(copy.Access == (Permissions.Read | Permissions.Write), "Flags enum did not survive.");
            Check(copy.MaybeLevel == Difficulty.Hard && copy.MissingLevel is null, "Nullable enum did not survive.");
            Check(copy.Stages.SequenceEqual([Difficulty.Easy, Difficulty.Hard]), "Enum list did not survive.");
            Check(copy.Spawns["gate"] == Difficulty.Easy, "Enum dictionary did not survive.");
            var mover = restored.Objects[1].GetComponent<Transform>()!;
            Check(mover.LocalPosition == new Vector3(1, 2, 3) && mover.LocalScale == Vector3.One, "Transform component did not survive.");
            Check(serializer.Serialize(restored) == yaml, "Extended save/load changed output.");
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }

        // Clone must deep-copy reference values; editing the source must not leak into the clone.
        var clone = serializer.Clone(scene);
        var cloneProbe = clone.Objects[0].GetComponent<ExtendedProbe>()!;
        sample.Scores[0] = 999;
        sample.Tags.Add("leaked");
        sample.Counts["alice"] = 999;
        sample.Target!.LocalPosition = new Vector3(999, 999, 999);
        Check(cloneProbe.Scores[0] == 10 && cloneProbe.Tags.Count == 2 && cloneProbe.Counts["alice"] == 3
            && cloneProbe.Target!.LocalPosition == new Vector3(7, 8, 9), "Clone must deep-copy collections and Transform.");
        sample.Scores[0] = 10;
        sample.Tags.Remove("leaked");
        sample.Counts["alice"] = 3;
        sample.Target.LocalPosition = new Vector3(7, 8, 9);

        // Null string elements and empty collections survive.
        sample.Tags = ["", "x"];
        Check(serializer.Deserialize(serializer.Serialize(scene)).Objects[0].GetComponent<ExtendedProbe>()!.Tags.SequenceEqual(["", "x"]),
            "Empty string elements did not survive.");
        sample.Tags = ["a", "b"];

        // Rejections: non-finite, malformed mappings, collections as scalars, unsupported types.
        sample.Ratio = double.NaN;
        Reject(() => serializer.Serialize(scene), "Non-finite double saved.");
        sample.Ratio = 2.5;
        sample.Position = new Vector3(float.NaN, 0, 0);
        Reject(() => serializer.Serialize(scene), "Non-finite Vector3 saved.");
        sample.Position = new Vector3(1, 2, 3);
        var yamlBase = serializer.Serialize(scene);
        Reject(() => serializer.Deserialize(yamlBase.Replace("Ratio: 2.5", "Ratio: NaN")), "NaN double accepted.");
        Reject(() => serializer.Deserialize(yamlBase.Replace("Position:", "Missing:")), "Unknown member discarded.");
        Reject(() => serializer.Deserialize(yamlBase.Replace("Scores:", "Scores: not-a-sequence")), "String accepted as sequence.");
        Reject(() => serializer.Deserialize(yamlBase.Replace("Level: Normal", "Level: Impossible")), "Unknown enum name accepted.");
        var numericEnum = serializer.Deserialize(yamlBase.Replace("Level: Normal", "Level: 0"));
        Check(numericEnum.Objects[0].GetComponent<ExtendedProbe>()!.Level == Difficulty.Easy, "Numeric enum value did not survive.");
        Reject(() => serializer.Deserialize(yamlBase + "\n  extra: true\n"), "Unknown document field accepted.");
        var badRegistry = new ComponentRegistry();
        badRegistry.Register<UnsupportedProbe>("checks.unsupported");
        var badScene = new Scene();
        badScene.AddEmpty().Attach(new UnsupportedProbe());
        Reject(() => new SceneSerializer(badRegistry).Serialize(badScene), "Unsupported member type saved.");
        var nestedRegistry = new ComponentRegistry();
        nestedRegistry.Register<NestedListProbe>("checks.nested");
        var nestedScene = new Scene();
        nestedScene.AddEmpty().Attach(new NestedListProbe());
        Reject(() => new SceneSerializer(nestedRegistry).Serialize(nestedScene), "Nested collections saved.");
        var intKeyRegistry = new ComponentRegistry();
        intKeyRegistry.Register<IntKeyProbe>("checks.intkey");
        var intKeyScene = new Scene();
        intKeyScene.AddEmpty().Attach(new IntKeyProbe());
        Reject(() => new SceneSerializer(intKeyRegistry).Serialize(intKeyScene), "Non-string dictionary keys saved.");

        Console.WriteLine("PASS: Inspector extended values (double/vectors/Transform/enum/collections/nullable) round-trip, clone separation, and rejections.");
    }

    public enum Difficulty
    {
        Easy,
        Normal,
        Hard,
    }

    [Flags]
    public enum Permissions
    {
        None = 0,
        Read = 1,
        Write = 2,
        Execute = 4,
    }

    public sealed class ExtendedProbe
    {
        [Inspector] public double Ratio { get; set; }
        [Inspector] public Vector2 Direction { get; set; }
        [Inspector] public Vector3 Position { get; set; }
        [Inspector] public Vector4 Color { get; set; }
        [Inspector] public Quaternion Rotation { get; set; }
        [Inspector] public Transform? Target { get; set; }
        [Inspector] public int[] Scores { get; set; } = [];
        [Inspector] public List<string> Tags { get; set; } = [];
        [Inspector] public List<int> EmptyList { get; set; } = [];
        [Inspector] public Dictionary<string, int> Counts { get; set; } = [with(StringComparer.Ordinal)];
        [Inspector] public Dictionary<string, Vector3> Offsets { get; set; } = [with(StringComparer.Ordinal)];
        [Inspector] public int? Maybe { get; set; }
        [Inspector] public int? Missing { get; set; }
        [Inspector] public List<int>? NullList { get; set; }
        [Inspector] public Dictionary<string, int>? NullDict { get; set; }
        [Inspector] public Transform? NullTransform { get; set; }
        [Inspector] public Difficulty Level { get; set; }
        [Inspector] public Permissions Access { get; set; }
        [Inspector] public Difficulty? MaybeLevel { get; set; }
        [Inspector] public Difficulty? MissingLevel { get; set; }
        [Inspector] public List<Difficulty> Stages { get; set; } = [];
        [Inspector] public Dictionary<string, Difficulty> Spawns { get; set; } = [with(StringComparer.Ordinal)];
    }

    public sealed class UnsupportedProbe
    {
        [Inspector] public DateTime When { get; set; }
    }

    public sealed class NestedListProbe
    {
        [Inspector] public List<List<int>> Nested { get; set; } = [];
    }

    public sealed class IntKeyProbe
    {
        [Inspector] public Dictionary<int, int> ById { get; set; } = [];
    }
}
