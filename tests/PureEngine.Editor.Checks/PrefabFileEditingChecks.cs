using PureEngine.Core;
using PureEngine.Editor;

static class PrefabFileEditingChecks
{
    public static void Run(string root)
    {
        var directory = Path.Combine(root, "PrefabFileEditing");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Editable.pure.prefab.yaml");
        var externalPath = Path.Combine(directory, "External.pure.prefab.yaml");
        var registry = new ComponentRegistry();
        registry.Register<PrefabFilePart>("checks.prefab-file-part");
        registry.Register<PrefabFileLinks>("checks.prefab-file-links");
        var external = new Scene();
        var externalRoot = external.AddEmpty();
        var externalPart = new PrefabFilePart { Value = 42 };
        externalRoot.Attach(externalPart);
        var externalId = PrefabFile.Create(externalPath, external, externalRoot, registry);
        var scene = new Scene();
        var owner = scene.AddEmpty();
        var child = scene.AddEmpty();
        child.SetParent(owner);
        var part = new PrefabFilePart { Value = 7 };
        child.Attach(part);
        var links = new PrefabFileLinks
        {
            Target = part,
            Child = child,
            External = (PrefabFilePart)scene.Prefabs.Assign(PrefabFile.Load(externalPath), typeof(PrefabFilePart), registry),
        };
        owner.Attach(links);
        var id = PrefabFile.Create(path, scene, owner, registry);
        var catalog = PrefabCatalog.ScanFolder(directory, out var diagnostics);
        Check(diagnostics.Count == 0, "Valid prefab files must enter the catalog.");
        var assets = new DataAssetStore();
        var created = 0;
        object factory(Type type)
        {
            created++;
            return Activator.CreateInstance(type)!;
        }
        var edit = PrefabFile.OpenForEditing(path, registry, out var openedId, out var changed, assets, catalog, factory);
        Check(openedId == id && !changed && created == 3 && ReferenceEquals(edit.DataAssets, assets),
            "Opening must preserve the prefab ID and pass through assets and the component factory.");
        AssertIdentities(edit, owner, child, part, links, externalId, externalRoot.GetComponentId(externalPart));
        edit.RootObjects.Single().Rename("Edited");
        edit.Objects.Single(item => item.Id == child.Id).GetComponent<PrefabFilePart>()!.Value = 9;
        PrefabFile.Save(path, edit, id, registry);
        var reopened = PrefabFile.OpenForEditing(path, registry, out openedId, out changed, prefabs: catalog);
        Check(openedId == id && !changed && reopened.RootObjects.Single().Name == "Edited"
            && reopened.Objects.Single(item => item.Id == child.Id).GetComponent<PrefabFilePart>()!.Value == 9,
            "Saved edits must survive reopening without member migration.");
        AssertIdentities(reopened, owner, child, part, links, externalId, externalRoot.GetComponentId(externalPart));
        Check(owner.Name != "Edited" && part.Value == 7, "Prefab editing must not mutate the source scene.");

        var original = File.ReadAllText(path);
        Reject(() => PrefabFile.Save(path, new Scene(), id, registry));
        var extra = edit.AddEmpty();
        Reject(() => PrefabFile.Save(path, edit, id, registry));
        edit.Remove(extra);
        Reject(() => PrefabFile.Save(path, edit, Guid.Empty, registry));
        Reject(() => PrefabFile.Save(path, edit, id, new ComponentRegistry()));
        Check(File.ReadAllText(path) == original && Directory.GetFiles(directory, "*.tmp").Length == 0,
            "Validation failures must leave the existing file intact without temporary files.");

        var malformedPath = Path.Combine(directory, "Malformed.pure.prefab.yaml");
        foreach (var yaml in new[]
        {
            "version: 999\nobjects: []\n",
            $"version: 1\nid: {id:D}\nobjects: []\n",
            original.Replace("checks.prefab-file-part", "checks.unknown", StringComparison.Ordinal),
            original.Replace($"id: {child.Id:D}", $"id: {owner.Id:D}", StringComparison.Ordinal),
        })
        {
            File.WriteAllText(malformedPath, yaml);
            Reject(() => PrefabFile.OpenForEditing(malformedPath, registry, out _, out _));
            Check(File.ReadAllText(malformedPath) == yaml && File.ReadAllText(path) == original,
                "Malformed prefab opens must not modify either file.");
        }
        var document = PrefabFile.Load(path);
        document.Objects!.Single(item => item.Id == child.Id).ParentId = null;
        Reject(() => new PrefabSerializer(registry).RestoreForEditing(document, out _));
        document = PrefabFile.Load(path);
        document.Objects!.Single(item => item.Id == owner.Id).Components!.Single().Values!.Remove(nameof(PrefabFileLinks.Child));
        File.WriteAllText(malformedPath, PrefabSerializer.Serialize(document));
        _ = PrefabFile.OpenForEditing(malformedPath, registry, out _, out changed, prefabs: catalog);
        Check(changed, "Opening must report added Inspector members.");
        Console.WriteLine("PASS: prefab editing identities, internal/external references, isolated edits, atomic saves, malformed files, and member migration.");
    }

    private static void AssertIdentities(Scene scene, SceneObject owner, SceneObject child, PrefabFilePart part,
        PrefabFileLinks links, Guid externalId, Guid externalComponentId)
    {
        var restoredOwner = scene.RootObjects.Single();
        var restoredChild = restoredOwner.Children.Single();
        var restoredPart = restoredChild.GetComponent<PrefabFilePart>()!;
        var restoredLinks = restoredOwner.GetComponent<PrefabFileLinks>()!;
        Check(scene.Objects.Count == 2 && restoredOwner.Id == owner.Id && restoredChild.Id == child.Id
            && restoredChild.GetComponentId(restoredPart) == child.GetComponentId(part)
            && restoredOwner.GetComponentId(restoredLinks) == owner.GetComponentId(links),
            "Editing must preserve every object and component identity, not instantiate remapped copies.");
        Check(ReferenceEquals(restoredLinks.Target, restoredPart) && ReferenceEquals(restoredLinks.Child, restoredChild),
            "Internal component and object references must resolve inside the editing scene.");
        Check(restoredLinks.External is { Value: 42 }
            && PrefabReferenceStore.TryGetIdentity(restoredLinks.External, out var identity)
            && identity == new PrefabReferenceStore.Identity(externalId, externalComponentId),
            "External prefab references must preserve their prefab and target IDs outside the hierarchy.");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        { return; }
        throw new InvalidOperationException("Invalid prefab editing operation must fail.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

public sealed class PrefabFilePart
{
    [Inspector] public int Value { get; set; }
}

public sealed class PrefabFileLinks
{
    [Inspector] public PrefabFilePart? Target { get; set; }
    [Inspector] public SceneObject? Child { get; set; }
    [Inspector] public PrefabFilePart? External { get; set; }
}
