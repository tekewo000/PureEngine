using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PureEngine.Editor;

/// <summary>Project内の画像素材の索引。Assets/以下の画像と隣接サイドカーからID→パスを作る。</summary>
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

    /// <summary>Project Open時と明示Refreshで再走査する。ファイルの作成・書換はしない。</summary>
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
                    diagnostics.Add($"{relative}: 素材の登録情報がありません。画像を取り込み直してください。");
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
                    diagnostics.Add($"{relative}: 登録情報が壊れています ({error.GetBaseException().Message})。");
                    continue;
                }
                if (document is null || document.Version != 1
                    || !Guid.TryParse(document.Id, out var id) || id == Guid.Empty
                    || document.Kind != "image")
                {
                    diagnostics.Add($"{relative}: 登録情報の形式が正しくありません (version 1・id・kind: image)。");
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
                    diagnostics.Add($"{Path.GetRelativePath(projectRoot, sidecar).Replace('\\', '/')}: 画像がなく登録情報だけが残っています。");
            }
        }
        Dictionary<Guid, AssetEntry> images = [];
        foreach (var (id, list) in byId)
        {
            if (list.Count != 1)
            {
                foreach (var (relative, _) in list)
                    diagnostics.Add($"{relative}: ID {id:D} が重複しているため解決できません。");
                continue;
            }
            images.Add(id, new AssetEntry(id, list[0].Relative, list[0].Full));
        }
        return new ProjectAssets(projectRoot, images, diagnostics);
    }

    /// <summary>外部の画像をAssets/へコピーして新規IDで登録する。失敗時は既存素材を上書きしない。</summary>
    public static AssetEntry ImportImage(string projectRoot, string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        projectRoot = Path.GetFullPath(projectRoot);
        sourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Source image not found.", sourcePath);
        if (!IsSupportedImage(sourcePath))
            throw new InvalidDataException(" PNG／JPEG の画像を取り込んでください。");
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

    /// <summary>描画用にID→画像バイト列を読む。欠落・重複IDは含めない。</summary>
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
                // 読み取り失敗は描画時の欠落診断に任せ、索引自体は維持する。
            }
        }
        return bytes;
    }

    private static void ValidateAssetPath(string projectRoot, string path)
    {
        var assets = Path.Combine(projectRoot, "Assets");
        ProjectFile.ValidateProjectPath(projectRoot, assets);
        ProjectFile.ValidateProjectPath(assets, path);
    }

    public static bool IsSupportedImage(string path) =>
        path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
}
