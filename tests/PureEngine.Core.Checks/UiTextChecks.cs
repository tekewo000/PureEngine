using System.Numerics;
using PureEngine.Core;

static class UiTextChecks
{
    public static void Run()
    {
        TextRoundTrip();
        TextRequirements();
        TextRenderOrder();
        Console.WriteLine("PASS: Text defaults, save/clone, requirements, and shared render Order.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static ComponentRegistry TextRegistry()
    {
        var registry = new ComponentRegistry();
        registry.Register<Transform>("core.transform");
        registry.Register<UiElement>("core.ui-element");
        registry.Register<Image>("core.image");
        registry.Register<Text>("core.text");
        return registry;
    }

    private static void TextRoundTrip()
    {
        var registry = TextRegistry();
        var serializer = new SceneSerializer(registry);
        Check(ComponentSchema.GetInspectorMembers(typeof(Text)).Any(member => member.Name == "Order"),
            "Text must expose Order through the RendererComponent base.");
        var fresh = new Text();
        Check(fresh.Content == "New Text" && fresh.Color == Color.White
            && fresh.FontSize == 24f && fresh.LineSpacing == 1.2f && fresh.Order == 0,
            "New Text must start with visible content, white color, and Order 0.");
        var scene = new Scene();
        var item = scene.AddEmpty();
        item.Rename("Label");
        item.Attach(new Transform());
        item.Attach(new UiElement());
        var text = new Text { Content = "Score: 12", Color = new Color(1, 0.5f, 0.25f, 0.75f), FontSize = 20f, LineSpacing = 1.5f, Order = 3 };
        item.Attach(text);
        var yaml = serializer.Serialize(scene);
        Check(yaml.Contains("core.text") && yaml.Contains("Content:") && yaml.Contains("FontSize:") && yaml.Contains("Order:"),
            "Text save must carry typeId, content, size, and Order.");
        var restored = serializer.Deserialize(yaml);
        var copy = restored.Objects[0].GetComponent<Text>()!;
        Check(copy.Content == "Score: 12" && copy.Color == new Color(1, 0.5f, 0.25f, 0.75f)
            && copy.FontSize == 20f && copy.LineSpacing == 1.5f && copy.Order == 3,
            "Text values did not survive save/load.");
        Check(serializer.Serialize(restored) == yaml, "Text save/load changed output.");

        var clone = serializer.Clone(scene);
        var cloneText = clone.Objects[0].GetComponent<Text>()!;
        Check(cloneText.Content == "Score: 12" && cloneText.Order == 3 && !ReferenceEquals(cloneText, text),
            "Clone must carry Text values to a separate instance.");
        text.Content = "Changed";
        text.Order = -1;
        Check(cloneText.Content == "Score: 12" && cloneText.Order == 3,
            "Editing the source after Clone must not leak into the clone.");

        // Remove whole member nodes, including Color's nested channels.
        var document = new YamlDotNet.RepresentationModel.YamlStream();
        document.Load(new StringReader(yaml));
        var root = (YamlDotNet.RepresentationModel.YamlMappingNode)document.Documents[0].RootNode;
        var objects = (YamlDotNet.RepresentationModel.YamlSequenceNode)root.Children["objects"];
        var itemNode = (YamlDotNet.RepresentationModel.YamlMappingNode)objects.Children[0];
        var componentNodes = (YamlDotNet.RepresentationModel.YamlSequenceNode)itemNode.Children["components"];
        var textNode = componentNodes.Children.Cast<YamlDotNet.RepresentationModel.YamlMappingNode>()
            .Single(node => node.Children["typeId"].ToString() == "core.text");
        ((YamlDotNet.RepresentationModel.YamlMappingNode)textNode.Children["values"]).Children.Clear();
        using var writer = new StringWriter();
        document.Save(writer, false);
        var legacyYaml = writer.ToString();
        Check(legacyYaml != yaml, "Legacy YAML setup must drop Text members.");
        var legacy = serializer.Deserialize(legacyYaml, out var membersChanged);
        var legacyText = legacy.Objects[0].GetComponent<Text>()!;
        Check(legacyText.Content == "New Text" && legacyText.FontSize == 24f && legacyText.Order == 0
            && legacyText.Color == Color.White && legacyText.LineSpacing == 1.2f,
            "Old data without Text members must load readable defaults.");
        Check(membersChanged, "Missing Text members must be reported as added members.");
        Check(serializer.Serialize(legacy).Contains("Content:"), "Resaving legacy data must connect Text members.");
    }

    private static void TextRequirements()
    {
        var lone = new SceneObject("Lone");
        lone.Attach(new Text());
        Check(UiComponentRequirements.GetMissing(lone).SequenceEqual([nameof(Transform), nameof(UiElement)]),
            "Text alone must require Transform and UiElement.");
        lone.Attach(new Transform());
        Check(UiComponentRequirements.GetMissing(lone).SequenceEqual([nameof(UiElement)]),
            "Text with Transform must still require UiElement.");
        lone.Attach(new UiElement());
        Check(UiComponentRequirements.GetMissing(lone).Count == 0,
            "Text with Transform and UiElement must be complete without an Image.");
    }

    private static void TextRenderOrder()
    {
        var plain = new SceneObject("Plain");
        Check(SceneViewMath.GetRenderOrder(plain) == 0, "Objects without visuals must sort as Order 0.");
        var imageOnly = new SceneObject("Image only");
        imageOnly.Attach(new Image { Order = 2 });
        Check(SceneViewMath.GetRenderOrder(imageOnly) == 2, "Image-only Order must survive.");
        var textOnly = new SceneObject("Text only");
        textOnly.Attach(new Text { Order = -4 });
        Check(SceneViewMath.GetRenderOrder(textOnly) == -4, "Text-only Order must survive, including negatives.");
        var combined = new SceneObject("Combined");
        combined.Attach(new Image { Order = 2 });
        combined.Attach(new Text { Order = 5 });
        Check(SceneViewMath.GetRenderOrder(combined) == 5,
            "Image and Text on one object must sort by the larger Order.");
        combined.GetComponent<Text>()!.Order = -6;
        Check(SceneViewMath.GetRenderOrder(combined) == 2,
            "Lowering Text below Image must keep the larger Image Order.");

        var scene = new Scene();
        var back = scene.AddEmpty();
        back.Rename("Back");
        back.Attach(new Transform());
        back.Attach(new UiElement());
        back.Attach(new Text { Order = 1 });
        var front = scene.AddEmpty();
        front.Rename("Front");
        front.Attach(new Transform());
        front.Attach(new UiElement());
        front.Attach(new Image { Order = 4 });
        var ordered = SceneViewMath.SortForRender(SceneViewMath.EnumerateLayouts(scene, new Vector2(400, 200)));
        Check(ordered.Count == 2 && ReferenceEquals(ordered[0].Object, back) && ReferenceEquals(ordered[1].Object, front),
            "Text and Image must share one Order sequence.");
    }
}