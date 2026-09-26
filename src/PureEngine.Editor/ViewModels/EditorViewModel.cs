using PureEngine.Core;

namespace PureEngine.Editor;

/// <summary>Composition root for editing documents and pane presentation state.</summary>
public sealed class EditorViewModel : EditorObservable, IDisposable
{
    public EditorViewModel()
    {
        SceneSerializer = new SceneSerializer(Components.Registry);
        Inspector = new InspectorViewModel(Documents);
        Hierarchy = new HierarchyViewModel(Documents, Inspector);
        Hierarchy.StatusChanged += SetStatus;
        DataAsset = new DataAssetViewModel(Documents, Inspector);
        DataAssetTable = new DataAssetTableViewModel(Documents.Table, Inspector);
        Play = new PlayViewModel(this);
        Compilation = new CompilationViewModel(this);
        StartPlayCommand = new EditorCommand(Play.Start, () => !IsDisposed && !Play.IsPlaying);
        StopPlayCommand = new EditorCommand(() => Play.Stop(), () => !IsDisposed && Play.IsPlaying);
        SaveDataAssetCommand = new EditorCommand(() => _ = RunSave(SaveDataAsset), () => CanEdit && !FileBusy && DataAsset.CanSave);
        SaveTableCommand = new EditorCommand(() => _ = RunSave(SaveTable), () => CanEdit && !FileBusy && DataAssetTable.CanSave);
        SavePrefabCommand = new EditorCommand(() => _ = RunSave(SavePrefab), () => CanSavePrefab && !FileBusy);
        DataAsset.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(DataAssetViewModel.CanSave)) SaveDataAssetCommand.Refresh();
        };
        DataAssetTable.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(DataAssetTableViewModel.CanSave)) SaveTableCommand.Refresh();
        };
        Play.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(PlayViewModel.IsPlaying)) RefreshAvailability();
        };
        Inspector.DocumentEdited += _ => RefreshDocumentState();
        Hierarchy.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(HierarchyViewModel.SelectedNodes)) RefreshDocumentState();
        };
    }

    public EditorDocuments Documents { get; } = new();
    public HierarchyViewModel Hierarchy { get; }
    public InspectorViewModel Inspector { get; }
    public DataAssetViewModel DataAsset { get; }
    public DataAssetTableViewModel DataAssetTable { get; }
    public ProjectPaneViewModel Project { get; } = new();
    public ConsoleViewModel Console { get; } = new();
    public PlayViewModel Play { get; }
    public CompilationViewModel Compilation { get; }
    public EditorCommand StartPlayCommand { get; }
    public EditorCommand StopPlayCommand { get; }
    public EditorCommand SaveDataAssetCommand { get; }
    public EditorCommand SaveTableCommand { get; }
    public EditorCommand SavePrefabCommand { get; }
    public SceneSerializer SceneSerializer { get; private set; }
    public ProjectComponents Components { get; private set; } = new();
    public ProjectFile? ProjectFile { get; private set; }
    public bool IsDisposed { get; private set; }
    public event Action? BeforeSceneSave;
    public event Action<EditedDocumentKind>? DocumentSaved;

    public bool FileBusy
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;
            Changed(nameof(IsFileIdle));
            SaveDataAssetCommand.Refresh();
            SaveTableCommand.Refresh();
            SavePrefabCommand.Refresh();
        }
    }
    public bool IsFileIdle => !FileBusy;
    public bool CanEdit => !IsDisposed && !Play.IsPlaying;
    public bool CanSetStartup => CanEdit && ProjectFile is not null;
    public bool CanSaveAs => CanEdit && !Documents.IsPrefabActive;
    public bool CanSavePrefab => CanEdit && Documents.Prefab is not null;
    public bool CanDeleteSelection => CanEdit && Hierarchy.SelectedObjects().Any(item => !Documents.IsPrefabActive || item.Parent is not null);
    public bool CanSaveSelection => CanEdit && Hierarchy.Primary is not null;
    public bool HasProject => ProjectFile is not null;
    public bool HasPrefab => Documents.Prefab is not null;
    public bool IsPrefabEditing => Documents.IsPrefabActive;
    public string Status { get; private set; } = "";
    public bool StatusIsError { get; private set; }
    public string StatusColor => StatusIsError ? "#FF9E99" : "#B8BDC5";
    public string WindowTitle => $"{(Documents.Current.IsDirty ? "* " : "")}{Path.GetFileName(Documents.Current.Path) ?? "Untitled"} — {(ProjectFile is null ? "" : ProjectFile.Document.Name + " — ")}PureEngine Editor";
    public string SceneTabTitle => Documents.Current.IsDirty ? "Scene View *" : "Scene View";
    public string PrefabTabTitle => Documents.Prefab?.IsDirty == true ? "Prefab Editor *" : "Prefab Editor";
    public string PrefabTitle => $"{Path.GetFileName(Documents.Prefab?.Path)}{(Documents.Prefab?.IsDirty == true ? " *" : "")}";
    public string? PrefabPath => Documents.Prefab?.Path;
    public string PrefabRelativePath => PrefabPath is { } path && ProjectFile is not null ? Path.GetRelativePath(ProjectFile.RootDirectory, path) : "";
    public string StuffsContext => $"Prefab: {Path.GetFileName(PrefabPath)}";
    public string SaveLabel => IsPrefabEditing ? "Save Prefab" : "Save Scene";

    public void AdoptProject(ProjectSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Documents.Scene.ReplaceServices(session.EditServices).Dispose();
        var previous = Components;
        Components = session.Components;
        SceneSerializer = new SceneSerializer(Components.Registry);
        session.TransferOwnership();
        ProjectFile = session.Project;
        var scene = Documents.Scene.Replace(session.Scene, session.Project.StartupScenePath, session.SceneNeedsSave);
        try { ComponentAssets.DisposeComponents(scene.OwnedComponents); }
        finally { previous.Dispose(); }
        Compilation.Start(ProjectFile, session.TakeUserCodeCache());
        Compilation.SetElapsed(session.InitialCompileElapsed);
        RefreshDocumentState();
    }

    public void SetStatus(string message, bool error = false)
    {
        Status = message;
        StatusIsError = error;
        Changed(nameof(Status));
        Changed(nameof(StatusIsError));
        Changed(nameof(StatusColor));
    }

    public void RequestReloadApply() => Compilation.ApplyPending();

    public async Task RunFileOperation(Func<Task> operation)
    {
        if (IsDisposed) return;
        var block = EditorOperationGate.FileOperationBlockReason(Play.IsPlaying, FileBusy);
        if (block is not null)
        {
            if (Play.IsPlaying) SetStatus(block, true);
            return;
        }
        FileBusy = true;
        try { await operation(); }
        catch (Exception error) { SetStatus($"Operation failed: {error.GetBaseException().Message}", true); }
        finally { FileBusy = false; Compilation.ApplyPending(); }
    }

    private Task RunSave(Func<bool> save) => RunFileOperation(() =>
    {
        save();
        return Task.CompletedTask;
    });

    public string? PrepareSceneSave()
    {
        BeforeSceneSave?.Invoke();
        var block = EditorOperationGate.SaveBlockReason(!IsPrefabEditing && Documents.Asset is null && Inspector.HasInputErrors);
        if (block is null) return SceneSerializer.Serialize(Documents.Scene.Current);
        SetStatus($"Cannot save. {block}", true);
        return null;
    }

    public void SaveScene(string path, string yaml)
    {
        Documents.SaveScene(path, yaml, ProjectFile);
        Project.SelectedFile = path;
        RefreshDocumentState();
        DocumentSaved?.Invoke(EditedDocumentKind.Scene);
        SetStatus($"Saved: {path}");
    }

    public bool SavePrefab()
    {
        BeforeSceneSave?.Invoke();
        if (Documents.Prefab is null || !CanEdit) return false;
        if (IsPrefabEditing && Documents.Asset is null && Inspector.HasInputErrors)
        {
            SetStatus("Cannot save prefab. Fix the Inspector input errors.", true);
            return false;
        }
        var path = Documents.Prefab.Path!;
        Documents.SavePrefab(Components.Registry, ProjectFile!);
        RefreshDocumentState();
        SetStatus($"Saved prefab: {Path.GetFileName(path)}");
        return true;
    }

    public bool SaveDataAsset()
    {
        if (!CanEdit) return false;
        if (Documents.Asset is null) return true;
        var saved = DataAsset.Save(Components.Registry, ProjectFile);
        if (saved) DocumentSaved?.Invoke(EditedDocumentKind.DataAsset);
        SetStatus(DataAsset.Status, !saved);
        return saved;
    }

    public bool SaveTable()
    {
        if (!CanEdit) return false;
        if (Documents.Table.Type is null || !Documents.Table.IsDirty) return true;
        var saved = DataAssetTable.Save(Components.Registry);
        if (saved) DocumentSaved?.Invoke(EditedDocumentKind.Table);
        SetStatus(DataAssetTable.OperationStatus, !saved);
        return saved;
    }

    internal void RefreshAfterCodeAdoption()
    {
        Hierarchy.RebuildRoots();
        Inspector.Select(Hierarchy.Primary?.Ref);
        DataAsset.Refresh();
        DataAssetTable.RefreshTypes(ProjectFile, Components);
        Project.RefreshDirectories(ProjectFile);
        Project.RefreshFiles(ProjectFile, Components, Documents.Scene.Path);
        RefreshDocumentState();
    }

    private void RefreshAvailability()
    {
        Inspector.IsReadOnly = Play.IsPlaying;
        StartPlayCommand.Refresh();
        StopPlayCommand.Refresh();
        RefreshDocumentState();
    }

    public void RefreshDocumentState()
    {
        Changed(nameof(CanEdit));
        Changed(nameof(CanSetStartup));
        Changed(nameof(CanSaveAs));
        Changed(nameof(CanSavePrefab));
        Changed(nameof(CanDeleteSelection));
        Changed(nameof(CanSaveSelection));
        Changed(nameof(HasProject));
        Changed(nameof(HasPrefab));
        Changed(nameof(IsPrefabEditing));
        Changed(nameof(WindowTitle));
        Changed(nameof(SceneTabTitle));
        Changed(nameof(PrefabTabTitle));
        Changed(nameof(PrefabTitle));
        Changed(nameof(PrefabPath));
        Changed(nameof(PrefabRelativePath));
        Changed(nameof(StuffsContext));
        Changed(nameof(SaveLabel));
        SavePrefabCommand.Refresh();
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        var errors = new List<Exception>();
        try { Compilation.Dispose(); }
        catch (Exception error) { errors.Add(error); }
        try { Play.Dispose(); }
        catch (Exception error) { errors.Add(error); }
        try { Documents.Dispose(); }
        catch (Exception error) { errors.Add(error); }
        try { Components.Dispose(); }
        catch (Exception error) { errors.Add(error); }
        Hierarchy.Clear();
        Inspector.Clear();
        Inspector.IsReadOnly = true;
        Project.Clear();
        DataAsset.Refresh();
        DataAssetTable.RefreshTypes(null, Components);
        StartPlayCommand.Refresh();
        StopPlayCommand.Refresh();
        RefreshDocumentState();
        if (errors.Count != 0) throw new AggregateException("Editor cleanup failed.", errors);
    }
}
