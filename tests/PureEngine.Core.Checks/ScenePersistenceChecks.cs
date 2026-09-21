using System.Globalization;
using PureEngine.Core;
using PureEngine.Core.Attributes;
using PureEngine.Editor;
using YamlDotNet.Core;

static class ScenePersistenceChecks
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
        registry.Register<PersistenceProbe>("checks.probe");
        Reject(() => registry.Register<PersistenceProbe>("other"), "Duplicate type registration accepted.");
        var serializer = new SceneSerializer(registry);
        var scene = new Scene();
        var item = scene.AddEmpty();
        item.Rename("プレイヤー: #1");
        var sample = new PersistenceProbe { Title = "001", Count = -25, Seconds = 0.125f, Enabled = true, Hidden = 123 };
        item.Attach(sample);
        scene.AddEmpty().Rename(item.Name);
        var originalCulture = CultureInfo.CurrentCulture;
        string yaml;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            yaml = serializer.Serialize(scene);
            var restored = serializer.Deserialize(yaml);
            var copy = restored.Objects[0].GetComponent<PersistenceProbe>()!;
            Check(restored.Objects.Count == 2 && restored.Objects[0].Id == item.Id
                && restored.Objects[1].Id == scene.Objects[1].Id && restored.Objects[1].Name == item.Name,
                "Object identity, order or duplicate names did not survive.");
            Check(copy.Title == "001" && copy.Count == -25 && copy.Seconds == 0.125f && copy.Enabled,
                "Inspector properties/fields did not survive.");
            Check(copy.Hidden == 7 && !yaml.Contains("Hidden"), "Unmarked member leaked into scene.");
            Check(serializer.Serialize(restored) == yaml, "Save/load changed output or scalar types.");
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }

        foreach (var text in new string?[] { "", "null", "~", "true", "yes", "1e3", "001", "日本語\nsecond: #line", null })
        {
            sample.Title = text;
            Check(serializer.Deserialize(serializer.Serialize(scene)).Objects[0].GetComponent<PersistenceProbe>()!.Title == text,
                $"String scalar did not survive: {text ?? "<null>"}");
        }
        sample.Title = "001";
        Check(serializer.Deserialize(serializer.Serialize(new Scene())).Objects.Count == 0, "Empty scene failed.");
        Reject(() => serializer.Deserialize(yaml.Replace("version: 1", "version: 99")), "Future version accepted.");
        Reject(() => serializer.Deserialize(yaml.Replace("checks.probe", "missing.type")), "Unknown component accepted.");
        Reject(() => serializer.Deserialize(yaml.Replace("Count:", "Missing:")), "Unknown member discarded.");
        Reject(() => serializer.Deserialize(yaml.Replace("Count: -25", "Count: wrong")), "Invalid int accepted.");
        Reject(() => serializer.Deserialize(yaml.Replace("Count: -25", "Count: 2147483648")), "Int overflow accepted.");
        Reject(() => serializer.Deserialize(yaml.Replace("Count: -25", "Count: [1, 2]")), "Collection accepted as scalar.");
        Reject(() => serializer.Deserialize(yaml.Replace("Seconds: 0.125", "Seconds: NaN")), "NaN accepted.");
        Reject(() => serializer.Deserialize(yaml.Replace("Seconds: 0.125", "Seconds: 1e100")), "Float overflow accepted.");
        Reject(() => serializer.Deserialize(yaml.Replace("Enabled: true", "Enabled: maybe")), "Invalid bool accepted.");
        Reject(() => serializer.Deserialize(yaml.Replace(scene.Objects[1].Id.ToString(), item.Id.ToString())), "Duplicate IDs accepted.");
        Reject(() => serializer.Deserialize("version: 1\nversion: 1\nobjects: []\n"), "Duplicate keys accepted.");
        Reject(() => serializer.Deserialize(yaml.Replace("Count: -25", "Count: -25\n      Count: 2")), "Duplicate value keys accepted.");
        Reject(() => serializer.Deserialize(yaml.Replace(item.Id.ToString(), Guid.Empty.ToString())), "Empty object ID accepted.");
        Reject(() => serializer.Deserialize("version: 1\n"), "Missing object list accepted.");
        Reject(() => serializer.Deserialize("version: 1\nobjects: []\nextra: true\n"), "Unknown document field accepted.");
        Reject(() => serializer.Deserialize(yaml + "\n---\nversion: 1\nobjects: []\n"), "Extra document ignored.");
        Reject(() => serializer.Deserialize("objects: ["), "Broken YAML accepted.");
        var defaulted = serializer.Deserialize(yaml.Replace("Count: -25", "# Count omitted"));
        Check(defaulted.Objects[0].GetComponent<PersistenceProbe>()!.Count == 10, "Missing member must retain initializer.");
        sample.Seconds = float.PositiveInfinity;
        Reject(() => serializer.Serialize(scene), "Non-finite float saved.");
        sample.Seconds = 0.125f;
        var unknownScene = new Scene();
        unknownScene.AddEmpty().Attach(new object());
        Reject(() => serializer.Serialize(unknownScene), "Unregistered component saved.");
        Check(scene.Objects[0] == item && item.GetComponent<PersistenceProbe>() == sample, "Failed reads mutated source scene.");

        var examplePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../Examples/Main.pure.scene.yaml"));
        var exampleSerializer = new SceneSerializer(ComponentAssets.Registry);
        var example = exampleSerializer.Deserialize(File.ReadAllText(examplePath));
        Check(example.Objects[0].GetComponent<PureEngine.Editor.Samples.PlayerStats>()!.Name == "001"
            && example.Objects[0].GetComponent<PureEngine.Editor.Samples.RoundSettings>()!.TurnSeconds == 30.5f,
            "The documented example must load using the Assets registry.");
        var duplicates = exampleSerializer.Serialize(example);
        var componentStart = duplicates.IndexOf("  - typeId:", StringComparison.Ordinal);
        Check(componentStart >= 0, "Expected component sequence in YAML.");
        Reject(() => exampleSerializer.Deserialize(duplicates + duplicates[componentStart..]), "Duplicate components accepted.");

        var directory = Path.Combine(Path.GetTempPath(), "PureEngine-YamlChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Main.pure.scene.yaml");
        try
        {
            SceneFile.Write(path, yaml);
            Check(File.ReadAllText(path) == yaml, "First save failed.");
            SceneFile.Write(path, serializer.Serialize(new Scene()));
            Check(serializer.Deserialize(File.ReadAllText(path)).Objects.Count == 0, "Replacement save failed.");
            var previous = File.ReadAllText(path);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var failed = false;
                try { SceneFile.Write(path, yaml); }
                catch (IOException) { failed = true; }
                Check(failed, "A locked destination was unexpectedly replaced.");
            }
            Check(File.ReadAllText(path) == previous, "Failed save damaged destination.");
            Check(Directory.GetFiles(directory).Length == 1, "Temporary files leaked.");
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }
}

public sealed class PersistenceProbe
{
    [Inspector] public string? Title { get; set; } = "default";
    [Inspector] public int Count { get; set; } = 10;
    [Inspector] public float Seconds { get; set; }
    [Inspector] public bool Enabled;
    public int Hidden { get; set; } = 7;
}
