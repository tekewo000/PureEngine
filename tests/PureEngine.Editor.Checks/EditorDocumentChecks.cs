using Microsoft.Extensions.DependencyInjection;
using PureEngine.Core;
using PureEngine.Editor;
using PureEngine.Runtime;

static class EditorDocumentChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Run(string parent)
    {
        var root = Path.Combine(parent, "DocumentChecks");
        Directory.CreateDirectory(root);
        using var documents = new EditorDocuments();
        var scene = documents.Scene;
        scene.MarkChanged();
        var prefab = new EditSceneStore(new Scene(), Path.Combine(root, "test.pure.prefab.yaml"));
        documents.AdoptPrefab(prefab, Guid.NewGuid());
        documents.ActivatePrefab(true);
        Check(ReferenceEquals(documents.Current, prefab) && documents.IsDirty,
            "Switching documents must preserve the main scene and its unsaved state.");
        documents.ActivatePrefab(false);
        Check(ReferenceEquals(documents.Current, scene), "Returning to Scene must restore its original owner.");
        documents.ClosePrefab();
        Check(documents.Prefab is null && !documents.IsPrefabActive && scene.IsDirty,
            "Closing a prefab must not discard the main scene.");

        var registry = new ComponentRegistry();
        registry.Register<MvvmDocumentAsset>("checks.mvvm-asset");
        var first = new DataAssetEditState(Path.Combine(root, "a.pure.asset.yaml"), new MvvmDocumentAsset(), Guid.NewGuid(), "checks.mvvm-asset") { Dirty = true };
        var second = new DataAssetEditState(Path.Combine(root, "missing", "b.pure.asset.yaml"), new MvvmDocumentAsset(), Guid.NewGuid(), "checks.mvvm-asset") { Dirty = true };
        documents.Table.Rows.AddRange([first, second]);
        documents.Table.RefreshOwnership();
        Check(ReferenceEquals(documents.Table.FindOwner(((MvvmDocumentAsset)first.Instance).Nested), first),
            "Nested edits must resolve to the owning table document.");
        var pending = documents.Table.PrepareSave(registry);
        try { DataAssetTableDocument.Save(pending); throw new InvalidOperationException("Expected an I/O failure."); }
        catch (DirectoryNotFoundException) { }
        Check(first.Dirty && second.Dirty && documents.Table.IsDirty,
            "A partially failed table save must leave every row dirty for retry.");
        Directory.CreateDirectory(Path.GetDirectoryName(second.Path)!);
        DataAssetTableDocument.Save(pending);
        Check(!documents.Table.IsDirty && File.Exists(second.Path), "A successful retry must save all rows and clear their dirty state.");
        first.Dirty = true;
        var migrated = first.Migrate(registry, registry);
        Check(migrated.Id == first.Id && migrated.Dirty && !ReferenceEquals(migrated.Instance, first.Instance),
            "Reload preparation must clone values and preserve identity and dirty state without publishing them.");

        ((MvvmDocumentAsset)second.Instance).Invalid = double.NaN;
        second.Dirty = true;
        var before = File.ReadAllText(first.Path);
        var rejected = false;
        try { _ = documents.Table.PrepareSave(registry); }
        catch (Exception) { rejected = true; }
        Check(rejected && File.ReadAllText(first.Path) == before && first.Dirty,
            "All rows must serialize successfully before any file is written.");
        CleanupOrder();
        Console.WriteLine("PASS: UI-independent document ownership, nested edits, save failures, migration, and cleanup.");
    }

    private static void CleanupOrder()
    {
        List<string> order = [];
        var documents = new EditorDocuments();
        documents.Scene.ReplaceServices(GameSession.Create(services =>
            services.AddScoped(_ => new DocumentCheckService(order)))).Dispose();
        _ = documents.Scene.Services.Services.GetRequiredService<DocumentCheckService>();
        documents.Scene.Current.AddEmpty().Attach(new DocumentCheckComponent(order));
        var failed = false;
        try { documents.Dispose(); }
        catch (AggregateException) { failed = true; }
        documents.Dispose();
        Check(failed && order.SequenceEqual(["component", "service"]),
            "Cleanup must release services after a component throws, and repeated cleanup must do nothing.");
    }

    private sealed class DocumentCheckService(List<string> order) : IDisposable
    {
        public void Dispose() => order.Add("service");
    }

    private sealed class DocumentCheckComponent(List<string> order) : IDisposable
    {
        public void Dispose()
        {
            order.Add("component");
            throw new InvalidOperationException("Expected cleanup failure.");
        }
    }
}

[DataAsset]
public sealed class MvvmDocumentAsset
{
    [Inspector] public MvvmDocumentValue Nested { get; set; } = new();
    [Inspector] public double Invalid { get; set; }
}

public sealed class MvvmDocumentValue
{
    [Inspector] public int Value { get; set; } = 42;
}
