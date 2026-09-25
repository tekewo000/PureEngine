using PureEngine.Core;

namespace PureEngine.Editor;

/// <summary>Creates, reads and edits prefab files with atomic publication.</summary>
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

    /// <summary>Restores an isolated authoring scene with the prefab's original object and component identities.</summary>
    public static Scene OpenForEditing(string path, ComponentRegistry registry, out Guid prefabId, out bool membersChanged,
        DataAssetStore? assets = null, PrefabCatalog? prefabs = null, Func<Type, object>? factory = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var document = Load(path);
        var scene = new PrefabSerializer(registry).RestoreForEditing(document, out membersChanged, assets, prefabs, factory);
        prefabId = document.Id;
        return scene;
    }

    /// <summary>Validates and atomically saves one prefab subtree without changing any identities.</summary>
    public static void Save(string path, Scene scene, Guid prefabId, ComponentRegistry registry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(registry);
        if (prefabId == Guid.Empty) throw new InvalidDataException("Prefab ID must not be empty.");
        if (scene.RootObjects.Count != 1)
            throw new InvalidDataException("A prefab must contain exactly one root object.");
        scene.RootObjects[0].PrefabId = prefabId;
        var document = new PrefabSerializer(registry).Capture(scene, scene.RootObjects[0]);
        document.Id = prefabId;
        document.Objects!.Single(item => item!.ParentId is null)!.PrefabId = prefabId;
        var yaml = PrefabSerializer.Serialize(document);
        SceneFile.Write(path, yaml);
    }
}
