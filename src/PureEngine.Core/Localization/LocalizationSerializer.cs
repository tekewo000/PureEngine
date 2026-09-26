using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PureEngine.Core;

/// <summary>Stored form of the project localization table file.</summary>
internal sealed class LocalizationDocument
{
    public int Version { get; set; }
    public List<string>? Languages { get; set; }
    public List<LocalizationEntryDocument>? Entries { get; set; }
}

internal sealed class LocalizationEntryDocument
{
    public Guid Id { get; set; }
    public string? Key { get; set; }
    public Dictionary<string, string>? Texts { get; set; }
    public Dictionary<string, string>? Voices { get; set; }
}

/// <summary>Reads and writes the project localization table file. Normalizes languages and drops invalid rows with diagnostics.</summary>
public sealed class LocalizationSerializer
{
    public const int CurrentVersion = 1;
    public const string FileName = "Localization.pure.loc.yaml";

    private readonly Lazy<ISerializer> _writer = new(static () => new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithQuotingNecessaryStrings().DisableAliases()
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull).Build());
    private readonly Lazy<IDeserializer> _reader = new(static () => new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithDuplicateKeyChecking().Build());

    /// <summary>Writes the table with languages in list order and rows sorted by key for stable diffs.</summary>
    public string Serialize(LocalizationTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var languages = NormalizeLanguages(table.Languages);
        var rows = table.Entries
            .Where(entry => entry.Id != Guid.Empty && !string.IsNullOrWhiteSpace(entry.Key))
            .OrderBy(entry => entry.Key.Trim(), StringComparer.Ordinal)
            .Select(entry => new LocalizationEntryDocument
            {
                Id = entry.Id,
                Key = entry.Key.Trim(),
                Texts = KeepKnownLanguages(entry.Texts, languages),
                Voices = KeepKnownLanguages(entry.Voices, languages),
            }).ToList();
        return _writer.Value.Serialize(new LocalizationDocument
        {
            Version = CurrentVersion,
            Languages = languages,
            Entries = rows,
        });
    }

    /// <summary>Restores the table, reporting dropped languages, rows, and cells instead of throwing.</summary>
    public (LocalizationTable Table, IReadOnlyList<string> Diagnostics) Deserialize(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var document = _reader.Value.Deserialize<LocalizationDocument>(yaml)
            ?? throw new InvalidDataException("The localization document is empty.");
        if (document.Version != CurrentVersion)
            throw new InvalidDataException($"Unsupported localization version: {document.Version}");
        List<string> diagnostics = [];
        var languages = NormalizeLanguages(document.Languages ?? [], diagnostics);
        var table = new LocalizationTable { Languages = languages };
        HashSet<Guid> seenIds = [];
        HashSet<string> seenKeys = [];
        foreach (var row in document.Entries ?? [])
        {
            if (row.Id == Guid.Empty || string.IsNullOrWhiteSpace(row.Key))
            {
                diagnostics.Add("Localization: dropped a row with an empty ID or key.");
                continue;
            }
            var key = row.Key.Trim();
            if (!seenIds.Add(row.Id) || !seenKeys.Add(key))
            {
                diagnostics.Add($"Localization: dropped duplicate row '{key}'. IDs and keys must be unique.");
                continue;
            }
            table.Entries.Add(new LocalizationEntry
            {
                Id = row.Id,
                Key = key,
                Texts = KeepKnownLanguages(row.Texts ?? [], languages, key, "text", diagnostics),
                Voices = KeepKnownLanguages(row.Voices ?? [], languages, key, "voice", diagnostics),
            });
        }
        return (table, diagnostics);
    }

    private static List<string> NormalizeLanguages(IEnumerable<string> codes, List<string>? diagnostics = null)
    {
        List<string> normalized = [];
        HashSet<string> seen = [];
        foreach (var code in codes)
        {
            var clean = code.Trim().ToLowerInvariant();
            if (!LocalizationService.IsValidLanguageCode(clean) || !seen.Add(clean))
            {
                diagnostics?.Add($"Localization: dropped invalid language '{code}'.");
                continue;
            }
            normalized.Add(clean);
        }
        if (normalized.Count == 0) normalized.Add(LocalizationService.DefaultLanguage);
        return normalized;
    }

    private static Dictionary<string, string> KeepKnownLanguages(
        Dictionary<string, string> cells, List<string> languages,
        string? key = null, string? kind = null, List<string>? diagnostics = null)
    {
        var kept = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (code, value) in cells)
        {
            var clean = code.Trim().ToLowerInvariant();
            if (!languages.Contains(clean))
            {
                if (key is not null)
                    diagnostics?.Add($"Localization: dropped {kind} '{code}' of '{key}' outside the table languages.");
                continue;
            }
            kept[clean] = value ?? "";
        }
        return kept;
    }
}
