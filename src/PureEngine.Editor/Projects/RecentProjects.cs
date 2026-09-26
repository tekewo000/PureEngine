using System.Text.Json;

namespace PureEngine.Editor;

public sealed record RecentProject(string Name, string ManifestPath);

/// <summary>Launcher history is local editor state, separate from project contents.</summary>
public sealed class RecentProjects(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
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
            Warning = "Could not load the recent projects history. Use Open Project instead.";
        }
    }

    public void Remember(ProjectFile project)
    {
        Entries = new[] { new RecentProject(project.Document.Name!, project.ManifestPath) }
            .Concat(Entries.Where(entry => !PathComparer.Equals(entry.ManifestPath, project.ManifestPath)))
            .Take(12).ToArray();
        Save("The project was opened, but the recent projects history could not be saved.");
    }

    public void Forget(RecentProject project)
    {
        if (!Entries.Any(entry => PathComparer.Equals(entry.ManifestPath, project.ManifestPath))) return;
        Entries = [.. Entries.Where(entry => !PathComparer.Equals(entry.ManifestPath, project.ManifestPath))];
        Save("The recent projects history could not be saved.");
    }

    private void Save(string saveWarning)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            SceneFile.Write(path, JsonSerializer.Serialize(Entries, JsonOptions));
            Warning = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Warning = saveWarning;
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
