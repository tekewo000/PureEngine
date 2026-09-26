using PureEngine.Core;

namespace PureEngine.Editor;

/// <summary>Table selection, row sizes, save state, and available asset types.</summary>
public sealed class DataAssetTableViewModel : EditorObservable
{
    public const double DefaultRowHeight = 148;
    private readonly DataAssetTableDocument _document;
    private readonly InspectorViewModel _inspector;
    private bool _hasProject;
    private readonly Dictionary<string, double> _heights = [with(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)];

    public DataAssetTableViewModel(DataAssetTableDocument document, InspectorViewModel inspector)
    {
        _document = document;
        _inspector = inspector;
        inspector.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(InspectorViewModel.IsReadOnly)) Refresh();
        };
    }

    public IReadOnlyList<DataAssetDescriptor> Types { get; private set; } = [];
    public IReadOnlyList<DataAssetEditState> Rows => _document.Rows;
    public DataAssetDescriptor? SelectedType => _document.Type;
    public bool IsEnabled => !_inspector.IsReadOnly;
    public bool CanSave => _document.IsDirty && IsEnabled;
    public bool CanAdd => SelectedType is not null && IsEnabled;
    public string Title => _document.IsDirty ? "Data Assets *" : "Data Assets";
    public string Status { get; private set; } = "Select a type.";
    public string OperationStatus { get; private set; } = "";

    public void RefreshTypes(ProjectFile? project, ProjectComponents components)
    {
        var selectedId = SelectedType?.TypeId;
        _hasProject = project is not null;
        Types = project is null ? [] : [.. DataAssetDescriptor.DescribeAll(components.Registry, out _, components.DataAssetTypes)
            .OrderBy(descriptor => descriptor.MenuPath, StringComparer.Ordinal)];
        _document.Type = Types.FirstOrDefault(descriptor => descriptor.TypeId == selectedId);
        Changed(nameof(Types));
        Refresh();
    }

    public void SelectType(DataAssetDescriptor? type)
    {
        _document.Type = type;
        Refresh();
    }

    public double RowHeight(DataAssetEditState row) => _heights.GetValueOrDefault(row.Path, DefaultRowHeight);

    public void SetRowHeight(DataAssetEditState row, double height)
    {
        if (IsEnabled) _heights[row.Path] = Math.Clamp(height, 80, 640);
    }

    public void Clear()
    {
        _document.Clear();
        _heights.Clear();
        Refresh();
    }

    public void Refresh()
    {
        Status = !_hasProject ? "Open a project." : SelectedType is not { } type ? "Select a type."
            : Rows.Count == 0 ? $"No {type.DisplayName} rows — Add Row."
            : $"{Rows.Count} row(s) of {type.DisplayName}.";
        Changed(nameof(Rows));
        Changed(nameof(SelectedType));
        Changed(nameof(IsEnabled));
        Changed(nameof(CanSave));
        Changed(nameof(CanAdd));
        Changed(nameof(Title));
        Changed(nameof(Status));
    }

    public bool Save(ComponentRegistry registry)
    {
        if (!IsEnabled) return false;
        if (SelectedType is null || !_document.IsDirty) return true;
        if (EditorOperationGate.SaveBlockReason(_inspector.HasFieldErrors) is { } block)
        {
            OperationStatus = $"Cannot save data asset table. {block}";
            return false;
        }
        List<(DataAssetEditState Row, string Yaml)> pending;
        try { pending = _document.PrepareSave(registry); }
        catch (Exception error)
        {
            Status = error.GetBaseException().Message;
            Changed(nameof(Status));
            OperationStatus = $"Cannot save data asset table: {Status}";
            return false;
        }
        try { DataAssetTableDocument.Save(pending); }
        catch (Exception error)
        {
            OperationStatus = $"Cannot save data asset table: {error.GetBaseException().Message}";
            return false;
        }
        OperationStatus = $"Saved {SelectedType.DisplayName} table: {pending.Count} row(s).";
        Refresh();
        return true;
    }

    public void Add(ProjectFile project, ComponentRegistry registry)
    {
        EnsureEditable();
        var row = _document.Add(project, registry);
        OperationStatus = $"Added {SelectedType!.DisplayName} row: {Path.GetRelativePath(project.RootDirectory, row.Path).Replace('\\', '/')}";
        Refresh();
    }

    public void Duplicate(DataAssetEditState source, ProjectFile project, ComponentRegistry registry)
    {
        EnsureEditable();
        var row = _document.Duplicate(source, project, registry);
        SetRowHeight(row, RowHeight(source));
        OperationStatus = $"Duplicated row: {Path.GetRelativePath(project.RootDirectory, row.Path).Replace('\\', '/')}";
        Refresh();
    }

    public void Delete(DataAssetEditState row)
    {
        EnsureEditable();
        _document.Delete(row);
        _heights.Remove(row.Path);
        Refresh();
    }

    private void EnsureEditable()
    {
        if (!IsEnabled) throw new InvalidOperationException("Cannot edit the data asset table while playing.");
    }

    public string? Rescan(ProjectFile project, ComponentRegistry registry)
    {
        var error = _document.Scan(project, registry);
        Refresh();
        if (error is not null)
        {
            Status = error;
            Changed(nameof(Status));
        }
        return error;
    }
}
