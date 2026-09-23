using System.Numerics;
using PureEngine.Core;
using YamlDotNet.Core;

static class UiComponentChecks
{
    public static void Run()
    {
        SpriteRoundTrip();
        SpriteRejections();
        OrderRoundTrip();
        Requirements();
        ParentRoundTrip();
        ParentRejections();
        SiblingOrder();
        RemoveCascade();
        Console.WriteLine("PASS: Sprite YAML round-trip/clone, render Order save/clone, UI requirements, parent save/reopen, sibling order, and subtree removal.");
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
        throw new InvalidOperationException(message);
    }

    private static ComponentRegistry UiRegistry()
    {
        var registry = new ComponentRegistry();
        registry.Register<Transform>("core.transform");
        registry.Register<UiElement>("core.ui-element");
        registry.Register<Image>("core.image");
        registry.Register<SpriteProbe>("checks.sprite");
        return registry;
    }

    private static void SpriteRoundTrip()
    {
        var registry = UiRegistry();
        var serializer = new SceneSerializer(registry);
        var scene = new Scene();
        var item = scene.AddEmpty();
        item.Rename("Sprite holder");
        var id = Guid.NewGuid();
        var image = new Image { Sprite = new Sprite(id, (8, 4, 16, 12)), Color = new Color(1, 0.5f, 0.25f, 0.75f) };
        item.Attach(new Transform());
        item.Attach(new UiElement());
        item.Attach(image);
        var probeItem = scene.AddEmpty();
        probeItem.Rename("Probe");
        var probe = new SpriteProbe { MaybeSprite = null, Sprites = [new Sprite(id)] };
        probeItem.Attach(probe);

        var yaml = serializer.Serialize(scene);
        Check(yaml.Contains("version: 3") && yaml.Contains("imageId") && yaml.Contains("sourceRect"),
            "Sprite YAML must carry version 3 with imageId and sourceRect.");
        var restored = serializer.Deserialize(yaml);
        var copy = restored.Objects[0].GetComponent<Image>()!;
        Check(copy.Sprite is not null && copy.Sprite.ImageId == id && copy.Sprite.SourceRect == (8, 4, 16, 12),
            "Sprite whole/crop did not survive.");
        Check(copy.Color == new Color(1, 0.5f, 0.25f, 0.75f), "Image color did not survive.");
        Check(!ReferenceEquals(copy.Sprite, image.Sprite), "Restored Sprite must be a new instance.");
        var probeCopy = restored.Objects[1].GetComponent<SpriteProbe>()!;
        Check(probeCopy.MaybeSprite is null && probeCopy.Sprites.Count == 1 && probeCopy.Sprites[0].ImageId == id,
            "Sprite null and list entries did not survive.");
        Check(serializer.Serialize(restored) == yaml, "Sprite save/load changed output.");

        var legacyYaml = yaml.Replace("r: 1", "x: 1").Replace("g: 0.5", "y: 0.5")
            .Replace("b: 0.25", "z: 0.25").Replace("a: 0.75", "w: 0.75");
        Check(legacyYaml != yaml, "Legacy color fixture must differ from the current format.");
        var legacyScene = serializer.Deserialize(legacyYaml);
        Check(legacyScene.Objects[0].GetComponent<Image>()!.Color == image.Color,
            "Legacy Vector4 Image color must retain all channels.");
        Check(serializer.Serialize(legacyScene) == yaml, "Legacy Image colors must save as RGBA.");

        var clone = serializer.Clone(scene);
        var cloneImage = clone.Objects[0].GetComponent<Image>()!;
        Check(cloneImage.Sprite is not null && cloneImage.Sprite.ImageId == id
            && !ReferenceEquals(cloneImage.Sprite, image.Sprite), "Clone must separate Sprite instances.");
        image.Sprite = new Sprite(Guid.NewGuid());
        probe.Sprites[0] = new Sprite(Guid.NewGuid());
        Check(cloneImage.Sprite!.ImageId == id && clone.Objects[1].GetComponent<SpriteProbe>()!.Sprites[0].ImageId == id,
            "Editing the source after Clone must not leak into the clone.");
    }

