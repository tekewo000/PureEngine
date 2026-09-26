using PureEngine.Core;

namespace PureEngine.Editor;

/// <summary>Single-asset editing presentation and save decisions.</summary>
public sealed class DataAssetViewModel : EditorObservable
{
    private readonly EditorDocuments _documents;
    private readonly InspectorViewModel _inspector;

    public DataAssetViewModel(EditorDocuments documents, InspectorViewModel inspector)
    {
        _documents = documents;
        _inspector = inspector;
        inspector.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(InspectorViewModel.IsReadOnly)) Changed(nameof(CanSave));
            if (args.PropertyName == nameof(InspectorViewModel.SelectedObject)) Changed(nameof(IsVisible));
        };
    }

    public DataAssetEditState? Document => _documents.Asset;
    public bool IsVisible => Document is not null && !_inspector.HasObject;
    public bool CanSave => Document is { Dirty: true } && !_inspector.IsReadOnly;
    public string Title => Document is { } state ? (state.Dirty ? "* " : "") + Path.GetFileName(state.Path) : "";
    public string Id => Document?.Id.ToString("D") ?? "";
    public string TypeId => Document?.TypeId ?? "";
    public string? TypeName => Document?.Instance.GetType().FullName;
    public string Error { get; private set; } = "";
    public bool HasError => Error.Length != 0;
    public string Status { get; private set; } = "";

    public static DataAssetEditState PrepareOpen(string path, ComponentRegistry registry, ProjectFile? project)
    {
        project?.ValidateDataAssetPath(path);
        var (instance, id) = new DataAssetSerializer(registry).Deserialize(File.ReadAllText(path), out var typeId, out var changed);
        return new(Path.GetFullPath(path), instance, id, typeId) { Dirty = changed };
    }

    public void Adopt(DataAssetEditState document)
    {
        _documents.Asset = document;
        _documents.RefreshAssetOwnership();
        Refresh(clearError: true);
    }

    public void Close()
    {
        _documents.Asset = null;
        _documents.AssetOwned.Clear();
        Refresh(clearError: true);
    }

    public void Refresh(bool clearError = false)
    {
        if (clearError) Error = "";
        Changed(nameof(Document));
        Changed(nameof(IsVisible));
        Changed(nameof(CanSave));
        Changed(nameof(Title));
        Changed(nameof(Id));
        Changed(nameof(TypeId));
        Changed(nameof(TypeName));
        Changed(nameof(Error));
        Changed(nameof(HasError));
    }

    public bool Save(ComponentRegistry registry, ProjectFile? project)
    {
        if (_inspector.IsReadOnly) return false;
        if (Document is not { } state) return true;
        if (EditorOperationGate.SaveBlockReason(_inspector.HasFieldErrors) is { } block)
        {
            Status = $"Cannot save data asset. {block}";
            return false;
        }
        string yaml;
        try { yaml = state.Serialize(registry); }
        catch (Exception error)
        {
            Error = error.GetBaseException().Message;
            Status = $"Cannot save data asset: {Error}";
            Refresh();
            return false;
        }
        try { state.Save(yaml, project); }
        catch (Exception error)
        {
            Status = $"Cannot save data asset: {error.GetBaseException().Message}";
            return false;
        }
        Status = $"Saved data asset: {Path.GetFileName(state.Path)}";
        Refresh(clearError: true);
        return true;
    }
}
