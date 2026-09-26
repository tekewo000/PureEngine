using PureEngine.Core;
using PureEngine.Runtime;
using Microsoft.Extensions.DependencyInjection;

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
    public LocalizationDocument Localization { get; } = new();
    public HashSet<object> AssetOwned { get; } = [with(ReferenceEqualityComparer.Instance)];
    public bool IsDirty => Scene.IsDirty || Prefab is { IsDirty: true } || Asset is { Dirty: true } || Table.IsDirty || Localization.IsDirty;

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

    public EditedDocumentKind MarkChanged(object? owner)
    {
        if (owner is not null && Table.Owned.Contains(owner))
        {
            if (Table.FindOwner(owner) is { } row) row.Dirty = true;
            Table.RefreshOwnership();
            return EditedDocumentKind.Table;
        }
        if (owner is not null && Asset is not null && AssetOwned.Contains(owner))
        {
            Asset.Dirty = true;
            RefreshAssetOwnership();
            return EditedDocumentKind.DataAsset;
        }
        Current.MarkChanged();
        return EditedDocumentKind.Scene;
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

    public static PrefabCatalog BuildPrefabCatalog(ProjectFile? project)
    {
        if (project is null) return PrefabCatalog.Empty;
        try
        {
            var catalog = PrefabCatalog.ScanFolder(project.RootDirectory, out var diagnostics);
            foreach (var diagnostic in diagnostics) Log.Engine.Warning(diagnostic);
            return catalog;
        }
        catch (Exception error)
        {
            Log.Engine.Error("Cannot scan prefabs.", error);
            return PrefabCatalog.Empty;
        }
    }

    /// <summary>Loads the project localization table file. A missing file loads as an empty table, never null.</summary>
    public static LocalizationStore BuildLocalizationStore(ProjectFile? project)
    {
        if (project is null) return new LocalizationStore();
        try
        {
            var (store, diagnostics) = LocalizationStore.LoadFile(project.LocalizationPath);
            foreach (var diagnostic in diagnostics) Log.Engine.Warning(diagnostic);
            return store;
        }
        catch (Exception error)
        {
            Log.Engine.Error("Cannot scan localization.", error);
            return new LocalizationStore();
        }
    }

    public void SaveScene(string path, string yaml, ProjectFile? project)
    {
        project?.ValidateScenePath(path);
        SceneFile.Write(path, yaml);
        Scene.MarkSaved(path);
    }

    public Scene ReadScene(string path, ProjectFile? project, ComponentRegistry registry, out bool changed)
    {
        project?.ValidateScenePath(path);
        var serializer = new SceneSerializer(registry, BuildAssetStore(project, registry), BuildPrefabCatalog(project), BuildLocalizationStore(project));
        return serializer.Deserialize(File.ReadAllText(path), out changed, Current.Services.Factory);
    }

    public void ReplaceScene(Scene next, string? path)
    {
        var previous = Current.Replace(next, path, dirty: false);
        if (!ReferenceEquals(previous, next)) ComponentAssets.DisposeComponents(previous.OwnedComponents);
    }

    public static (EditSceneStore Document, Guid Id) PreparePrefab(string path, ProjectFile project, ProjectComponents components)
    {
        project.ValidatePrefabPath(path);
        var candidate = new EditSceneStore(new Scene());
        try
        {
            var assets = BuildAssetStore(project, components.Registry);
            var localization = BuildLocalizationStore(project);
            candidate.ReplaceServices(GameSession.Create(services =>
            {
                GameServices.ForProject(components)(services);
                if (assets is not null) services.AddSingleton(assets);
                services.AddSingleton(localization);
            })).Dispose();
            var restored = PrefabFile.OpenForEditing(path, components.Registry, out var id, out var changed,
                assets, BuildPrefabCatalog(project), candidate.Services.Factory, localization);
            candidate.Replace(restored, Path.GetFullPath(path), changed);
            return (candidate, id);
        }
        catch (Exception error)
        {
            try { DisposeScene(candidate); }
            catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
            throw;
        }
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
        Localization.Clear();
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

public enum EditedDocumentKind
{
    Scene,
    DataAsset,
    Table,
    Localization,
}
