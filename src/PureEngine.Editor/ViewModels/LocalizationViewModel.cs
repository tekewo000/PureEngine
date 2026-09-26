using PureEngine.Core;

namespace PureEngine.Editor;

/// <summary>Preview language and localization table presentation state. The table file itself is never saved here.</summary>
/// <remarks>
/// The selection only changes which language renderers resolve. Files keep every language, and each Play run
/// starts from this selection in its own service instance so game code can switch language at runtime.
/// </remarks>
public sealed class LocalizationViewModel : EditorObservable
{
    private readonly EditorDocuments _documents;
    private readonly InspectorViewModel _inspector;
    private string _previewLanguage = LocalizationService.DefaultLanguage;
    private IReadOnlyList<string> _availableLanguages = [LocalizationService.DefaultLanguage];
    private string _status = "No project.";

    public LocalizationViewModel(EditorDocuments documents, InspectorViewModel inspector)
    {
        _documents = documents;
        _inspector = inspector;
        _inspector.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(InspectorViewModel.IsReadOnly)) Refresh();
        };
    }

    public LocalizationDocument Document => _documents.Localization;

    /// <summary>Service used for the edit-scene preview. Follows <see cref="PreviewLanguage"/>.</summary>
    public LocalizationService PreviewService { get; } = new();

    /// <summary>Language columns in table order plus the default. Never empty.</summary>
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

    public bool IsEnabled => !_inspector.IsReadOnly;

    public bool CanSave => Document.IsDirty && IsEnabled;

    public string Title => Document.IsDirty ? "Localization *" : "Localization";

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>Rebuilds the language list from the given snapshot. Keeps the selection when still available.</summary>
    public void RefreshLanguages(LocalizationStore? store) =>
        AvailableLanguages = LocalizationService.AvailableLanguages(store);

    /// <summary>Reloads the working copy from the project table file.</summary>
    public void LoadForProject(ProjectFile? project)
    {
        if (project is null)
        {
            Document.Clear();
        }
        else
        {
            var diagnostics = Document.Load(project.LocalizationPath, project);
            foreach (var diagnostic in diagnostics) Log.Engine.Warning(diagnostic);
        }
        Refresh();
    }

    /// <summary>Refreshes title, status, and save state after edits, saves, or truth changes.</summary>
    public void Refresh()
    {
        var table = Document.Table;
        Status = Document.Path is null
            ? "No localization table. Add a key to create it."
            : $"{table.Entries.Count} key(s), {table.Languages.Count} language(s).";
        Changed(nameof(Title));
        Changed(nameof(CanSave));
        Changed(nameof(IsEnabled));
    }
}
