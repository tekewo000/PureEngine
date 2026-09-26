using PureEngine.Core;

namespace PureEngine.Editor;

/// <summary>Editing working copy of the project localization table. Owned by the editor, never shared with runs.</summary>
/// <remarks>
/// The tab edits this copy; saving persists it to the table file. Preview and Play snapshots are rebuilt
/// from the saved file so unsaved edits never leak into scenes or runs.
/// </remarks>
public sealed class LocalizationDocument
{
    private LocalizationTable _table = new();

    /// <summary>Null until the table is loaded for a project or created by the first edit.</summary>
    public string? Path { get; private set; }

    public bool IsDirty { get; private set; }

    public LocalizationTable Table => _table;

    /// <summary>Loads the table file, replacing the working copy. A missing file loads as an empty table.</summary>
    public IReadOnlyList<string> Load(string path, ProjectFile project)
    {
        ArgumentNullException.ThrowIfNull(project);
        project.ValidateLocalizationPath(path);
        var (store, diagnostics) = LocalizationStore.LoadFile(path);
        var table = new LocalizationTable { Languages = [.. store.Languages] };
        foreach (var entry in store.OrderedEntries())
        {
            table.Entries.Add(new LocalizationEntry
            {
                Id = entry.Id,
                Key = entry.Key,
                Texts = new Dictionary<string, string>(entry.Texts, StringComparer.Ordinal),
                Voices = new Dictionary<string, string>(entry.Voices, StringComparer.Ordinal),
            });
        }
        _table = table;
        Path = System.IO.Path.GetFullPath(path);
        IsDirty = false;
        return diagnostics;
    }

    /// <summary>Stores the serialized working copy to the table file.</summary>
    public void Save(string path, ProjectFile project)
    {
        ArgumentNullException.ThrowIfNull(project);
        project.ValidateLocalizationPath(path);
        File.WriteAllText(path, new LocalizationSerializer().Serialize(_table));
        Path = System.IO.Path.GetFullPath(path);
        IsDirty = false;
    }

    public void MarkDirty() => IsDirty = true;

    public void Clear()
    {
        _table = new LocalizationTable();
        Path = null;
        IsDirty = false;
    }
}
