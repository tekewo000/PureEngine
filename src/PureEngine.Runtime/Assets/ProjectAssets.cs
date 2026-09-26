using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PureEngine.Editor;

/// <summary>Index of image assets in the project. Builds ID-to-path entries from images under Assets/ and adjacent sidecars.</summary>
public sealed class ProjectAssets
{
    public sealed record AssetEntry(Guid Id, string RelativePath, string FullPath);

    private static readonly IDeserializer Reader = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithDuplicateKeyChecking().Build();
    private static readonly ISerializer Writer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithQuotingNecessaryStrings().DisableAliases().Build();

    private sealed class AssetDocument
    {
        public int Version { get; set; }
        public string? Id { get; set; }
        public string? Kind { get; set; }
    }

    public string ProjectRoot { get; }
    public string AssetsDirectory => Path.Combine(ProjectRoot, "Assets");
    public IReadOnlyDictionary<Guid, AssetEntry> Images { get; }
    public IReadOnlyList<string> Diagnostics { get; }

    private ProjectAssets(string projectRoot, Dictionary<Guid, AssetEntry> images, List<string> diagnostics)
    {
        ProjectRoot = projectRoot;
        Images = images;
        Diagnostics = diagnostics;
    }

    /// <summary>Rescans on project open and explicit refresh. Never creates or rewrites files.</summary>
    public static ProjectAssets Scan(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        projectRoot = Path.GetFullPath(projectRoot);
        Dictionary<Guid, List<(string Relative, string Full)>> byId = [];
        List<string> diagnostics = [];
        var assets = Path.Combine(projectRoot, "Assets");
        try { ValidateAssetPath(projectRoot, assets); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            diagnostics.Add($"Assets: {error.Message}");
            return new ProjectAssets(projectRoot, [], diagnostics);
        }
        if (Directory.Exists(assets))
        {
            foreach (var full in Directory.EnumerateFiles(assets, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false,
            }).Order(StringComparer.Ordinal))
            {
                if (full.EndsWith(".pureasset.yaml", StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsSupportedImage(full)) continue;
                var relative = Path.GetRelativePath(projectRoot, full).Replace('\\', '/');
                var sidecar = full + ".pureasset.yaml";
                if (!File.Exists(sidecar))
                {
                    diagnostics.Add($"{relative}: Missing asset registration. Reimport the image.");
                    continue;
                }
                AssetDocument? document;
                try
                {
                    ValidateAssetPath(projectRoot, full);
                    ValidateAssetPath(projectRoot, sidecar);
                    document = Reader.Deserialize<AssetDocument>(File.ReadAllText(sidecar));
                }
                catch (Exception error)
                {
                    diagnostics.Add($"{relative}: Registration is corrupted ({error.GetBaseException().Message}).");
                    continue;
                }
                if (document is null || document.Version != 1
                    || !Guid.TryParse(document.Id, out var id) || id == Guid.Empty
                    || document.Kind != "image")
                {
                    diagnostics.Add($"{relative}: Invalid registration format (version 1, id, kind: image).");
                    continue;
                }
                if (!byId.TryGetValue(id, out var list))
                {
                    list = [];
                    byId.Add(id, list);
                }
                list.Add((relative, full));
            }
            foreach (var sidecar in Directory.EnumerateFiles(assets, "*.pureasset.yaml", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false,
            }).Order(StringComparer.Ordinal))
            {
                var imagePath = sidecar[..^".pureasset.yaml".Length];
                if (!File.Exists(imagePath))
                    diagnostics.Add($"{Path.GetRelativePath(projectRoot, sidecar).Replace('\\', '/')}: The image is missing; only the registration remains.");
            }
        }
        Dictionary<Guid, AssetEntry> images = [];
        foreach (var (id, list) in byId)
        {
            if (list.Count != 1)
            {
                foreach (var (relative, _) in list)
                    diagnostics.Add($"{relative}: Cannot resolve because ID {id:D} is duplicated.");
                continue;
            }
            images.Add(id, new AssetEntry(id, list[0].Relative, list[0].Full));
        }
        return new ProjectAssets(projectRoot, images, diagnostics);
    }

    /// <summary>Copies an external image into Assets/ and registers it with a new ID. Never overwrites existing assets on failure.</summary>
    public static AssetEntry ImportImage(string projectRoot, string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        projectRoot = Path.GetFullPath(projectRoot);
        sourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Source image not found.", sourcePath);
        if (!IsSupportedImage(sourcePath))
            throw new InvalidDataException("Import a PNG/JPEG image.");
        var assets = Path.Combine(projectRoot, "Assets");
        ValidateAssetPath(projectRoot, assets);
        Directory.CreateDirectory(assets);
        var fileName = Path.GetFileName(sourcePath);
        var destination = Path.Combine(assets, fileName);
        for (var suffix = 1; File.Exists(destination) || File.Exists(destination + ".pureasset.yaml"); suffix++)
            destination = Path.Combine(assets,
                $"{Path.GetFileNameWithoutExtension(fileName)} ({suffix}){Path.GetExtension(fileName)}");
        var id = Guid.NewGuid();
        var sidecar = destination + ".pureasset.yaml";
        var document = new AssetDocument { Version = 1, Id = id.ToString("D"), Kind = "image" };
        var staging = destination + $".{Guid.NewGuid():N}.tmp";
        var stagingSidecar = sidecar + $".{Guid.NewGuid():N}.tmp";
        var publishedImage = false;
        var completed = false;
        try
        {
            ValidateAssetPath(projectRoot, staging);
            File.Copy(sourcePath, staging);
            ValidateAssetPath(projectRoot, stagingSidecar);
            File.WriteAllText(stagingSidecar, Writer.Serialize(document));
            ValidateAssetPath(projectRoot, destination);
            File.Move(staging, destination);
            publishedImage = true;
            ValidateAssetPath(projectRoot, sidecar);
            File.Move(stagingSidecar, sidecar);
            completed = true;
        }
        finally
        {
            ValidateAssetPath(projectRoot, staging);
            if (File.Exists(staging)) File.Delete(staging);
            ValidateAssetPath(projectRoot, stagingSidecar);
            if (File.Exists(stagingSidecar)) File.Delete(stagingSidecar);
            if (publishedImage && !completed)
            {
                ValidateAssetPath(projectRoot, destination);
                File.Delete(destination);
            }
        }
        var relative = Path.GetRelativePath(projectRoot, destination).Replace('\\', '/');
        return new AssetEntry(id, relative, destination);
    }

    /// <summary>Reads ID-to-image bytes for rendering. Excludes missing and duplicated IDs.</summary>
    public Dictionary<Guid, byte[]> LoadImageBytes()
    {
        Dictionary<Guid, byte[]> bytes = [];
        foreach (var (id, entry) in Images)
        {
            try
            {
                ValidateAssetPath(ProjectRoot, entry.FullPath);
                bytes.Add(id, File.ReadAllBytes(entry.FullPath));
            }
            catch
            {
                // Leave read failures to the missing-asset diagnostics at render time and keep the index intact.
            }
        }
        return bytes;
    }

    private static void ValidateAssetPath(string projectRoot, string path)
    {
        var assets = Path.Combine(projectRoot, "Assets");
        Runtime.ContentPaths.Validate(projectRoot, assets);
        Runtime.ContentPaths.Validate(assets, path);
    }

    public static bool IsSupportedImage(string path) =>
        path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
}
