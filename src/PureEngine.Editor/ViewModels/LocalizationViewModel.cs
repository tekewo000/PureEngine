using PureEngine.Core;

namespace PureEngine.Editor;

/// <summary>Preview language for localized text in Scene View and Game. Owned by the editor, never saved.</summary>
/// <remarks>
/// The selection only changes which language renderers resolve. Files keep every language, and each Play run
/// starts from this selection in its own service instance so game code can switch language at runtime.
/// </remarks>
public sealed class LocalizationViewModel : EditorObservable
{
    private string _previewLanguage = LocalizationService.DefaultLanguage;
    private IReadOnlyList<string> _availableLanguages = [LocalizationService.DefaultLanguage];

    /// <summary>Service used for the edit-scene preview. Follows <see cref="PreviewLanguage"/>.</summary>
    public LocalizationService PreviewService { get; } = new();

    /// <summary>Languages in use by LocalizedText assets plus the default. Never empty.</summary>
    public IReadOnlyList<string> AvailableLanguages
    {
        get => _availableLanguages;
        private set
        {
            if (value.Count == 0) value = [LocalizationService.DefaultLanguage];
            if (_availableLanguages.SequenceEqual(value)) return;
            _availableLanguages = value;
            Changed();
            if (!_availableLanguages.Contains(_previewLanguage))
                PreviewLanguage = _availableLanguages[0];
        }
    }

    public string PreviewLanguage
    {
        get => _previewLanguage;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || !LocalizationService.IsValidLanguageCode(value)) return;
            var normalized = value.Trim().ToLowerInvariant();
            if (SetProperty(ref _previewLanguage, normalized))
                PreviewService.CurrentLanguage = normalized;
        }
    }

    /// <summary>Rebuilds the language list from the project store. Keeps the selection when still available.</summary>
    public void Refresh(DataAssetStore? store) =>
        AvailableLanguages = store is null
            ? [LocalizationService.DefaultLanguage]
            : LocalizationService.AvailableLanguages(store);

    /// <summary>Resets to the default language when the project closes.</summary>
    public void Clear() => Refresh(null);
}
