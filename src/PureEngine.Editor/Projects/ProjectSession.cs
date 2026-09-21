using PureEngine.Core;

namespace PureEngine.Editor;

/// <summary>A project and its validated startup scene, ready to hand to an editor.</summary>
public sealed record ProjectSession(ProjectFile Project, Scene Scene)
{
    public static ProjectSession Open(string manifestPath, Func<Type, object>? factory = null)
    {
        var project = ProjectFile.Open(manifestPath);
        var scene = new SceneSerializer(ComponentAssets.Registry).Deserialize(File.ReadAllText(project.StartupScenePath), factory);
        _ = project.ListDirectories();
        _ = project.ListFiles("Scenes");
        return new(project, scene);
    }

    public static ProjectSession Create(string parentDirectory, string name)
    {
        var scene = new Scene();
        var yaml = new SceneSerializer(ComponentAssets.Registry).Serialize(scene);
        return new(ProjectFile.Create(parentDirectory, name, yaml), scene);
    }
}
