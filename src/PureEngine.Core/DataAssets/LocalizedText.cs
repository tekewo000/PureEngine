namespace PureEngine.Core;

/// <summary>One localizable string shared by any Text. Holds per-language text with optional voice clip slots.</summary>
/// <remarks>
/// Language keys use BCP47-style primary tags in lowercase (ja, en, ko). An empty value counts as untranslated
/// and falls back through <see cref="LocalizationService"/> instead of drawing blank.
/// Voice values are clip references kept as strings until the audio pipeline lands; empty means silence.
/// The display <see cref="Key"/> is only a human-readable name. Saves and scene references keep the stable file ID,
/// so renaming the key never breaks them.
/// </remarks>
[DataAsset("Localization/Text")]
public sealed class LocalizedText
{
    [Inspector]
    public string Key { get; set; } = "menu.new";

    [Inspector]
    public Dictionary<string, string> Texts { get; set; } = [];

    [Inspector]
    public Dictionary<string, string> Voices { get; set; } = [];
}
