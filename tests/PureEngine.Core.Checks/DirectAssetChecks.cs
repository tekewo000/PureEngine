using PureEngine.Core;
using PureEngine.Runtime;
using Microsoft.Extensions.DependencyInjection;

internal static class DirectAssetChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "PureEngine-DirectAssets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var registry = new ComponentRegistry();
            registry.Register<WeaponFixture>("asset.weapon");
            registry.Register<Holder>("checks.holder");
            var id = Guid.NewGuid();
            var path = Path.Combine(root, "Weapon.pure.asset.yaml");
            File.WriteAllText(path, new DataAssetSerializer(registry).Serialize(new WeaponFixture { Attack = 17 }, id));
            var assets = DataAssetStore.ScanFolder(root, registry, out _);
            var serializer = new SceneSerializer(registry, assets);
            var scene = serializer.Deserialize("version: 3\nobjects: []");
            var weapon = assets.Get<WeaponFixture>(id);
            var holder = new Holder { Weapon = weapon, Weapons = [weapon], ByName = new() { ["main"] = weapon } };
            scene.AddEmpty().Attach(holder);
            var yaml = serializer.Serialize(scene);
            Check(yaml.Contains(id.ToString("D")) && !yaml.Contains("Attack:"), "Scene must store asset IDs, not asset values.");
            var restored = serializer.Deserialize(yaml).Objects.Single().GetComponent<Holder>()!;
            Check(ReferenceEquals(restored.Weapon, restored.Weapons[0]) && ReferenceEquals(restored.Weapon, restored.ByName["main"]),
                "All slots must share one instance per snapshot.");
            var clone = new SceneSerializer(registry).Clone(scene).Objects.Single().GetComponent<Holder>()!;
            Check(!ReferenceEquals(clone.Weapon, weapon) && ReferenceEquals(clone.Weapon, clone.Weapons[0]),
                "Clone must isolate snapshots while preserving sharing.");
            for (var run = 0; run < 2; run++)
            {
                using var play = PlaySession.Prepare(scene, registry, services =>
                    services.AddSingleton(DataAssetStore.ScanFolder(root, registry, out _)));
                var live = play.Runtime.Scene.Objects.Single().GetComponent<Holder>()!;
                Check(live.Weapon?.Attack == 17 && !ReferenceEquals(live.Weapon, weapon), "Play must resolve fresh saved assets.");
                live.Weapon!.Attack = 99;
            }
            Check(weapon.Attack == 17, "Play must not mutate editing assets.");
            File.Delete(path);
            var missingSerializer = new SceneSerializer(registry, DataAssetStore.ScanFolder(root, registry, out _));
            var missing = missingSerializer.Deserialize(yaml);
            Check(missing.Objects.Single().GetComponent<Holder>()!.Weapon is null, "Missing assets must resolve to null.");
            Check(missingSerializer.Serialize(missing).Contains(id.ToString("D")), "Missing IDs must survive saving.");
            File.WriteAllText(path, new DataAssetSerializer(registry).Serialize(new WeaponFixture { Attack = 23 }, id));
            var recovered = new SceneSerializer(registry, DataAssetStore.ScanFolder(root, registry, out _))
                .Deserialize(missingSerializer.Serialize(missing));
            Check(recovered.Objects.Single().GetComponent<Holder>()!.Weapon?.Attack == 23, "Restored files must recover by ID.");
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine("PASS: direct asset references, collections, identity, clone, Play isolation, missing IDs, and recovery.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public sealed class Holder
    {
        [Inspector] public WeaponFixture? Weapon { get; init; }
        [Inspector] public WeaponFixture[] Weapons { get; init; } = [];
        [Inspector] public Dictionary<string, WeaponFixture> ByName { get; init; } = [];
    }
}