    private static void SpriteRejections()
    {
        var registry = UiRegistry();
        var serializer = new SceneSerializer(registry);
        var scene = new Scene();
        var item = scene.AddEmpty();
        item.Attach(new Image { Sprite = new Sprite(Guid.NewGuid(), (0, 0, 8, 8)) });
        var yaml = serializer.Serialize(scene);
        Reject(() => serializer.Deserialize(yaml.Replace("imageId:", "imageId: not-a-guid")),
            "Invalid imageId accepted.");
        Reject(() => serializer.Deserialize(yaml.Replace("imageId:", "imageId: 00000000-0000-0000-0000-000000000000")),
            "Empty imageId accepted.");
        Reject(() => serializer.Deserialize(yaml.Replace("sourceRect", "sourceRect: {x: 0}")),
            "Malformed sourceRect accepted.");
        var wholeItem = new SceneObject("Whole");
        wholeItem.Attach(new Image { Sprite = new Sprite(Guid.NewGuid()) });
        var wholeScene = new Scene();
        // Attach order does not matter for validation; use a standalone scene for negative cases.
        Reject(() => _ = new Sprite(Guid.NewGuid(), (-1, 0, 1, 1)), "Negative sprite origin accepted.");
        Reject(() => _ = new Sprite(Guid.NewGuid(), (0, 0, 0, 1)), "Zero sprite width accepted.");
        Check(wholeItem.GetComponent<Image>()!.Sprite is not null, "Whole sprite setup failed.");
        Check(wholeScene.Objects.Count == 0, "Negative-case scene must stay empty.");
    }

    private static void OrderRoundTrip()
    {
        var registry = UiRegistry();
        var serializer = new SceneSerializer(registry);
        Check(ComponentSchema.GetInspectorMembers(typeof(Image)).Any(member => member.Name == "Order"),
            "Image must expose Order through the RendererComponent base.");
        Check(new Image().Order == 0, "New Images must start with Order 0.");
        var scene = new Scene();
        var item = scene.AddEmpty();
        item.Rename("Ordered");
        item.Attach(new Transform());
        item.Attach(new UiElement());
        var image = new Image { Sprite = new Sprite(Guid.NewGuid()), Order = -7 };
        item.Attach(image);
        var yaml = serializer.Serialize(scene);
        Check(yaml.Contains("Order:"), "Order must be saved through Inspector values.");
        var restored = serializer.Deserialize(yaml);
        Check(restored.Objects[0].GetComponent<Image>()!.Order == -7,
            "Negative Order did not survive save/load.");
        Check(serializer.Serialize(restored) == yaml, "Order save/load changed output.");
        var clone = serializer.Clone(scene);
        var cloneImage = clone.Objects[0].GetComponent<Image>()!;
        Check(cloneImage.Order == -7 && !ReferenceEquals(cloneImage, image),
            "Clone must carry Order to a separate Image instance.");
        image.Order = 4;
        Check(cloneImage.Order == -7, "Editing Order after Clone must not leak into the clone.");

        // Old scenes without Order keep the previous display as Order 0.
        var legacyYaml = string.Join("\n", yaml.Split('\n').Where(line => !line.Contains("Order:", StringComparison.Ordinal)));
        Check(!legacyYaml.Contains("Order:"), "Legacy YAML setup failed.");
        var legacy = serializer.Deserialize(legacyYaml, out var membersChanged);
        Check(legacy.Objects[0].GetComponent<Image>()!.Order == 0,
            "Old data without Order must load as Order 0.");
        Check(membersChanged, "Missing Order must be reported as added members.");
        Check(serializer.Serialize(legacy).Contains("Order:"), "Resaving legacy data must connect Order.");
    }

