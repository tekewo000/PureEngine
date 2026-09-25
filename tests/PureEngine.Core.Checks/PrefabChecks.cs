using Microsoft.Extensions.DependencyInjection;
using PureEngine.Core;
using PureEngine.Runtime;
using YamlDotNet.Core;

/// <summary>
/// Copy-only prefab checks: capture, file round-trip, placement with fresh IDs, internal/external
/// references, Missing, priorities, member tolerance, structural rejection, rollback, catalog scan,
/// spawner binding, and Play spawning with isolation across runs.
/// </summary>
static class PrefabChecks
{
    public static void Run()
    {
        TemplateReferences();
        Roundtrip();
        ExternalAndMissing();
        Tolerance();
        StructureRejections();
        Rollback();
        CatalogScan();
        SpawnerBinding();
        PlaySpawning();
        Console.WriteLine("PASS: prefab capture, round-trip, fresh IDs, references, missing, priorities, tolerance, rejection, rollback, catalog, spawner, and Play isolation.");
    }

    private static ComponentRegistry Registry()
    {
        var registry = new ComponentRegistry();
        registry.Register<PrefabPart>("checks.prefab-part");
        registry.Register<PrefabHolder>("checks.prefab-holder");
        registry.Register<PrefabLifecycle>("checks.prefab-lifecycle");
        registry.Register<PrefabWeapon>("checks.prefab-weapon");
        registry.Register<SpawnerProbe>("checks.spawner-probe");
        return registry;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or YamlException or ArgumentException or InvalidOperationException)
        { return; }
        throw new Exception(message);
    }

    private sealed record SourceScene(Scene Scene, SceneObject Root, SceneObject Child, PrefabHolder Holder, PrefabPart ChildPart, PrefabLifecycle Lifecycle, Guid PrefabId, Guid WeaponId, SceneObject Outside, PrefabHolder OutsideHolder);

    private static SourceScene BuildSource(ComponentRegistry registry, DataAssetStore assets)
    {
        var serializer = new SceneSerializer(registry, assets);
        var scene = serializer.Deserialize("version: 3\nobjects: []");
        var root = scene.AddEmpty();
        root.Rename("Enemy");
        var child = scene.AddEmpty();
        child.Rename("Gun");
        child.SetParent(root);
        var part = new PrefabPart { Value = 42, Name = "Cannon" };
        child.Attach(part);
        var lifecycle = new PrefabLifecycle { Hp = 100 };
        root.Attach(lifecycle);
        root.SetStartPriority(lifecycle, 5);
        root.SetUpdatePriority(lifecycle, -3);
        root.SetDestroyPriority(lifecycle, 2);
        var weapon = assets.Get<PrefabWeapon>(assets.Ids.Single());
        var holder = new PrefabHolder
        {
            Single = part,
            Owner = child,
            Self = null,
            Tags = [part],
            Map = new Dictionary<string, PrefabPart?>(StringComparer.Ordinal) { ["main"] = part },
            Config = new PrefabConfig { Primary = part, Owner = child, Extras = [part] },
            Weapon = weapon,
        };
        root.Attach(holder);
        holder.Self = holder;
        var outside = scene.AddEmpty();
        outside.Rename("Outside");
        var outsideHolder = new PrefabHolder();
        outside.Attach(outsideHolder);
        holder.Peer = outsideHolder;
        var weaponId = assets.Ids.Single();
        return new SourceScene(scene, root, child, holder, part, lifecycle, Guid.NewGuid(), weaponId, outside, outsideHolder);
    }

    private static DataAssetStore WriteWeaponAsset(ComponentRegistry registry, string directory, int attack)
    {
        Directory.CreateDirectory(directory);
        var id = Guid.NewGuid();
        File.WriteAllText(Path.Combine(directory, "Weapon.pure.asset.yaml"),
            new DataAssetSerializer(registry).Serialize(new PrefabWeapon { Attack = attack }, id));
        return DataAssetStore.ScanFolder(directory, registry, out _);
    }

    private static void TemplateReferences()
    {
        var registry = Registry();
        var source = new Scene();
        var root = source.AddEmpty();
        root.Attach(new PrefabPart { Value = 42 });
        var document = new PrefabSerializer(registry).Capture(source, root);
        document.Id = Guid.NewGuid();
        var scene = new Scene();
        var host = scene.AddEmpty();
        var broken = PrefabSerializer.Parse(PrefabSerializer.Serialize(document));
        broken.Objects![0].Components![0].Values!["Value"] = "invalid";
        Reject(() => scene.Prefabs.Assign(broken, typeof(PrefabPart), registry), "Invalid templates must reject assignment.");
        var template = (PrefabPart)scene.Prefabs.Assign(document, typeof(PrefabPart), registry);
        var templateRoot = (SceneObject)scene.Prefabs.Assign(document, typeof(SceneObject), registry);
        host.Attach(new PrefabHolder { Single = template, Owner = templateRoot, Tags = [template],
            Map = new() { ["main"] = template }, Config = new PrefabConfig { Primary = template } });
        var serializer = new SceneSerializer(registry);
        var yaml = serializer.Serialize(scene);
        Check(scene.Objects.Count == 1 && host.Children.Count == 0, "Template assignments must never join the hierarchy.");
        var clone = serializer.Clone(scene);
        var holder = clone.Objects.Single().GetComponent<PrefabHolder>()!;
        Check(holder.Single is { Value: 42 } && !ReferenceEquals(holder.Single, template)
            && ReferenceEquals(holder.Single, holder.Owner!.GetComponent<PrefabPart>())
            && ReferenceEquals(holder.Single, holder.Tags.Single()) && ReferenceEquals(holder.Single, holder.Map["main"])
            && ReferenceEquals(holder.Single, holder.Config!.Primary), "Clone must isolate and reconnect typed prefab references in every slot.");
        var migrated = SceneCodeMigrator.Migrate(scene, registry, registry);
        Check(migrated.Objects.Single().GetComponent<PrefabHolder>()!.Single is { Value: 42 }, "Code reload must preserve typed prefab references.");
        var missing = serializer.Deserialize(yaml);
        Check(missing.Objects.Single().GetComponent<PrefabHolder>()!.Single is null && missing.References.MissingCount == 5,
            "Unavailable prefabs must retain their asset and target identities as Missing.");
        Check(serializer.Serialize(missing) == yaml, "Saving Missing prefab references must preserve both IDs.");
        var cloneSerializer = new PrefabSerializer(registry);
        var holderPrefab = cloneSerializer.Capture(scene, host);
        holderPrefab.Id = Guid.NewGuid();
        var placed = cloneSerializer.Instantiate(scene, holderPrefab);
        Check(ReferenceEquals(placed.GetComponent<PrefabHolder>()!.Single, template), "Prefab copies must preserve external template identities.");
        var spawner = new PrefabSpawner();
        spawner.Bind(clone, registry, null, null);
        var instance = spawner.Instantiate(holder.Single!);
        Check(instance.Value == 42 && !ReferenceEquals(instance, holder.Single) && clone.Objects.Count == 2,
            "Instantiate must return the requested component of exactly one new subtree.");
        var instanceRoot = clone.Objects.Single(item => item.Components.Contains(instance));
        Check(instanceRoot.Parent is null && clone.Remove(instanceRoot) && clone.Objects.Count == 1,
            "Instances must default to roots and remain removable.");
        Reject(() => spawner.Instantiate(new PrefabPart()), "Unassigned component instances must not spawn prefabs.");
    }

    private static void Roundtrip()
    {
        var registry = Registry();
        var directory = Path.Combine(Path.GetTempPath(), "PureEngine-PrefabRoundtrip-" + Guid.NewGuid().ToString("N"));
        try
        {
            var assets = WriteWeaponAsset(registry, directory, 17);
            var source = BuildSource(registry, assets);
            var serializer = new PrefabSerializer(registry);
            var document = serializer.Capture(source.Scene, source.Root);
            Check(document.Objects?.Count == 2, "Capture must hold the root and its descendants only.");
            Check(document.Objects![0].ParentId is null && document.Objects[0].SiblingIndex == 0, "The prefab root must be parentless with sibling index 0.");
            var yaml = serializer.Serialize(source.Scene, source.Root, source.PrefabId);
            Check(yaml.Contains("version: 1") && yaml.Contains(source.PrefabId.ToString("D")) && !yaml.Contains("Outside"),
                "Prefab files must carry their own version and identity, not outside objects.");
            var parsed = PrefabSerializer.Parse(yaml);
            Check(parsed.Objects!.Single(item => item.ParentId is null).PrefabId == source.PrefabId,
                "Serialized prefabs must mark their root with the prefab ID.");
            // Parsed scalars lose their runtime types, so file stability means reaching a fixed point, not byte equality.
            var republished = PrefabSerializer.Serialize(parsed);
            Check(PrefabSerializer.Serialize(PrefabSerializer.Parse(republished)) == republished, "Prefab save/load must reach a stable form.");
            Reject(() => serializer.Serialize(source.Scene, source.Root, Guid.Empty), "Empty prefab IDs must be rejected.");
            Reject(() => serializer.Capture(source.Scene, new SceneObject("Foreign")), "Capture of a foreign root must be rejected.");
            Reject(() => serializer.Capture(new Scene(), source.Root), "Capture from a scene that does not own the root must be rejected.");

            // Placement into a scene that already holds the external target preserves the outside link.
            var target = new SceneSerializer(registry, assets).Deserialize(new SceneSerializer(registry, assets).Serialize(source.Scene));
            var targetRoot = target.Objects.Single(item => item.Name == "Enemy");
            var targetOutside = target.Objects.Single(item => item.Name == "Outside");
            target.Remove(targetRoot);
            var placed = serializer.Instantiate(target, parsed, out var membersChanged);
            Check(!membersChanged, "A clean prefab must not report member changes.");
            Check(placed.Name == "Enemy" && placed.Parent is null && target.Objects.Count == 3, "Placement must append a root copy.");
            var placedHolder = placed.GetComponent<PrefabHolder>()!;
            var placedChild = placed.Children.Single();
            var placedPart = placedChild.GetComponent<PrefabPart>()!;
            Check(placedChild.Name == "Gun" && placedPart.Value == 42 && placedPart.Name == "Cannon", "Values and names must survive.");
            Check(placed.Id != source.Root.Id && placedChild.Id != source.Child.Id, "Placed objects need fresh IDs.");
            Check(placed.PrefabId == parsed.Id, "Placed prefab roots must remember their source prefab ID.");
            Check(placedChild.PrefabId is null, "Placed prefab children must not carry the source marker.");
            Check(ReferenceEquals(placedHolder.Single, placedPart) && ReferenceEquals(placedHolder.Owner, placedChild),
                "Internal references must reconnect to the copies.");
            Check(ReferenceEquals(placedHolder.Self, placedHolder), "Self references must point at the copy.");
            Check(placedHolder.Tags.Length == 1 && ReferenceEquals(placedHolder.Tags[0], placedPart), "Array references must reconnect.");
            Check(ReferenceEquals(placedHolder.Map["main"], placedPart), "Dictionary references must reconnect.");
            Check(ReferenceEquals(placedHolder.Config!.Primary, placedPart) && ReferenceEquals(placedHolder.Config.Owner, placedChild)
                && placedHolder.Config.Extras.Count == 1 && ReferenceEquals(placedHolder.Config.Extras[0], placedPart),
                "Nested references must reconnect.");
            Check(ReferenceEquals(placedHolder.Peer, targetOutside.GetComponent<PrefabHolder>()!), "Outside references must keep the destination target.");
            Check(ReferenceEquals(placedHolder.Weapon, target.DataAssets.Get<PrefabWeapon>(source.WeaponId)), "Asset references must resolve from the destination snapshot.");
            var placedLifecycle = placed.GetComponent<PrefabLifecycle>()!;
            Check(placedLifecycle.Hp == 100 && placed.GetStartPriority(placedLifecycle) == 5
                && placed.GetUpdatePriority(placedLifecycle) == -3 && placed.GetDestroyPriority(placedLifecycle) == 2,
                "Priorities must survive prefab placement.");
            var yamlTarget = new SceneSerializer(registry, assets).Serialize(target);
            Check(yamlTarget.Contains(targetOutside.Id.ToString("D")), "Outside links must persist through scene saves.");
            var reloadedTarget = new SceneSerializer(registry, assets).Deserialize(yamlTarget);
            Check(reloadedTarget.Objects.Single(item => item.Id == placed.Id).PrefabId == parsed.Id,
                "Scene saves must persist placed prefab markers.");
            Reject(() => placed.PrefabId = Guid.Empty, "Empty prefab IDs must be rejected.");

            // Placement under a parent lands as its last child with the subtree order intact.
            var shelter = target.AddEmpty();
            shelter.Rename("Shelter");
            var nested = serializer.Instantiate(target, parsed, shelter);
            Check(ReferenceEquals(nested.Parent, shelter) && shelter.Children.Count == 1, "Parented placement must land as the last child.");
            Check(nested.Children.Single().Name == "Gun", "Subtree order must survive parented placement.");
            Reject(() => serializer.Instantiate(target, parsed, source.Root), "A parent from another scene must be rejected.");

            // Duplicating inside the source scene remaps the inside and keeps the outside.
            var before = source.Scene.Objects.Count;
            var copy = serializer.Instantiate(source.Scene, parsed);
            Check(source.Scene.Objects.Count == before + 2 && ReferenceEquals(copy.GetComponent<PrefabHolder>()!.Peer, source.OutsideHolder),
                "Same-scene duplication must remap inside references and keep outside ones.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void ExternalAndMissing()
    {
        var registry = Registry();
        var directory = Path.Combine(Path.GetTempPath(), "PureEngine-PrefabMissing-" + Guid.NewGuid().ToString("N"));
        try
        {
            var assets = WriteWeaponAsset(registry, directory, 17);
            var source = BuildSource(registry, assets);
            var serializer = new PrefabSerializer(registry);
            var outsideId = source.Outside.Id;
            var outsideHolderId = source.Outside.GetComponentId(source.OutsideHolder);
            source.Scene.Remove(source.Outside);
            Check(source.Holder.Peer is null, "Removal must null the peer slot.");
            var yaml = serializer.Serialize(source.Scene, source.Root, source.PrefabId);
            Check(yaml.Contains(outsideHolderId.ToString("D")), "Missing IDs must survive prefab files.");
            var fresh = new SceneSerializer(registry, assets).Deserialize("version: 3\nobjects: []");
            var placed = serializer.Instantiate(fresh, yaml, out _);
            var placedHolder = placed.GetComponent<PrefabHolder>()!;
            var ownerId = placed.GetComponentId(placedHolder);
            Check(placedHolder.Peer is null, "Missing peers must resolve to null.");
            Check(fresh.References.TryGetMissing(ownerId, "Peer", out var missing) && missing == outsideHolderId,
                "Missing peer IDs must be kept on the copy.");
            var freshYaml = new SceneSerializer(registry, assets).Serialize(fresh);
            Check(freshYaml.Contains(outsideHolderId.ToString("D")) && !freshYaml.Contains(outsideId.ToString("D")),
                "Missing peer IDs must persist through scene saves.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void Tolerance()
    {
        var registry = Registry();
        var directory = Path.Combine(Path.GetTempPath(), "PureEngine-PrefabTolerance-" + Guid.NewGuid().ToString("N"));
        try
        {
            var assets = WriteWeaponAsset(registry, directory, 17);
            var source = BuildSource(registry, assets);
            var serializer = new PrefabSerializer(registry);
            var yaml = serializer.Serialize(source.Scene, source.Root, source.PrefabId);
            Scene Fresh() => new SceneSerializer(registry, assets).Deserialize("version: 3\nobjects: []");

            var renamed = serializer.Instantiate(Fresh(), yaml.Replace("Name: Cannon", "OldName: Cannon"), out var renamedChanged);
            Check(renamedChanged, "Renamed members must report changes.");
            Check(renamed.Children.Single().GetComponent<PrefabPart>()!.Name == "Cannon", "Old names must restore into current members.");

            var extra = serializer.Instantiate(Fresh(), yaml.Replace("Value: 42", "Value: 42\n      Extra: 1"), out var extraChanged);
            Check(extraChanged, "Unknown members must report changes.");
            Check(extra.Children.Single().GetComponent<PrefabPart>()!.Value == 42, "Unknown members must be ignored.");

            var dropped = serializer.Instantiate(Fresh(), yaml.Replace("Value: 42", "# Value omitted"), out var droppedChanged);
            Check(droppedChanged, "Dropped members must report changes.");
            Check(dropped.Children.Single().GetComponent<PrefabPart>()!.Value == 0, "Dropped members must keep initializers.");

            Reject(() => serializer.Instantiate(Fresh(), yaml.Replace("Value: 42", "Value: wrong"), out _), "Mistyped values must be rejected.");
            Reject(() => serializer.Instantiate(Fresh(), yaml.Replace("checks.prefab-part", "missing.type"), out _), "Unknown component types must be rejected.");
            var partId = source.Child.GetComponentId(source.ChildPart);
            Reject(() => serializer.Instantiate(Fresh(), yaml.Replace($"ref: {partId:D}", "Value: 1"), out _),
                "Inline values in reference slots must be rejected.");
            var newline = Environment.NewLine;
            var prioritized = yaml.Replace("typeId: checks.prefab-part",
                $"typeId: checks.prefab-part{newline}    priorities:{newline}      start: 1");
            Reject(() => serializer.Instantiate(Fresh(), prioritized, out _), "Priorities on members without lifecycles must be rejected.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void StructureRejections()
    {
        var registry = Registry();
        var serializer = new PrefabSerializer(registry);
        var objectId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var componentId = Guid.NewGuid();
        PrefabDocument Valid() => new()
        {
            Version = 1,
            Id = Guid.NewGuid(),
            Objects =
            [
                new SceneObjectDocument
                {
                    Id = objectId, Name = "Root", ParentId = null, SiblingIndex = 0,
                    Components =
                    [
                        new ComponentDocument
                        {
                            Id = componentId, TypeId = "checks.prefab-part",
                            Values = new Dictionary<string, object?> { ["Value"] = 1, ["Name"] = "R" },
                        },
                    ],
                },
                new SceneObjectDocument
                {
                    Id = childId, Name = "Child", ParentId = objectId, SiblingIndex = 0, Components = [],
                },
            ],
        };
        var target = new Scene();
        Check(serializer.Instantiate(target, Valid()) is not null, "A minimal prefab must place.");
        Reject(() => PrefabSerializer.Parse("version: 99\nobjects: []\n"), "Future prefab versions must be rejected.");
        Reject(() => PrefabSerializer.Serialize(new PrefabDocument { Version = 1, Objects = [] }), "Empty prefabs must be rejected.");
        PrefabDocument Mutate(Func<PrefabDocument, PrefabDocument> mutate) => mutate(Valid());
        Reject(() => PrefabSerializer.Serialize(Mutate(document => { document.Id = Guid.Empty; return document; })), "Empty prefab IDs must be rejected.");
        Reject(() => PrefabSerializer.Serialize(Mutate(document => { document.Objects![1].ParentId = null; return document; })), "Multiple roots must be rejected.");
        Reject(() => PrefabSerializer.Serialize(Mutate(document => { document.Objects![1].ParentId = Guid.NewGuid(); return document; })), "Missing parents must be rejected.");
        Reject(() => PrefabSerializer.Serialize(Mutate(document => { document.Objects![0].ParentId = childId; return document; })), "Parent cycles must be rejected.");
        Reject(() => PrefabSerializer.Serialize(Mutate(document => { document.Objects![0].ParentId = objectId; return document; })), "Self parents must be rejected.");
        Reject(() => PrefabSerializer.Serialize(Mutate(document => { document.Objects![1].SiblingIndex = 7; return document; })), "Sibling gaps must be rejected.");
        Reject(() => PrefabSerializer.Serialize(Mutate(document => { document.Objects![0].SiblingIndex = 1; return document; })), "Non-zero root siblings must be rejected.");
        Reject(() => PrefabSerializer.Serialize(Mutate(document => { document.Objects![1].Id = objectId; return document; })), "Duplicate object IDs must be rejected.");
        Reject(() => PrefabSerializer.Serialize(Mutate(document => { document.Objects![0].Components![0].Id = objectId; return document; })),
            "Component IDs colliding with object IDs must be rejected.");
        Reject(() => PrefabSerializer.Serialize(Mutate(document =>
        {
            document.Objects![1].Components!.Add(new ComponentDocument { Id = componentId, TypeId = "checks.prefab-part", Values = [] });
            return document;
        })), "Duplicate component IDs must be rejected.");
        Reject(() => PrefabSerializer.Serialize(Mutate(document =>
        {
            document.Objects![0].Components![0].Priorities = new Dictionary<string, object?> { ["start"] = 1, ["bogus"] = 2 };
            return document;
        })), "Unknown priorities must be rejected.");
        Reject(() => serializer.Instantiate(new Scene(), Valid(), new SceneObject("Foreign")), "Foreign parents must be rejected.");
    }

    private static void Rollback()
    {
        var registry = Registry();
        var directory = Path.Combine(Path.GetTempPath(), "PureEngine-PrefabRollback-" + Guid.NewGuid().ToString("N"));
        try
        {
            var assets = WriteWeaponAsset(registry, directory, 17);
            var source = BuildSource(registry, assets);
            var serializer = new PrefabSerializer(registry);
            var yaml = serializer.Serialize(source.Scene, source.Root, source.PrefabId);
            var before = PrefabSerializer.Serialize(PrefabSerializer.Parse(PrefabSerializer.Serialize(PrefabSerializer.Parse(yaml))));
            Scene Fresh() => new SceneSerializer(registry, assets).Deserialize("version: 3\nobjects: []");
            var broken = Fresh();
            Reject(() => serializer.Instantiate(broken, yaml.Replace("checks.prefab-holder", "missing.type"), out _), "Unknown types must fail placement.");
            Check(broken.Objects.Count == 0 && broken.References.MissingCount == 0, "Failed placement must leave the destination untouched.");
            var factoryBroken = Fresh();
            Reject(() => serializer.Instantiate(factoryBroken, PrefabSerializer.Parse(yaml), out _, null, _ => throw new InvalidOperationException("no factory")),
                "Factory failures must fail placement.");
            Check(factoryBroken.Objects.Count == 0, "Factory failures must roll back created objects.");
            Check(PrefabSerializer.Serialize(PrefabSerializer.Parse(yaml)) == before, "Placement must not mutate the prefab document.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void CatalogScan()
    {
        var registry = Registry();
        var directory = Path.Combine(Path.GetTempPath(), "PureEngine-PrefabCatalog-" + Guid.NewGuid().ToString("N"));
        try
        {
            var assets = WriteWeaponAsset(registry, directory, 17);
            var source = BuildSource(registry, assets);
            var serializer = new PrefabSerializer(registry);
            var firstId = Guid.NewGuid();
            var secondId = Guid.NewGuid();
            File.WriteAllText(Path.Combine(directory, "First.pure.prefab.yaml"), serializer.Serialize(source.Scene, source.Root, firstId));
            File.WriteAllText(Path.Combine(directory, "Second.pure.prefab.yaml"), serializer.Serialize(source.Scene, source.Child, secondId));
            File.WriteAllText(Path.Combine(directory, "Broken.pure.prefab.yaml"), "version: 1\nobjects: []\n");
            File.WriteAllText(Path.Combine(directory, "Dupe.pure.prefab.yaml"), serializer.Serialize(source.Scene, source.Root, firstId));
            var catalog = PrefabCatalog.ScanFolder(directory, out var diagnostics);
            Check(catalog.Find(firstId) is null && catalog.Find(secondId) is not null, "Duplicate prefab IDs must exclude every copy.");
            Check(diagnostics.Count == 2, "Invalid and duplicated prefab files must be diagnosed.");
            Check(PrefabCatalog.ScanFolder(Path.Combine(directory, "Missing"), out var emptyDiagnostics).Ids.Count == 0
                && emptyDiagnostics.Count == 0, "Missing prefab folders must scan empty.");
            var target = new SceneSerializer(registry, assets).Deserialize("version: 3\nobjects: []");
            Check(serializer.Instantiate(target, catalog.Find(secondId)!) is not null, "Catalog documents must place.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void SpawnerBinding()
    {
        var registry = Registry();
        var spawner = new PrefabSpawner();
        Check(!spawner.IsBound, "Spawners start unbound.");
        Reject(() => spawner.Spawn(Guid.NewGuid()), "Unbound spawners must reject Spawn.");
        var scene = new Scene();
        spawner.Bind(scene, registry, null, null);
        Check(spawner.IsBound, "Bind must mark the spawner bound.");
        Reject(() => spawner.Spawn(Guid.Empty), "Empty prefab IDs must be rejected.");
        Reject(() => spawner.Spawn(Guid.NewGuid()), "Unknown prefab IDs must be rejected.");
    }

    private static void PlaySpawning()
    {
        var registry = Registry();
        var directory = Path.Combine(Path.GetTempPath(), "PureEngine-PrefabPlay-" + Guid.NewGuid().ToString("N"));
        try
        {
            var assets = WriteWeaponAsset(registry, directory, 23);
            var source = BuildSource(registry, assets);
            var serializer = new PrefabSerializer(registry);
            var prefabId = Guid.NewGuid();
            File.WriteAllText(Path.Combine(directory, "Enemy.pure.prefab.yaml"), serializer.Serialize(source.Scene, source.Root, prefabId));
            var catalog = PrefabCatalog.ScanFolder(directory, out var diagnostics);
            Check(diagnostics.Count == 0 && catalog.Find(prefabId) is not null, "The prefab catalog must load the enemy.");
            var spawnScene = new Scene();
            var host = spawnScene.AddEmpty();
            host.Rename("Spawner");
            host.Attach(new SpawnerProbe(new PrefabSpawner())
            {
                Template = (PrefabLifecycle)spawnScene.Prefabs.Assign(catalog.Find(prefabId)!, typeof(PrefabLifecycle), registry, assets: assets),
                SpawnInUpdate = 1,
            });
            for (var run = 0; run < 2; run++)
            {
                using var play = PlaySession.Prepare(spawnScene, registry, services =>
                {
                    services.AddScoped<PrefabSpawner>();
                    services.AddSingleton(catalog);
                    services.AddSingleton(DataAssetStore.ScanFolder(directory, registry, out _));
                });
                var runTemplate = play.Runtime.Scene.Objects.Single().GetComponent<SpawnerProbe>()!.Template!;
                play.Start();
                play.Step(0.1f);
                play.Step(0.1f);
                var enemies = play.Runtime.Scene.Objects.Where(item => item.Name == "Enemy").ToArray();
                Check(enemies.Length == 2, "Start and Update spawns must each place one enemy.");
                foreach (var enemy in enemies)
                {
                    var holder = enemy.GetComponent<PrefabHolder>()!;
                    var gun = enemy.Children.Single();
                    Check(ReferenceEquals(holder.Single, gun.GetComponent<PrefabPart>()!), "Spawned copies must reconnect inside.");
                    Check(holder.Weapon?.Attack == 23, "Spawned copies must resolve Play snapshot assets.");
                    Check(!ReferenceEquals(holder.Weapon, assets.Get<PrefabWeapon>(source.WeaponId)), "Play assets must not leak editing instances.");
                    Check(enemy.GetComponent<PrefabLifecycle>()!.Starts == 1, "Spawned lifecycles must start on the next frame.");
                }
                var guns = play.Runtime.Scene.Objects.Where(item => item.Name == "Gun").ToArray();
                Check(guns.Length == 2 && guns.All(gun => gun.Parent is not null), "Spawned children must keep their parents.");
                Check(runTemplate.Starts == 0 && runTemplate.Destroys == 0, "Prefab templates must never run lifecycle callbacks.");
                play.Stop();
                Check(runTemplate.Disposed == 1 && runTemplate.Destroys == 0, "Stopping must dispose template resources exactly once without Destroy.");
            }
            Check(assets.Get<PrefabWeapon>(source.WeaponId).Attack == 23, "Play runs must not mutate editing assets.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    public sealed class PrefabPart
    {
        [Inspector] public int Value { get; set; }
        [Inspector, FormerlySerializedAs("OldName")] public string Name { get; set; } = "";
    }

    public sealed class PrefabConfig
    {
        [Inspector] public PrefabPart? Primary { get; set; }
        [Inspector] public SceneObject? Owner { get; set; }
        [Inspector] public List<PrefabPart?> Extras { get; set; } = [];
    }

    public sealed class PrefabHolder
    {
        [Inspector] public PrefabPart? Single { get; set; }
        [Inspector] public SceneObject? Owner { get; set; }
        [Inspector] public PrefabPart?[] Tags { get; set; } = [];
        [Inspector] public Dictionary<string, PrefabPart?> Map { get; set; } = [];
        [Inspector] public PrefabHolder? Self { get; set; }
        [Inspector] public PrefabHolder? Peer { get; set; }
        [Inspector] public PrefabConfig? Config { get; set; }
        [Inspector] public PrefabWeapon? Weapon { get; set; }
    }

    public sealed class PrefabLifecycle : IDisposable
    {
        [Inspector] public int Hp { get; set; }
        public int Starts;
        public int Updates;
        public int Destroys;
        public int Disposed;
        public void Dispose() => Disposed++;
#pragma warning disable CA1822 // Reflection tests require these lifecycle members to remain instance members.
        [Start] private void OnStart() => Starts++;
        [Update] private void Tick() => Updates++;
        [Destroy] private void OnDestroy() => Destroys++;
#pragma warning restore CA1822
    }

    public sealed class SpawnerProbe(PrefabSpawner spawner)
    {
        public PrefabSpawner Spawner { get; } = spawner;
        [Inspector] public PrefabLifecycle? Template { get; set; }
        [Inspector] public int SpawnInUpdate { get; set; }
#pragma warning disable CA1822 // Reflection tests require these lifecycle members to remain instance members.
        [Start] private void OnStart() => Spawner.Instantiate(Template!);
        [Update] private void Tick()
        {
            if (SpawnInUpdate == 1)
            {
                SpawnInUpdate = 2;
                Spawner.Instantiate(Template!);
            }
        }
#pragma warning restore CA1822
    }
}

[DataAsset]
public sealed class PrefabWeapon
{
    [Inspector] public int Attack { get; set; } = 5;
}