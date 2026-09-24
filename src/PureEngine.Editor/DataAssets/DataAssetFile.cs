using PureEngine.Core;

namespace PureEngine.Editor;

/// <summary>Creates and reads data asset files. Publishes complete files without replacing existing assets.</summary>
public static class DataAssetFile
{
    /// <summary>Creates a default asset of the given type and writes it to the path. Returns the new asset ID.</summary>
    public static Guid Create(string path, Type type, ComponentRegistry registry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(registry);
        var serializer = new DataAssetSerializer(registry);
        var instance = serializer.CreateInstance(type);
        var id = Guid.NewGuid();
        var yaml = serializer.Serialize(instance, id);
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

    /// <summary>Reads an asset file without touching the scene being edited.</summary>
    public static (object Instance, Guid Id, string TypeId) Load(string path, ComponentRegistry registry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(registry);
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Data asset not found.", path);
        var serializer = new DataAssetSerializer(registry);
        var (instance, id) = serializer.Deserialize(File.ReadAllText(path), out var typeId, out _);
        return (instance, id, typeId);
    }
}