namespace PureEngine.Core;

/// <summary>One localizable string row. Identity is the stable ID; the key is only a human-readable name.</summary>
public sealed class LocalizationEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Key { get; set; } = "menu.new";

    /// <summary>Language code to text. Every key must exist in the owning table languages; missing cells are untranslated.</summary>
    public Dictionary<string, string> Texts { get; set; } = [];

    /// <summary>Language code to voice clip reference. Empty means silence; clips resolve when the audio pipeline lands.</summary>
    public Dictionary<string, string> Voices { get; set; } = [];
}

/// <summary>Project-wide localization table. Languages are table-global columns; every row shares them.</summary>
/// <remarks>There is no per-row language set. Adding a language adds an empty cell to every row.</remarks>
public sealed class LocalizationTable
{
    public List<string> Languages { get; set; } = [LocalizationService.DefaultLanguage];

    public List<LocalizationEntry> Entries { get; set; } = [];

    /// <summary>Deep-copies languages and rows for snapshots and working copies.</summary>
    public LocalizationTable Clone()
    {
        var copy = new LocalizationTable { Languages = [.. Languages] };
        foreach (var entry in Entries)
        {
            copy.Entries.Add(new LocalizationEntry
            {
                Id = entry.Id,
                Key = entry.Key,
                Texts = new Dictionary<string, string>(entry.Texts, StringComparer.Ordinal),
                Voices = new Dictionary<string, string>(entry.Voices, StringComparer.Ordinal),
            });
        }
        return copy;
    }
}