    private static void Requirements()
    {
        var imageOnly = new SceneObject("Image only");
        imageOnly.Attach(new Image());
        Check(UiComponentRequirements.GetMissing(imageOnly).SequenceEqual(["Transform", "UiElement"]),
            "Image alone must report Transform and UiElement.");
        imageOnly.Attach(new Transform());
        Check(UiComponentRequirements.GetMissing(imageOnly).SequenceEqual(["UiElement"]),
            "Image with Transform must still report UiElement.");
        imageOnly.Attach(new UiElement());
        Check(UiComponentRequirements.GetMissing(imageOnly).Count == 0,
            "Complete Image combination must clear the diagnostic.");

        var elementOnly = new SceneObject("Element only");
        elementOnly.Attach(new UiElement());
        Check(UiComponentRequirements.GetMissing(elementOnly).SequenceEqual(["Transform"]),
            "UiElement alone must report Transform.");
        elementOnly.Attach(new Transform());
        Check(UiComponentRequirements.GetMissing(elementOnly).Count == 0,
            "UiElement with Transform must clear the diagnostic.");

        var plain = new SceneObject("Plain");
        Check(UiComponentRequirements.GetMissing(plain).Count == 0, "Objects without UI must not report.");
        var transformOnly = new SceneObject("Transform only");
        transformOnly.Attach(new Transform());
        Check(UiComponentRequirements.GetMissing(transformOnly).Count == 0, "Transform alone must not report.");
    }

    private static void ParentRoundTrip()
    {
        var registry = UiRegistry();
        var serializer = new SceneSerializer(registry);
        var scene = new Scene();
        var parent = scene.AddEmpty();
        parent.Rename("Parent");
        parent.Attach(new Transform());
        parent.Attach(new UiElement());
        var child = scene.AddEmpty();
        child.Rename("Child");
        child.SetParent(parent);
        var childImageId = Guid.NewGuid();
        child.Attach(new Transform { LocalPosition = new Vector3(10, 20, 0) });
        child.Attach(new UiElement { SizeDelta = new Vector2(64, 32) });
        child.Attach(new Image { Sprite = new Sprite(childImageId), Color = new Color(0, 1, 0, 1) });
        var sibling = scene.AddEmpty();
        sibling.Rename("Sibling");
        sibling.SetParent(parent);
        sibling.Attach(new Transform());

        var yaml = serializer.Serialize(scene);
        Check(yaml.Contains("parentId") && yaml.Contains("siblingIndex"), "Parent YAML must carry parentId and siblingIndex.");
        var restored = serializer.Deserialize(yaml);
        Check(restored.Objects.Count == 3, "Parent round-trip lost objects.");
        var restoredParent = restored.Objects.First(item => item.Name == "Parent");
        var restoredChild = restored.Objects.First(item => item.Name == "Child");
        var restoredSibling = restored.Objects.First(item => item.Name == "Sibling");
        Check(ReferenceEquals(restoredChild.Parent, restoredParent)
            && ReferenceEquals(restoredSibling.Parent, restoredParent)
            && restoredParent.Children.Count == 2
            && ReferenceEquals(restoredParent.Children[0], restoredChild)
            && ReferenceEquals(restoredParent.Children[1], restoredSibling),
            "Parent links or sibling order did not survive.");
        Check(restoredChild.GetComponent<Image>()!.Sprite!.ImageId == childImageId
            && restoredChild.GetComponent<Transform>()!.LocalPosition == new Vector3(10, 20, 0),
            "Child component values did not survive with parents.");
        Check(restored.RootObjects.Count == 1 && ReferenceEquals(restored.RootObjects[0], restoredParent),
            "Root order did not survive.");
        Check(serializer.Serialize(restored) == yaml, "Parent save/load changed output.");

        var clone = serializer.Clone(scene);
        var cloneParent = clone.Objects.First(item => item.Name == "Parent");
        var cloneChild = clone.Objects.First(item => item.Name == "Child");
        Check(ReferenceEquals(cloneChild.Parent, cloneParent) && !ReferenceEquals(cloneChild, child)
            && !ReferenceEquals(cloneParent, parent), "Clone must resolve parents to clone targets.");
        Check(!ReferenceEquals(cloneChild.GetComponent<Image>()!.Sprite, child.GetComponent<Image>()!.Sprite),
            "Clone must separate Sprite references.");
    }

