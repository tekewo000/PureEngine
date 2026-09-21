using System.Text.Json;

namespace PureEngine.Editor;

public sealed record RecentProject(string Name, string ManifestPath);

/// <summary>Launcher history is local editor state, separate from project contents.</summary>
public sealed class RecentProjects(string path)
{
    public IReadOnlyList<RecentProject> Entries { get; private set; } = [];
    public string? Warning { get; private set; }

    public void Load()
    {
        try
        {
            var entries = File.Exists(path)
                ? JsonSerializer.Deserialize<List<RecentProject>>(File.ReadAllText(path)) ?? [] : [];
            Entries = entries.Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.Name)
                && !string.IsNullOrWhiteSpace(entry.ManifestPath) && Path.IsPathFullyQualified(entry.ManifestPath))
                .DistinctBy(entry => entry.ManifestPath, PathComparer).Take(12).ToArray();
            Warning = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Entries = [];
            Warning = "最近開いたProjectの履歴を読み込めませんでした。Open Projectから開けます。";
        }
    }

    public void Remember(ProjectFile project)
    {
        Entries = new[] { new RecentProject(project.Document.Name!, project.ManifestPath) }
            .Concat(Entries.Where(entry => !PathComparer.Equals(entry.ManifestPath, project.ManifestPath)))
            .Take(12).ToArray();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            SceneFile.Write(path, JsonSerializer.Serialize(Entries, new JsonSerializerOptions { WriteIndented = true }));
            Warning = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Warning = "Projectは開けましたが、最近開いたProjectの履歴を保存できませんでした。";
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
