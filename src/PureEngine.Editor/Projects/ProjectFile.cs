using PureEngine.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PureEngine.Editor;

/// <summary>A project manifest plus scenes beneath its own Scenes directory.</summary>
public sealed class ProjectFile
{
    public string ManifestPath { get; }
    public string RootDirectory => Path.GetDirectoryName(ManifestPath)!;
    public string ScenesDirectory => Path.Combine(RootDirectory, "Scenes");
    public ProjectDocument Document { get; private set; }
    public string StartupScenePath => ResolveScenePath(Document.StartupScene!);
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private ProjectFile(string manifestPath, ProjectDocument document)
    {
        ManifestPath = Path.GetFullPath(manifestPath);
        Document = document;
    }

    public static ProjectFile Open(string manifestPath)
    {
        var document = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithDuplicateKeyChecking().Build().Deserialize<ProjectDocument>(File.ReadAllText(manifestPath));
        if (document is null || document.Version != 1 || string.IsNullOrWhiteSpace(document.Name)
            || string.IsNullOrWhiteSpace(document.StartupScene))
            throw new InvalidDataException("Projectにはversion: 1、name、startupSceneが必要です。");
        var project = new ProjectFile(manifestPath, document);
        if (!File.Exists(project.StartupScenePath))
            throw new FileNotFoundException("起動シーンが見つかりません。", project.StartupScenePath);
        return project;
    }

