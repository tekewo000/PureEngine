using PureEngine.Core;

namespace PureEngine.Editor;

/// <summary>Owns editing documents and their active scene without depending on a window.</summary>
public sealed class EditorDocuments : IDisposable
{
    private bool _disposed;
    public EditSceneStore Scene { get; } = new(new Scene());
    public EditSceneStore Current => IsPrefabActive ? Prefab! : Scene;
    public EditSceneStore? Prefab { get; private set; }
    public Guid PrefabId { get; private set; }
    public bool IsPrefabActive { get; private set; }
    public DataAssetEditState? Asset { get; set; }
    public DataAssetTableDocument Table { get; } = new();
    public HashSet<object> AssetOwned { get; } = [with(ReferenceEqualityComparer.Instance)];
    public bool IsDirty => Scene.IsDirty || Prefab is { IsDirty: true } || Asset is { Dirty: true } || Table.IsDirty;

    public void ActivatePrefab(bool active)
    {
        if (active && Prefab is null) throw new InvalidOperationException("No prefab document is open.");
        IsPrefabActive = active;
    }

    public void AdoptPrefab(EditSceneStore document, Guid id)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (Prefab is not null) throw new InvalidOperationException("Close the previous prefab before adopting its replacement.");
        Prefab = document;
        PrefabId = id;
    }

    public void ClosePrefab()
    {
        var previous = Prefab;
        Prefab = null;
        PrefabId = Guid.Empty;
        IsPrefabActive = false;
        if (previous is not null) DisposeScene(previous);
    }

    public void RefreshAssetOwnership()
    {
        AssetOwned.Clear();
        if (Asset is not null) DataAssetEditState.CollectObjects(Asset.Instance, AssetOwned);
    }

    public static DataAssetStore? BuildAssetStore(ProjectFile? project, ComponentRegistry registry)
    {
        if (project is null) return null;
        try
        {
            var store = DataAssetStore.ScanFolder(project.RootDirectory, registry, out var diagnostics);
            foreach (var diagnostic in diagnostics) Log.Engine.Warning(diagnostic);
            return store;
        }
        catch (Exception error)
        {
            Log.Engine.Error("Cannot scan data assets.", error);
            return null;
        }
    }

    public void SaveScene(string path, string yaml, ProjectFile? project)
    {
        project?.ValidateScenePath(path);
        SceneFile.Write(path, yaml);
        Scene.MarkSaved(path);
    }

    public void SavePrefab(ComponentRegistry registry, ProjectFile project)
    {
        var document = Prefab ?? throw new InvalidOperationException("No prefab document is open.");
        var path = document.Path ?? throw new InvalidOperationException("The prefab has no save path.");
        project.ValidatePrefabPath(path);
        PrefabFile.Save(path, document.Current, PrefabId, registry);
        document.MarkSaved(path);
    }

    public static void DisposeScene(EditSceneStore document)
    {
        var errors = new List<Exception>();
        try { ComponentAssets.DisposeComponents(document.Reset().OwnedComponents); }
        catch (Exception error) { errors.Add(error); }
        try { document.Services.Dispose(); }
        catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0) throw new AggregateException("Document cleanup failed.", errors);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Asset = null;
        AssetOwned.Clear();
        Table.Clear();
        Table.Type = null;
        var errors = new List<Exception>();
        try { ClosePrefab(); }
        catch (Exception error) { errors.Add(error); }
        try { DisposeScene(Scene); }
        catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0) throw new AggregateException("Documents cleanup failed.", errors);
    }
}
