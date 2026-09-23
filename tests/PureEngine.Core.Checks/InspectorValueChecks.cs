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
        var targetItem = scene.AddEmpty();
        targetItem.Rename("TargetHolder");
        var targetTransform = new Transform { LocalPosition = new Vector3(7, 8, 9) };
        targetItem.Attach(targetTransform);
        var sample = new ExtendedProbe
        {
            Ratio = 2.5,
            Position = new Vector3(1, 2, 3),
            Direction = new Vector2(4, -5),
            Color = new Vector4(1, 2, 3, 4),
            Rotation = new Quaternion(0, 0, 0, 1),
            Target = targetTransform,
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
            Check(ReferenceEquals(copy.Target, restored.Objects[1].GetComponent<Transform>()), "Transform reference must resolve to the same-scene instance.");
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
            var mover = restored.Objects[2].GetComponent<Transform>()!;
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
        targetTransform.LocalPosition = new Vector3(999, 999, 999);
        Check(cloneProbe.Scores[0] == 10 && cloneProbe.Tags.Count == 2 && cloneProbe.Counts["alice"] == 3
            && cloneProbe.Target!.LocalPosition == new Vector3(7, 8, 9), "Clone must deep-copy collections and resolve references to the clone target.");
        Check(!ReferenceEquals(cloneProbe.Target, targetTransform) && ReferenceEquals(cloneProbe.Target, clone.Objects[1].GetComponent<Transform>()),
            "Clone must resolve references to the clone scene, not the source instance.");
        sample.Scores[0] = 10;
        sample.Tags.Remove("leaked");
        sample.Counts["alice"] = 3;
        targetTransform.LocalPosition = new Vector3(7, 8, 9);

        // Null string elements and empty collections survive.
        sample.Tags = ["", "x"];
        Check(serializer.Deserialize(serializer.Serialize(scene)).Objects[0].GetComponent<ExtendedProbe>()!.Tags.SequenceEqual(["", "x"]),
            "Empty string elements did not survive.");
        sample.Tags = ["a", "b"];

        // Color stores RGBA floats, survives YAML and Clone, and rejects non-finite or malformed values.
        var colorRegistry = new ComponentRegistry();
        colorRegistry.Register<ColorProbe>("checks.color");
        var colorSerializer = new SceneSerializer(colorRegistry);
        var colorScene = new Scene();
        var colorItem = colorScene.AddEmpty();
        colorItem.Rename("Color");
        var colorSample = new ColorProbe
        {
            Tint = new Color(1f, 0.5f, 0.25f, 0.75f),
            MaybeTint = new Color(0f, 1f, 0f, 1f),
            MissingTint = null,
            Swatches = [new Color(1f, 0f, 0f, 1f), new Color(0f, 0f, 1f, 0.5f)],
            Palette = new Dictionary<string, Color>(StringComparer.Ordinal) { ["accent"] = new Color(0f, 1f, 0f, 1f) },
        };
        colorItem.Attach(colorSample);
        var savedCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var colorYaml = colorSerializer.Serialize(colorScene);
            Check(colorYaml.Contains("Tint:") && colorYaml.Contains("r:") && colorYaml.Contains("Swatches"), "Color YAML must use r/g/b/a mappings.");
            var colorCopy = colorSerializer.Deserialize(colorYaml).Objects[0].GetComponent<ColorProbe>()!;
            Check(colorCopy.Tint == new Color(1f, 0.5f, 0.25f, 0.75f), "Color did not survive.");
            Check(colorCopy.MaybeTint == new Color(0f, 1f, 0f, 1f) && colorCopy.MissingTint is null, "Nullable Color did not survive.");
            Check(colorCopy.Swatches.SequenceEqual([new Color(1f, 0f, 0f, 1f), new Color(0f, 0f, 1f, 0.5f)]), "Color list did not survive.");
            Check(colorCopy.Palette["accent"] == new Color(0f, 1f, 0f, 1f), "Color dictionary did not survive.");
            Check(colorSerializer.Serialize(colorSerializer.Deserialize(colorYaml)) == colorYaml, "Color save/load changed output.");
            var colorClone = colorSerializer.Clone(colorScene).Objects[0].GetComponent<ColorProbe>()!;
            colorSample.Swatches[0] = new Color(0f, 0f, 0f, 1f);
            colorSample.Palette["accent"] = new Color(0f, 0f, 0f, 1f);
            Check(colorClone.Swatches[0] == new Color(1f, 0f, 0f, 1f) && colorClone.Palette["accent"] == new Color(0f, 1f, 0f, 1f), "Clone must copy Color collections.");
            colorSample.Swatches[0] = new Color(1f, 0f, 0f, 1f);
            colorSample.Palette["accent"] = new Color(0f, 1f, 0f, 1f);
        }
        finally { CultureInfo.CurrentCulture = savedCulture; }
        colorSample.Tint = new Color(float.NaN, 0f, 0f, 1f);
        Reject(() => colorSerializer.Serialize(colorScene), "Non-finite Color saved.");
        colorSample.Tint = new Color(1f, 0.5f, 0.25f, 0.75f);
        var colorYamlBase = colorSerializer.Serialize(colorScene);
        Reject(() => colorSerializer.Deserialize(colorYamlBase.Replace("r: 1", "r: NaN")), "NaN Color channel accepted.");
        Reject(() => colorSerializer.Deserialize(colorYamlBase.Replace("Tint:", "Tint: not-a-mapping")), "String accepted as Color.");
        Reject(() => colorSerializer.Deserialize(colorYamlBase.Replace("r: 1", "x: 1")), "Vector keys accepted as Color.");

        // Custom classes nest as mappings: direct members, doubly nested members, arrays, lists, and dictionaries.
        var customRegistry = new ComponentRegistry();
        customRegistry.Register<NestedProbe>("checks.custom");
        var customSerializer = new SceneSerializer(customRegistry);
        var customScene = new Scene();
        var nestedItem = customScene.AddEmpty();
        nestedItem.Rename("Nested");
        var nested = new NestedProbe
        {
            Boss = new SkillStats { Hp = 30, Name = "Rex" },
            Loadout = new SkillLoadout { Weapon = "Bow", Stats = new SkillStats { Hp = 5, Name = "Kit" } },
            Squad = [new SkillStats { Hp = 1, Name = "A" }, new SkillStats { Hp = 2, Name = "B" }],
            Party = [new SkillStats { Hp = 3, Name = "C" }],
            Ranks = new Dictionary<string, SkillStats>(StringComparer.Ordinal)
            {
                ["leader"] = new SkillStats { Hp = 9, Name = "Z" },
            },
        };
        nestedItem.Attach(nested);
        var nestedYaml = customSerializer.Serialize(customScene);
        var nestedCopy = customSerializer.Deserialize(nestedYaml).Objects[0].GetComponent<NestedProbe>()!;
        Check(nestedCopy.Boss is not null && nestedCopy.Boss.Hp == 30 && nestedCopy.Boss.Name == "Rex",
            "Custom class member did not survive.");
        Check(!ReferenceEquals(nestedCopy.Boss, nested.Boss), "Custom class member must be deep-copied.");
        Check(nestedCopy.Loadout is not null && nestedCopy.Loadout.Weapon == "Bow"
            && nestedCopy.Loadout.Stats is not null && nestedCopy.Loadout.Stats.Hp == 5 && nestedCopy.Loadout.Stats.Name == "Kit",
            "Doubly nested custom class did not survive.");
        Check(nestedCopy.Squad.Length == 2 && nestedCopy.Squad[0].Hp == 1 && nestedCopy.Squad[1].Name == "B",
            "Custom class array did not survive.");
        Check(nestedCopy.Party.Count == 1 && nestedCopy.Party[0].Hp == 3 && nestedCopy.Party[0].Name == "C",
            "Custom class list did not survive.");
        Check(nestedCopy.Ranks.Count == 1 && nestedCopy.Ranks["leader"].Hp == 9 && nestedCopy.Ranks["leader"].Name == "Z",
            "Custom class dictionary did not survive.");
        Check(customSerializer.Serialize(customSerializer.Deserialize(nestedYaml)) == nestedYaml,
            "Custom class save/load changed output.");
        var formerYaml = nestedYaml.Replace("Hp:", "Health:");
        Check(customSerializer.Serialize(customSerializer.Deserialize(formerYaml)) == nestedYaml,
            "Former names must preserve custom values in members, nested objects, arrays, lists and dictionaries.");
        Reject(() => customSerializer.Deserialize(nestedYaml.Replace("Name: Rex", "Health: 31")),
            "Current and former nested names for the same member must be rejected.");
        // Null custom members survive, and missing nested keys keep their initializers.
        nested.Boss = null;
        Check(customSerializer.Deserialize(customSerializer.Serialize(customScene)).Objects[0].GetComponent<NestedProbe>()!.Boss is null,
            "Null custom class member did not survive.");
        var renamedYaml = nestedYaml.Replace("Name: Rex", "Nickname: Rex");
        var renamedCopy = customSerializer.Deserialize(renamedYaml).Objects[0].GetComponent<NestedProbe>()!;
        Check(renamedCopy.Boss is not null && renamedCopy.Boss.Hp == 30 && renamedCopy.Boss.Name == "fresh",
            "Unknown nested keys must be ignored while matching values and initializers remain intact.");
        // Clone must deep-copy nested custom objects; editing the source must not leak into the clone.
        nested.Boss = new SkillStats { Hp = 30, Name = "Rex" };
        var nestedClone = customSerializer.Clone(customScene).Objects[0].GetComponent<NestedProbe>()!;
        nested.Boss.Hp = 999;
        nested.Loadout!.Stats.Hp = 999;
        nested.Squad[0].Hp = 999;
        nested.Party[0].Hp = 999;
        nested.Ranks["leader"].Hp = 999;
        Check(nestedClone.Boss!.Hp == 30 && nestedClone.Loadout!.Stats.Hp == 5
            && nestedClone.Squad[0].Hp == 1 && nestedClone.Party[0].Hp == 3 && nestedClone.Ranks["leader"].Hp == 9,
            "Clone must deep-copy nested custom objects.");
        // Rejections: recursive, abstract, generic, struct, or unconstructible custom classes; derived mix-ins.
        var recursiveRegistry = new ComponentRegistry();
        recursiveRegistry.Register<RecursiveProbe>("checks.recursive");
        var recursiveScene = new Scene();
        recursiveScene.AddEmpty().Attach(new RecursiveProbe());
        Reject(() => new SceneSerializer(recursiveRegistry).Serialize(recursiveScene), "Recursive custom class saved.");
        var abstractRegistry = new ComponentRegistry();
        abstractRegistry.Register<AbstractProbe>("checks.abstract");
        var abstractScene = new Scene();
        abstractScene.AddEmpty().Attach(new AbstractProbe());
        Reject(() => new SceneSerializer(abstractRegistry).Serialize(abstractScene), "Abstract custom class member saved.");
        var genericRegistry = new ComponentRegistry();
        genericRegistry.Register<GenericProbe>("checks.generic");
        var genericScene = new Scene();
        genericScene.AddEmpty().Attach(new GenericProbe());
        Reject(() => new SceneSerializer(genericRegistry).Serialize(genericScene), "Generic custom class member saved.");
        var structRegistry = new ComponentRegistry();
        structRegistry.Register<StructProbe>("checks.struct");
        var structScene = new Scene();
        structScene.AddEmpty().Attach(new StructProbe());
        Reject(() => new SceneSerializer(structRegistry).Serialize(structScene), "Struct member saved.");
        var noCtorRegistry = new ComponentRegistry();
        noCtorRegistry.Register<NoCtorProbe>("checks.noctor");
        var noCtorScene = new Scene();
        noCtorScene.AddEmpty().Attach(new NoCtorProbe());
        Reject(() => new SceneSerializer(noCtorRegistry).Serialize(noCtorScene), "Unconstructible custom class saved.");
        nested.Boss = new DerivedStats { Hp = 1, Name = "D", Extra = true };
        Reject(() => customSerializer.Serialize(customScene), "Derived custom class instance saved as its base.");
        nested.Boss = new SkillStats { Hp = 30, Name = "Rex" };

        // Rejections: non-finite, malformed mappings, collections as scalars, unsupported types.
        sample.Ratio = double.NaN;
        Reject(() => serializer.Serialize(scene), "Non-finite double saved.");
        sample.Ratio = 2.5;
        sample.Position = new Vector3(float.NaN, 0, 0);
        Reject(() => serializer.Serialize(scene), "Non-finite Vector3 saved.");
        sample.Position = new Vector3(1, 2, 3);
        var yamlBase = serializer.Serialize(scene);
        Reject(() => serializer.Deserialize(yamlBase.Replace("Ratio: 2.5", "Ratio: NaN")), "NaN double accepted.");
        var removedVector = serializer.Deserialize(yamlBase.Replace("\n      Position:", "\n      RemovedPosition:"));
        Check(removedVector.Objects[0].GetComponent<ExtendedProbe>()!.Position == Vector3.Zero
            && removedVector.Objects[0].GetComponent<ExtendedProbe>()!.Ratio == 2.5,
            "Unknown structured Inspector values must be ignored while matching values remain intact.");
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

        Console.WriteLine("PASS: Inspector extended values (double/vectors/Transform/enum/collections/nullable/custom classes) round-trip, clone separation, and rejections.");
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

    public sealed class ColorProbe
    {
        [Inspector] public Color Tint { get; set; } = Color.White;
        [Inspector] public Color? MaybeTint { get; set; }
        [Inspector] public Color? MissingTint { get; set; }
        [Inspector] public List<Color> Swatches { get; set; } = [];
        [Inspector] public Dictionary<string, Color> Palette { get; set; } = [with(StringComparer.Ordinal)];
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

    public class SkillStats
    {
        [Inspector, FormerlySerializedAs("Health")] public int Hp { get; set; } = 7;
        [Inspector] public string Name = "fresh";
    }

    public sealed class DerivedStats : SkillStats
    {
        [Inspector] public bool Extra { get; set; }
    }

    public sealed class SkillLoadout
    {
        [Inspector] public string Weapon = "sword";
        [Inspector] public SkillStats Stats { get; set; } = new();
    }

    public sealed class NestedProbe
    {
        [Inspector] public SkillStats? Boss { get; set; }
        [Inspector] public SkillLoadout? Loadout { get; set; }
        [Inspector] public SkillStats[] Squad { get; set; } = [];
        [Inspector] public List<SkillStats> Party { get; set; } = [];
        [Inspector] public Dictionary<string, SkillStats> Ranks { get; set; } = [with(StringComparer.Ordinal)];
    }

    public sealed class RecursiveNode
    {
        [Inspector] public RecursiveNode? Next { get; set; }
    }

    public sealed class RecursiveProbe
    {
        [Inspector] public RecursiveNode? Head { get; set; }
    }

    public abstract class AbstractStats
    {
        [Inspector] public int Hp { get; set; }
    }

    public sealed class AbstractProbe
    {
        [Inspector] public AbstractStats? Stats { get; set; }
    }

    public sealed class Box<T>
    {
        [Inspector] public int Count { get; set; }
    }

    public sealed class GenericProbe
    {
        [Inspector] public Box<int>? Box { get; set; }
    }

    public struct PointStats
    {
#pragma warning disable CS0649 // Never assigned: intentional negative case for struct rejection.
        [Inspector] public int X;
#pragma warning restore CS0649
    }

    public sealed class StructProbe
    {
        [Inspector] public PointStats Point { get; set; }
    }

    public sealed class NoCtorStats(int hp)
    {
        [Inspector]
        public int Hp { get; set; } = hp;
    }

    public sealed class NoCtorProbe
    {
        [Inspector] public NoCtorStats? Stats { get; set; }
    }
}
