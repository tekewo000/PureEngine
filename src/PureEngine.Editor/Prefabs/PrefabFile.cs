using PureEngine.Core;

namespace PureEngine.Editor;

/// <summary>Creates and reads prefab files. Publishes complete files without replacing existing prefabs.</summary>
public static class PrefabFile
{
    /// <summary>Captures the object subtree and writes it to the path with a fresh prefab ID. Returns the new prefab ID.</summary>
    public static Guid Create(string path, Scene scene, SceneObject root, ComponentRegistry registry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(registry);
        var id = Guid.NewGuid();
        var yaml = new PrefabSerializer(registry).Serialize(scene, root, id);
        path = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Guid.NewGuid():N}.tmp");
        try
        {
            SceneFile.Write(temporary, yaml);
            File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return id;
    }

    /// <summary>Reads a prefab file without touching the scene being edited.</summary>
    public static PrefabDocument Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Prefab not found.", path);
        return PrefabSerializer.Parse(File.ReadAllText(path));
    }
}
