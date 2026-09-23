using PureEngine.Core;

static class UiCreationChecks
{
    public static void Run()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        var scene = new Scene();
        var parent = scene.AddEmpty();
        var image = scene.AddNamed(" Image ");
        image.SetParent(parent);
        Check(image.Name == "Image" && ReferenceEquals(image.Parent, parent), "Named objects must preserve scene ownership and trim names.");
        Check(scene.AddNamed("Image").Name == "Image (1)" && scene.AddNamed(" Image ").Name == "Image (2)",
            "Names must be unique after trimming.");
        Check(scene.AddNamed("Button").Name == "Button" && scene.AddNamed("Button").Name == "Button (1)",
            "Each base name must have its own suffix sequence.");
        Check(scene.AddEmpty().Name == "Empty (1)", "AddEmpty naming must remain unchanged.");
        try
        {
            scene.AddNamed("   ");
            throw new InvalidOperationException("Blank names must be rejected.");
        }
        catch (ArgumentException) { }

        var serializer = new SceneSerializer(new ComponentRegistry());
        var yaml = serializer.Serialize(scene);
        Check(serializer.Serialize(serializer.Deserialize(yaml)) == yaml, "Named objects and parent links must survive save/load.");
        using var runtime = new SceneRuntime(new Scene(), new ComponentRegistry());
        var runtimeParent = runtime.Scene.AddNamed("Group");
        var runtimeChild = runtime.Scene.AddNamed("Image");
        runtimeChild.SetParent(runtimeParent);
        Check(ReferenceEquals(runtimeChild.Parent, runtimeParent), "Named runtime objects must retain hierarchy ownership.");
        Console.WriteLine("PASS: named object uniqueness, validation, ownership, and YAML round-trip.");
    }
}
