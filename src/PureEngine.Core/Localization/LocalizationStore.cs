namespace PureEngine.Core;

/// <summary>Snapshot of the project localization table for one edit session or run. Rows are looked up by stable ID.</summary>
/// <remarks>
/// Game code never touches rows directly; text and voice resolve through LocalizationService.
/// Editing and each Play run hold independent snapshots built from the same file or working copy.
/// </remarks>
public sealed class LocalizationStore
{
    private List<string> _languages = [LocalizationService.DefaultLanguage];
    private Dictionary<Guid, LocalizationEntry> _entries = [];

    public LocalizationStore()
    {
    }

    public LocalizationStore(LocalizationTable table) => Refresh(table);

    /// <summary>Table-global language columns in list order. Never empty.</summary>
    public IReadOnlyList<string> Languages => _languages;

    public IReadOnlyList<Guid> EntryIds => [.. _entries.Keys];

    public bool TryGetEntry(Guid id, out LocalizationEntry? entry) => _entries.TryGetValue(id, out entry);

    /// <summary>Rows ordered by key for editors. The store keeps no selection.</summary>
    public IReadOnlyList<LocalizationEntry> OrderedEntries() =>
        [.. _entries.Values.OrderBy(entry => entry.Key, StringComparer.Ordinal)];

    /// <summary>Replaces languages and rows in place so services and scenes sharing this instance stay current.</summary>
    public void Refresh(LocalizationTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        _languages = table.Languages.Count == 0 ? [LocalizationService.DefaultLanguage] : [.. table.Languages];
        Dictionary<Guid, LocalizationEntry> entries = [];
        foreach (var entry in table.Entries)
            entries.TryAdd(entry.Id, entry);
        _entries = entries;
    }

    /// <summary>Copies languages and rows for an independent snapshot.</summary>
    public LocalizationStore Clone()
    {
        var table = new LocalizationTable { Languages = [.. _languages] };
        foreach (var entry in _entries.Values)
        {
            table.Entries.Add(new LocalizationEntry
            {
                Id = entry.Id,
                Key = entry.Key,
                Texts = new Dictionary<string, string>(entry.Texts, StringComparer.Ordinal),
                Voices = new Dictionary<string, string>(entry.Voices, StringComparer.Ordinal),
            });
        }
        return new LocalizationStore(table);
    }

    /// <summary>Loads the project table file. A missing file is not an error and loads as an empty table.</summary>
    public static (LocalizationStore Store, IReadOnlyList<string> Diagnostics) LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) return (new LocalizationStore(), []);
        string yaml;
        try
        {
            yaml = File.ReadAllText(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return (new LocalizationStore(), [$"Localization: cannot read {path} ({error.GetBaseException().Message})."]);
        }
        try
        {
            var (table, diagnostics) = new LocalizationSerializer().Deserialize(yaml);
            return (new LocalizationStore(table), diagnostics);
        }
        catch (Exception error)
        {
            return (new LocalizationStore(), [$"Localization: invalid table ({error.GetBaseException().Message})."]);
        }
    }
}
