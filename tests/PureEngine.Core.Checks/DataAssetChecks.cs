using PureEngine.Core;
using PureEngine.Editor;
using YamlDotNet.Core;

static class DataAssetChecks
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
            throw new InvalidOperationException(message);
        }

        var registry = new ComponentRegistry();
        registry.Register<WeaponFixture>("user.weapon");
        registry.Register<PotionFixture>("user.potion");
        registry.Register<RefFixture>("user.ref");
        registry.Register<LegacyNameFixture>("user.legacy");

        Check(DataAssetDescriptor.TryCreate(typeof(WeaponFixture), registry, out var weapon, out _)
            && weapon is { MenuPath: "WeaponFixture", DisplayName: "WeaponFixture", TypeId: "user.weapon" },
            "Data asset menu must default to the type name.");
        Check(DataAssetDescriptor.TryCreate(typeof(PotionFixture), registry, out var potion, out _)
            && potion is { MenuPath: "Items/Potion", DisplayName: "Potion" },
            "Data asset menu path must split into folders and a display name.");
        Check(DataAssetDescriptor.IsDataAssetType(typeof(WeaponFixture))
            && !DataAssetDescriptor.IsDataAssetType(typeof(PlainFixture))
            && !DataAssetDescriptor.IsDataAssetType(typeof(DerivedWeaponFixture)),
            "Only directly marked types count; the marker must not inherit.");
        Reject(() => DropNullDescriptor(typeof(PlainFixture), registry), "Unmarked class accepted as a data asset.");
        Reject(() => DropNullDescriptor(typeof(AbstractFixture), registry), "Abstract data asset accepted.");
        Reject(() => DropNullDescriptor(typeof(NoCtorFixture), registry), "Data asset without a parameterless constructor accepted.");
        Reject(() => DropNullDescriptor(typeof(UnsupportedFixture), registry), "Data asset with an unsupported member accepted.");
        Reject(() => DropNullDescriptor(typeof(BadMenuFixture), registry), "Data asset with an empty menu part accepted.");
        Reject(() => DropNullDescriptor(typeof(WeaponFixture), null!), "Null registry accepted.");
        Reject(() => DropNullDescriptor(typeof(WeaponFixture), new ComponentRegistry()), "Unregistered data asset accepted.");

        registry.Register<AnotherPotionFixture>("user.potion2");
        var listed = DataAssetDescriptor.DescribeAll(registry, out var diagnostics);
        Check(listed.Count == 4 && diagnostics.Any(text => text.Contains("duplicate")),
            "Duplicate data asset menus must keep the first type and report the clash.");

        var serializer = new DataAssetSerializer(registry);
        var sword = new WeaponFixture { Name = "Iron Sword", Attack = 10, Tags = ["melee"], Stats = new WeaponStatsFixture { Min = 8, Max = 12 } };
        var assetId = Guid.NewGuid();
        var yaml = serializer.Serialize(sword, assetId);
        var (restored, restoredId) = serializer.Deserialize(yaml, out var membersChanged);
        var copy = (WeaponFixture)restored;
        Check(restoredId == assetId && !membersChanged
            && copy.Name == "Iron Sword" && copy.Attack == 10
            && copy.Tags is ["melee"] && copy.Stats is { Min: 8, Max: 12 },
            "Data asset values, nesting, and identity did not survive.");
        Check(serializer.Serialize(restored, restoredId) == yaml, "Save/load changed data asset output.");

        var (renamedInstance, renamedId) = serializer.Deserialize(yaml.Replace("Attack:", "Missing:"), out membersChanged);
        Check(membersChanged && ((WeaponFixture)renamedInstance).Attack == 0
            && !serializer.Serialize(renamedInstance, renamedId).Contains("Missing:"),
            "Unknown data asset names must be ignored and disappear on the next save.");
        var legacyYaml = new DataAssetSerializer(registry).Serialize(new LegacyNameFixture { Power = 5 }, Guid.NewGuid());
        var (migratedInstance, _) = serializer.Deserialize(legacyYaml.Replace("Power:", "OldPower:"), out membersChanged);
        Check(membersChanged && ((LegacyNameFixture)migratedInstance).Power == 5,
            "Former data asset names must restore into the current member.");
        Reject(() => serializer.Serialize(new RefFixture { Target = sword }, Guid.NewGuid()),
            "Scene reference in a data asset accepted.");
        Reject(() => serializer.Serialize(sword, Guid.Empty), "Empty data asset ID accepted.");
        Reject(() => serializer.Deserialize(yaml.Replace("version: 1", "version: 99"), out _), "Future data asset version accepted.");
        Reject(() => serializer.Deserialize(yaml.Replace(assetId.ToString("D"), Guid.Empty.ToString("D")), out _), "Empty data asset ID accepted.");
        Reject(() => serializer.Deserialize(yaml.Replace("user.weapon", "missing.type"), out _), "Unknown data asset type accepted.");
        Reject(() => serializer.Deserialize(yaml.Replace("Attack: 10", "Attack: wrong"), out _), "Invalid data asset value accepted.");
        Reject(() => serializer.Deserialize("version: 1\nversion: 1\nid: 00000000-0000-0000-0000-000000000000\n", out _),
            "Duplicate data asset keys accepted.");

        CheckCompilerDetection();
        CheckFileRoundTrip(registry);
        CheckStore(registry);
        Console.WriteLine("PASS: data asset descriptors, menus, YAML round-trip, file creation, and store lookup.");
    }

    private static void CheckStore(ComponentRegistry registry)
    {
        var root = Path.Combine(Path.GetTempPath(), "DataAssetStore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var serializer = new DataAssetSerializer(registry);
            var firstId = Guid.NewGuid();
            File.WriteAllText(Path.Combine(root, "Sword.pure.asset.yaml"),
                serializer.Serialize(new WeaponFixture { Name = "Sword", Attack = 10 }, firstId));
            var secondId = Guid.NewGuid();
            Directory.CreateDirectory(Path.Combine(root, "Nested"));
            File.WriteAllText(Path.Combine(root, "Nested", "Potion.pure.asset.yaml"),
                serializer.Serialize(new PotionFixture { Power = 3 }, secondId));
            File.WriteAllText(Path.Combine(root, "Duplicate.pure.asset.yaml"),
                serializer.Serialize(new PotionFixture { Power = 9 }, firstId));
            File.WriteAllText(Path.Combine(root, "Broken.pure.asset.yaml"), "not: yaml: :");
            File.WriteAllText(Path.Combine(root, "Notes.txt"), "ignored");
            var store = DataAssetStore.ScanFolder(root, registry, out var diagnostics);
            if (store.Ids.Count != 2 || diagnostics.Count != 2)
                throw new InvalidOperationException(
                    $"Store must load 2 assets with 2 diagnostics, got {store.Ids.Count} and {diagnostics.Count}.");
            // First file wins on duplicates; scan order is ordinal by path.
            if (store.Get<PotionFixture>(firstId).Power != 9)
                throw new InvalidOperationException("Duplicate IDs must keep the first file.");
            try
            {
                _ = store.Get<WeaponFixture>(secondId);
                throw new InvalidOperationException("Type mismatch accepted.");
            }
            catch (InvalidDataException) { }
            if (!store.TryGet(secondId, out PotionFixture? potion) || potion?.Power != 3)
                throw new InvalidOperationException("TryGet failed for a stored asset.");
            if (store.TryGet(Guid.NewGuid(), out PotionFixture? _))
                throw new InvalidOperationException("TryGet accepted a missing ID.");
            if (store.GetAll<WeaponFixture>().Count != 0 || store.GetAll<PotionFixture>().Count != 2)
                throw new InvalidOperationException("GetAll returned the wrong assets.");
            var missing = DataAssetStore.ScanFolder(Path.Combine(root, "Absent"), registry, out var empty);
            if (missing.Ids.Count != 0 || empty.Count != 0)
                throw new InvalidOperationException("Missing folders must scan empty.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void DropNullDescriptor(Type type, ComponentRegistry registry)
    {
        if (DataAssetDescriptor.TryCreate(type, registry, out _, out _)) return;
        throw new InvalidDataException($"{type.FullName} is not a data asset type.");
    }

    private static void CheckCompilerDetection()
    {
        var root = Path.Combine(Path.GetTempPath(), "DataAssetChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "Loot.cs");
            File.WriteAllText(file, """
                using PureEngine.Core;
                namespace Game;
                [DataAsset("Items/Loot")]
                public sealed class LootData
                {
                    [Inspector] public int Gold { get; set; } = 100;
                }
                public sealed class PlainHelper
                {
                    public int Value { get; set; }
                }
                """);
            var compiled = UserCodeCompiler.CompileFiles([file]);
            if (!compiled.Success)
                throw new InvalidOperationException("Data asset fixture failed to compile: "
                    + string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));
            if (compiled.AttachableTypes.Count != 2
                || compiled.DataAssetTypes.SingleOrDefault()?.Name != "LootData")
                throw new InvalidOperationException("Only [DataAsset] types must be reported as data assets.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void CheckFileRoundTrip(ComponentRegistry registry)
    {
        var root = Path.Combine(Path.GetTempPath(), "DataAssetFiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = ProjectFile.Create(root, "AssetGame",
                new SceneSerializer(new ComponentRegistry()).Serialize(new Scene()));
            var name = project.NextDataAssetName("", "WeaponFixture");
            if (name != "WeaponFixture.pure.asset.yaml")
                throw new InvalidOperationException($"Unexpected data asset name: {name}");
            var path = Path.Combine(project.RootDirectory, name);
            project.ValidateDataAssetPath(path);
            var id = DataAssetFile.Create(path, typeof(WeaponFixture), registry);
            var second = project.NextDataAssetName("", "WeaponFixture");
            if (second == name) throw new InvalidOperationException("Data asset names must not collide.");
            var (instance, loadedId, typeId) = DataAssetFile.Load(path, registry);
            if (loadedId != id || typeId != "user.weapon" || instance.GetType() != typeof(WeaponFixture))
                throw new InvalidOperationException("Data asset file identity did not survive.");
            try
            {
                project.ValidateDataAssetPath(Path.Combine(project.RootDirectory, "Notes.txt"));
                throw new InvalidOperationException("Non-asset extension accepted.");
            }
            catch (InvalidDataException) { }
        }
        finally { Directory.Delete(Path.Combine(root, "AssetGame"), recursive: true); }
    }
}

[DataAsset]
public class WeaponFixture
{
    [Inspector] public string Name { get; set; } = "";
    [Inspector] public int Attack { get; set; }
    [Inspector] public List<string> Tags { get; set; } = [];
    [Inspector] public WeaponStatsFixture? Stats { get; set; }
}

public sealed class WeaponStatsFixture
{
    [Inspector] public int Min { get; set; }
    [Inspector] public int Max { get; set; }
}

public sealed class DerivedWeaponFixture : WeaponFixture
{
}

[DataAsset("Items/Potion")]
public sealed class PotionFixture
{
    [Inspector] public int Power { get; set; }
}

[DataAsset("Items/Potion")]
public sealed class AnotherPotionFixture
{
    [Inspector] public int Power { get; set; }
}

public sealed class PlainFixture
{
    [Inspector] public int Value { get; set; }
}

[DataAsset]
public abstract class AbstractFixture
{
    [Inspector] public int Value { get; set; }
}

[DataAsset]
public sealed class NoCtorFixture(int value)
{
    [Inspector] public int Value { get; set; } = value;
}

[DataAsset]
public sealed class UnsupportedFixture
{
    [Inspector] public DateTime When { get; set; }
}

[DataAsset("Items//Bad")]
public sealed class BadMenuFixture
{
    [Inspector] public int Value { get; set; }
}

[DataAsset]
public sealed class RefFixture
{
    [Inspector] public WeaponFixture? Target { get; set; }
}

[DataAsset]
public sealed class LegacyNameFixture
{
    [Inspector][FormerlySerializedAs("OldPower")] public int Power { get; set; }
}
