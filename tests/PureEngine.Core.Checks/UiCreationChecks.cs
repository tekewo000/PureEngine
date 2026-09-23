using PureEngine.Core;
using PureEngine.Core.Components;
using PureEngine.Editor;

/// <summary>
/// Stuffsの右クリックメニュー「UI」から作れるImage／ButtonのCore側の確認。
/// 登録・名前付け・アタッチ・YAML往復を画面なしで確かめる。
/// </summary>
static class UiCreationChecks
{
    public static void Run()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        var registry = ComponentAssets.Registry;
        Check(registry.GetType("core.image") == typeof(Image), "core.image must resolve to the Image component.");
        Check(registry.GetType("core.button") == typeof(Button), "core.button must resolve to the Button component.");
        Check(registry.GetId(typeof(Image)) == "core.image", "Image must keep the core.image typeId.");
        Check(registry.GetId(typeof(Button)) == "core.button", "Button must keep the core.button typeId.");

        var inspectorImage = ComponentSchema.GetInspectorMembers(typeof(Image)).Select(m => m.Name).ToArray();
        Check(inspectorImage.Contains("SpritePath") && inspectorImage.Contains("Color")
            && inspectorImage.Contains("Width") && inspectorImage.Contains("Height"),
            "Image Inspector members are missing.");
        var inspectorButton = ComponentSchema.GetInspectorMembers(typeof(Button)).Select(m => m.Name).ToArray();
        Check(inspectorButton.Contains("Text") && inspectorButton.Contains("Interactable"),
            "Button Inspector members are missing.");

        var scene = new Scene();
        var imageItem = scene.AddNamed("Image");
        var buttonItem = scene.AddNamed("Button");
        Check(imageItem.Name == "Image" && buttonItem.Name == "Button", "AddNamed must use the base name.");
        Check(scene.AddNamed("Image").Name == "Image (1)" && scene.AddNamed("Button").Name == "Button (1)",
            "UI names must not collide.");
        Check(scene.AddEmpty().Name == "Empty", "AddEmpty must keep the Empty default name.");
        try
        {
            scene.AddNamed("   ");
            throw new InvalidOperationException("Blank UI base names must be rejected.");
        }
        catch (ArgumentException) { }

        Check(ComponentAssets.TryAttach(imageItem, typeof(Image)), "Image must attach to its object.");
        Check(!ComponentAssets.TryAttach(imageItem, typeof(Image)) && imageItem.Components.Count == 1,
            "Repeated Image attach must not duplicate.");
        Check(ComponentAssets.TryAttach(buttonItem, typeof(Image))
            && ComponentAssets.TryAttach(buttonItem, typeof(Button))
            && buttonItem.Components.Count == 2, "Button objects must carry Image (visuals) and Button.");
        Check(buttonItem.GetComponent<Image>() is not null && buttonItem.GetComponent<Button>() is not null,
            "GetComponent must return the UI attachments.");
        Check(!ComponentAssets.TryAttach(buttonItem, typeof(Button)), "Repeated Button attach must be rejected.");

        var image = imageItem.GetComponent<Image>()!;
        image.SpritePath = "Assets/logo.png";
        image.Color = "#FF0000";
        image.Width = 200;
        image.Height = 50;
        var button = buttonItem.GetComponent<Button>()!;
        button.Text = "はじめる";
        button.Interactable = false;

        var serializer = new SceneSerializer(registry);
        var restored = serializer.Deserialize(serializer.Serialize(scene));
        var restoredImage = restored.Objects[0].GetComponent<Image>()!;
        Check(restoredImage.SpritePath == "Assets/logo.png" && restoredImage.Color == "#FF0000"
            && restoredImage.Width == 200 && restoredImage.Height == 50, "Image values did not survive YAML.");
        var restoredButton = restored.Objects[1].GetComponent<Button>()!;
        Check(restoredButton.Text == "はじめる" && !restoredButton.Interactable, "Button values did not survive YAML.");
        Check(restored.Objects[0].Name == "Image" && restored.Objects[1].Name == "Button",
            "UI object names did not survive YAML.");
        Check(serializer.Serialize(restored) == serializer.Serialize(scene), "UI save/load changed output.");

        Console.WriteLine("PASS: UI Image/Button registration, naming, attach, and YAML round-trip.");
    }
}
