using System.Text.RegularExpressions;

namespace PureEngine.Core;

/// <summary>Resolves localized text and voice slots for one preview or run language with a shared fallback chain.</summary>
/// <remarks>
/// Editing and each Play run own independent instances through constructor injection, so a settings screen
/// can switch <see cref="CurrentLanguage"/> at runtime without touching files or other runs.
/// The single fallback rule is current language, the default language, the first available language, then the caller fallback.
/// Empty values always count as untranslated and never win over a real translation.
/// </remarks>
public sealed partial class LocalizationService
{
    public const string DefaultLanguage = "ja";

    [GeneratedRegex("^[a-z]{2,8}(-[a-z0-9]{2,8})*$", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex LanguageCodePattern();

    private string _currentLanguage = DefaultLanguage;

    /// <summary>Language used for resolution. Normalized to lowercase; invalid codes are rejected.</summary>
    public string CurrentLanguage
    {
        get => _currentLanguage;
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            var normalized = value.Trim().ToLowerInvariant();
            if (!IsValidLanguageCode(normalized))
                throw new ArgumentException($"Invalid language code: {value}", nameof(value));
            _currentLanguage = normalized;
        }
    }

    /// <summary>Whether the code is a BCP47-style primary tag such as ja, en, ko, or ja-JP.</summary>
    public static bool IsValidLanguageCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        return LanguageCodePattern().IsMatch(code.Trim().ToLowerInvariant());
    }

    /// <summary>Resolves the entry for <see cref="CurrentLanguage"/> with the shared fallback chain.</summary>
    public string ResolveText(LocalizedText? entry, string? fallback) =>
        ResolveText(entry, fallback, _currentLanguage);

    /// <summary>Resolves the voice slot for <see cref="CurrentLanguage"/>. Null means silence.</summary>
    public string? ResolveVoice(LocalizedText? entry) => ResolveVoice(entry, _currentLanguage);

    /// <summary>Resolves without a service instance for renderers and checks. A null language uses the default.</summary>
    public static string ResolveText(LocalizedText? entry, string? fallback, string? language)
    {
        if (entry is null) return fallback ?? "";
        var current = string.IsNullOrWhiteSpace(language) ? DefaultLanguage : language.Trim().ToLowerInvariant();
        if (TryUsableText(entry.Texts, current, out var text)) return text;
        if (!string.Equals(current, DefaultLanguage, StringComparison.Ordinal)
            && TryUsableText(entry.Texts, DefaultLanguage, out text)) return text;
        foreach (var code in entry.Texts.Keys.Order(StringComparer.Ordinal))
            if (TryUsableText(entry.Texts, code, out text)) return text;
        return fallback ?? "";
    }

    /// <summary>Resolves a voice slot without a service instance. A null language uses the default.</summary>
    public static string? ResolveVoice(LocalizedText? entry, string? language)
    {
        if (entry is null) return null;
        var current = string.IsNullOrWhiteSpace(language) ? DefaultLanguage : language.Trim().ToLowerInvariant();
        if (TryUsableVoice(entry.Voices, current, out var voice)) return voice;
        if (!string.Equals(current, DefaultLanguage, StringComparison.Ordinal)
            && TryUsableVoice(entry.Voices, DefaultLanguage, out voice)) return voice;
        foreach (var code in entry.Voices.Keys.Order(StringComparer.Ordinal))
            if (TryUsableVoice(entry.Voices, code, out voice)) return voice;
        return null;
    }

    /// <summary>Language columns for editors: every language used by any entry plus the default, sorted.</summary>
    public static IReadOnlyList<string> AvailableLanguages(DataAssetStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        HashSet<string> found = [DefaultLanguage];
        foreach (var id in store.Ids)
        {
            if (!store.TryGet<object>(id, out var asset) || asset is not LocalizedText entry) continue;
            foreach (var code in entry.Texts.Keys.Concat(entry.Voices.Keys))
            {
                var normalized = code.Trim().ToLowerInvariant();
                if (IsValidLanguageCode(normalized)) found.Add(normalized);
            }
        }
        List<string> ordered = [.. found];
        ordered.Sort(StringComparer.Ordinal);
        return ordered;
    }

    private static bool TryUsableText(Dictionary<string, string> texts, string code, out string text)
    {
        text = "";
        foreach (var (key, value) in texts)
        {
            if (!string.Equals(key.Trim().ToLowerInvariant(), code, StringComparison.Ordinal)) continue;
            if (string.IsNullOrWhiteSpace(value)) return false;
            text = value;
            return true;
        }
        return false;
    }

    private static bool TryUsableVoice(Dictionary<string, string> voices, string code, out string? voice)
    {
        voice = null;
        foreach (var (key, value) in voices)
        {
            if (!string.Equals(key.Trim().ToLowerInvariant(), code, StringComparison.Ordinal)) continue;
            if (string.IsNullOrWhiteSpace(value)) return false;
            voice = value;
            return true;
        }
        return false;
    }
}