    private static void ParentRejections()
    {
        var registry = UiRegistry();
        var serializer = new SceneSerializer(registry);
        var scene = new Scene();
        var parent = scene.AddEmpty();
        parent.Rename("Parent");
        var child = scene.AddEmpty();
        child.Rename("Child");
        child.SetParent(parent);
        var sibling = scene.AddEmpty();
        sibling.Rename("Sibling");
        sibling.SetParent(parent);
        var yaml = serializer.Serialize(scene);
        var childId = child.Id.ToString("D");
        var parentId = parent.Id.ToString("D");
        Reject(() => serializer.Deserialize(yaml.Replace($"parentId: {parentId}", $"parentId: {Guid.NewGuid():D}")),
            "Missing parent accepted.");
        Reject(() => serializer.Deserialize(yaml.Replace($"parentId: {parentId}", $"parentId: {childId}")),
            "Self parent accepted.");
        Reject(() => serializer.Deserialize(yaml.Replace("siblingIndex: 0", "siblingIndex: 5")),
            "Out-of-range siblingIndex accepted.");
        var lastIndex = yaml.LastIndexOf("siblingIndex: 1", StringComparison.Ordinal);
        Check(lastIndex >= 0, "Sibling test setup failed.");
        var duplicated = yaml[..lastIndex] + "siblingIndex: 0" + yaml[(lastIndex + "siblingIndex: 1".Length)..];
        Reject(() => serializer.Deserialize(duplicated),
            "Duplicate siblingIndex accepted.");
        Reject(() => serializer.Deserialize(yaml.Replace("version: 3", "version: 99")),
            "Future scene version accepted.");
        Reject(() => serializer.Deserialize(yaml.Replace("version: 3", "version: 1")),
            "Version 1 with parent fields accepted.");
    }

    private static void SiblingOrder()
    {
        var scene = new Scene();
        var parent = scene.AddEmpty();
        parent.Rename("Parent");
        var first = scene.AddEmpty();
        first.Rename("First");
        first.SetParent(parent);
        var second = scene.AddEmpty();
        second.Rename("Second");
        second.SetParent(parent);
        Check(parent.Children[0] == first && parent.Children[1] == second, "Sibling setup failed.");
        second.SetSiblingIndex(0);
        Check(parent.Children[0] == second && parent.Children[1] == first, "SetSiblingIndex did not reorder.");
        var registry = UiRegistry();
        var serializer = new SceneSerializer(registry);
        var restored = serializer.Deserialize(serializer.Serialize(scene));
        var restoredParent = restored.Objects.First(item => item.Name == "Parent");
        Check(restoredParent.Children[0].Name == "Second" && restoredParent.Children[1].Name == "First",
            "Reordered siblings did not survive.");
        Reject(() => first.SetSiblingIndex(5), "Out-of-range sibling index accepted.");
    }

    private static void RemoveCascade()
    {
        var scene = new Scene();
        var parent = scene.AddEmpty();
        parent.Rename("Parent");
        var child = scene.AddEmpty();
        child.Rename("Child");
        child.SetParent(parent);
        var grandchild = scene.AddEmpty();
        grandchild.Rename("Grandchild");
        grandchild.SetParent(child);
        var other = scene.AddEmpty();
        other.Rename("Other");
        Check(scene.Remove(parent) && scene.Objects.Count == 1 && scene.Objects[0] == other,
            "Removing a parent must delete its descendants at once.");
        Check(parent.Children.Count == 1 && child.Children.Count == 1,
            "Removed tree links are retained on the detached objects.");
    }

    public sealed class SpriteProbe
    {
        [Inspector] public Sprite? MaybeSprite { get; set; }
        [Inspector] public List<Sprite> Sprites { get; set; } = [];
    }
}