    public static ProjectFile Create(string parentDirectory, string name, string initialSceneYaml)
    {
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name is "." or ".."
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.'))
            throw new ArgumentException("Project名には有効なフォルダ名を指定してください。");
        if (!Directory.Exists(parentDirectory)) throw new DirectoryNotFoundException(parentDirectory);
        var root = Path.Combine(Path.GetFullPath(parentDirectory), name);
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("同名のフォルダまたはファイルが既にあります。");
        var project = new ProjectFile(Path.Combine(root, "Project.pure.project.yaml"), new ProjectDocument
        {
            Version = 1, Name = name, StartupScene = "Scenes/Main.pure.scene.yaml",
        });
        // Publish a complete folder only after both files have been written successfully.
        var staging = Path.Combine(Path.GetFullPath(parentDirectory), ".pure-project-" + Guid.NewGuid().ToString("N"));
        var staged = new ProjectFile(Path.Combine(staging, "Project.pure.project.yaml"), project.Document);
        try
        {
            Directory.CreateDirectory(staged.ScenesDirectory);
            SceneFile.Write(staged.StartupScenePath, initialSceneYaml);
            staged.WriteManifest(staged.Document);
            if (!string.Equals(Path.GetDirectoryName(staging), Path.GetDirectoryName(root), PathComparison))
                throw new IOException("Projectの作成先が親フォルダの外にあります。");
            Directory.Move(staging, root);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                File.Delete(staged.StartupScenePath);
                File.Delete(staged.ManifestPath);
                if (Directory.Exists(staged.ScenesDirectory)) Directory.Delete(staged.ScenesDirectory);
                Directory.Delete(staging);
            }
        }
        return project;
    }

    public string ResolveScenePath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) throw new InvalidDataException("シーンのパスはProjectからの相対パスにしてください。");
        var path = Path.GetFullPath(Path.Combine(RootDirectory, relativePath));
        ValidateScenePath(path);
        return path;
    }

    public string GetSceneRelativePath(string fullPath)
    {
        ValidateScenePath(fullPath);
        return Path.GetRelativePath(RootDirectory, fullPath).Replace('\\', '/');
    }

    public void ValidateScenePath(string path)
    {
        path = Path.GetFullPath(path);
        if (!path.StartsWith(ScenesDirectory + Path.DirectorySeparatorChar, PathComparison)
            || !path.EndsWith(".pure.scene.yaml", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ProjectのScenesフォルダ内に .pure.scene.yaml 形式で保存してください。");
        // Do not let a linked directory silently redirect a project-relative path outside the project.
        for (var entry = path; !string.Equals(entry, RootDirectory, PathComparison); entry = Path.GetDirectoryName(entry)!)
            if (Path.Exists(entry) && (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Projectのシーンにはリンク先のファイル・フォルダを指定できません。");
    }

    public IReadOnlyList<string> ListScenes() => Directory.EnumerateFiles(ScenesDirectory, "*", new EnumerationOptions
        { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false })
        .Where(path => path.EndsWith(".pure.scene.yaml", StringComparison.OrdinalIgnoreCase))
        .Select(GetSceneRelativePath).Order(StringComparer.Ordinal).ToArray();

    /// <summary>Project root直下からの相対フォルダ一覧。""はルート自身を表す。ExplorerのTree用。</summary>
    public IReadOnlyList<string> ListDirectories()
    {
        if (!Directory.Exists(RootDirectory)) return [];
        return Directory.EnumerateDirectories(RootDirectory, "*", new EnumerationOptions
            { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false })
            .Select(path => Path.GetRelativePath(RootDirectory, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>指定フォルダ直下のファイル名一覧（非再帰）。Explorerの右ペイン用。</summary>
    public IReadOnlyList<string> ListFiles(string relativeDirectory)
    {
        var directory = ResolveDirectoryPath(relativeDirectory);
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*", new EnumerationOptions
            { RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false })
            .Select(path => Path.GetFileName(path)!)
            .Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>相対フォルダパスを実パスに解決し、Root外への脱出を拒否する。</summary>
    public string ResolveDirectoryPath(string relativeDirectory)
    {
        relativeDirectory = (relativeDirectory ?? "").Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(relativeDirectory)) throw new InvalidDataException("フォルダのパスはProjectからの相対パスにしてください。");
        var path = Path.GetFullPath(Path.Combine(RootDirectory, relativeDirectory));
        ValidateFolderPath(path);
        return path;
    }

    public void ValidateFolderPath(string path)
    {
        path = Path.GetFullPath(path);
        if (!string.Equals(path, RootDirectory, PathComparison)
            && !path.StartsWith(RootDirectory + Path.DirectorySeparatorChar, PathComparison))
            throw new InvalidDataException("Projectフォルダ内のパスを指定してください。");
        for (var entry = path; !string.Equals(entry, RootDirectory, PathComparison); entry = Path.GetDirectoryName(entry)!)
            if (Path.Exists(entry) && (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Projectのフォルダにはリンク先を指定できません。");
    }

    /// <summary>指定フォルダがScenes配下かどうか。シーンファイル作成可否の判定用。</summary>
    public bool IsUnderScenes(string relativeDirectory)
    {
        var path = Path.GetFullPath(Path.Combine(RootDirectory,
            (relativeDirectory ?? "").Replace('/', Path.DirectorySeparatorChar)));
        return string.Equals(path, ScenesDirectory, PathComparison)
            || path.StartsWith(ScenesDirectory + Path.DirectorySeparatorChar, PathComparison);
    }

    /// <summary>指定フォルダ内に重複しないシーン名を返す。</summary>
    public string NextSceneName(string relativeDirectory)
    {
        var directory = ResolveDirectoryPath(relativeDirectory);
        if (!IsUnderScenes(relativeDirectory)) throw new InvalidDataException("シーンはProjectのScenesフォルダ内に作成してください。");
        var name = "Scene.pure.scene.yaml";
        for (var number = 2; File.Exists(Path.Combine(directory, name)); number++)
            name = $"Scene{number}.pure.scene.yaml";
        return name;
    }

    public string NextSceneName()
    {
        var name = "Scene.pure.scene.yaml";
        for (var number = 2; File.Exists(Path.Combine(ScenesDirectory, name)); number++)
            name = $"Scene{number}.pure.scene.yaml";
        return name;
    }

    public void SetStartupScene(string path)
    {
        var relative = GetSceneRelativePath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("起動シーンを先に保存してください。", path);
        var updated = new ProjectDocument { Version = 1, Name = Document.Name, StartupScene = relative };
        WriteManifest(updated);
        Document = updated;
    }

    private void WriteManifest(ProjectDocument document) => SceneFile.Write(ManifestPath,
        new SerializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithQuotingNecessaryStrings().DisableAliases().Build().Serialize(document));
}
