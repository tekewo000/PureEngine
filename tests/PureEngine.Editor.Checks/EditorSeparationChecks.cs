using PureEngine.Core;
using PureEngine.Editor;

static class EditorSeparationChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static void Run(string parent)
    {
        GateWithoutControls();
        StoreOwnership();
        CoordinatorWithoutWindow(parent);
        Console.WriteLine("PASS: editor separation gate/store/reload without Window.");
    }

    private static void GateWithoutControls()
    {
        Check(EditorOperationGate.ReloadBlockReason(false, false, false) is null, "Reload should be allowed when idle.");
        Check(EditorOperationGate.ReloadBlockReason(true, false, false) is not null, "Play must defer reload.");
        Check(EditorOperationGate.ReloadBlockReason(false, true, false) is not null, "FileBusy must defer reload.");
        Check(EditorOperationGate.ReloadBlockReason(false, false, true) is not null, "Input errors must defer reload.");

        Check(EditorOperationGate.PlayBlockReason(false, false, false) is null, "Play should be allowed when idle.");
        Check(EditorOperationGate.PlayBlockReason(false, true, false) is not null, "FileBusy must block Play.");
        Check(EditorOperationGate.PlayBlockReason(false, false, true) is not null, "Input errors must block Play.");

        Check(EditorOperationGate.FileOperationBlockReason(false, false) is null, "File operation should be allowed when idle.");
        Check(EditorOperationGate.FileOperationBlockReason(true, false) is not null, "Play must block file operations.");

        Check(EditorOperationGate.SaveBlockReason(false) is null, "Save should be allowed without input errors.");
        Check(EditorOperationGate.SaveBlockReason(true) is not null, "Input errors must block save.");

        Check(EditorOperationGate.NeedsUnsavedConfirmation(true, false), "Dirty must need confirmation.");
        Check(EditorOperationGate.NeedsUnsavedConfirmation(false, true), "Input errors must need confirmation.");
        Check(!EditorOperationGate.NeedsUnsavedConfirmation(false, false), "Clean must not need confirmation.");

        Check(EditorOperationGate.CanReloadNow(true, false, false, false, false), "Pending reload should run when idle.");
        Check(!EditorOperationGate.CanReloadNow(false, false, false, false, false), "No pending must not run.");
        Check(!EditorOperationGate.CanReloadNow(true, true, false, false, false), "Reloading must not reenter.");
        Check(!EditorOperationGate.CanReloadNow(true, false, true, false, false), "Play must defer pending reload.");
    }

    private static void StoreOwnership()
    {
        var first = new Scene();
        var store = new EditSceneStore(first, "a.pure.scene.yaml", dirty: false);
        Check(ReferenceEquals(store.Current, first) && store.Path == "a.pure.scene.yaml" && !store.IsDirty, "Store must hold initial scene.");

        store.MarkChanged();
        Check(store.IsDirty, "MarkChanged must set dirty.");

        store.MarkSaved("b.pure.scene.yaml");
        Check(store.Path == "b.pure.scene.yaml" && !store.IsDirty, "MarkSaved must update path and clear dirty.");

        var next = new Scene();
        var previous = store.Replace(next, "c.pure.scene.yaml", dirty: true);
        Check(ReferenceEquals(previous, first) && ReferenceEquals(store.Current, next)
            && store.Path == "c.pure.scene.yaml" && store.IsDirty, "Replace must adopt next and return previous.");

        store.SetPath("d.pure.scene.yaml");
        Check(store.Path == "d.pure.scene.yaml" && ReferenceEquals(store.Current, next), "SetPath must keep scene.");

        var reset = store.Reset();
        Check(ReferenceEquals(reset, next) && store.Current.Objects.Count == 0
            && store.Path is null && !store.IsDirty, "Reset must return previous and clear.");
    }

    private static string Source(int version) => $$"""
        using PureEngine.Core;
        using PureEngine.Core.Attributes;
        namespace Game;
        public class Player
        {
            [Inspector] public int Health = 10;
            {{(version > 1 ? "[Inspector] public int Added = 42;" : "")}}
            public int Version => {{version}};
            [Start] public void Start() { }
            [Update] public void Update(float dt) { }
        }
        """;

    private static void CoordinatorWithoutWindow(string parent)
    {
        // WindowやControlを生成せず、中核処理だけを検証する。
        var root = Path.Combine(parent, "SeparationReload");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "Player.cs");
        File.WriteAllText(file, Source(1));

        var coordinator = new UserCodeReloadCoordinator();
        Check(!coordinator.HasPending && !coordinator.IsReloading, "Initial reload state must be idle.");

        coordinator.RequestPending();
        Check(coordinator.HasPending, "RequestPending must set pending.");
        Check(coordinator.ShouldReloadNow(false, false, false), "Pending reload should run when idle.");
        Check(!coordinator.ShouldReloadNow(true, false, false), "Play must defer pending reload.");
        Check(!coordinator.ShouldReloadNow(false, false, true), "Input errors must defer pending reload.");
        coordinator.ClearPending();

        using var services = GameSession.Create();
        var currentRegistry = new ComponentRegistry();
        var compiled1 = UserCodeCompiler.CompileProject(root);
        Check(compiled1.Success, "Setup compilation failed.");
        try
        {
            foreach (var type in compiled1.AttachableTypes)
                currentRegistry.RegisterType(type, UserCodeCompiler.TypeIdFor(type));
            var playerType1 = compiled1.AttachableTypes.Single(t => t.Name == "Player");
            var scene = new Scene();
            var item = scene.AddEmpty();
            item.Rename("Hero");
            var id = item.Id;
            var component1 = services.Factory(playerType1);
            playerType1.GetField("Health")!.SetValue(component1, 73);
            item.Attach(component1);
            item.SetStartPriority(component1, -9);
            item.SetUpdatePriority(component1, 5);

            // Deferred: gate blocks before any compilation.
            var compileCalled = false;
            var deferred = coordinator.Reload(scene, currentRegistry, services.Factory, root,
                isPlaying: true, fileBusy: false, hasInputErrors: false,
                compile: _ => { compileCalled = true; return compiled1; });
            Check(deferred.IsDeferred && !compileCalled && coordinator.HasPending, "Play must defer without compiling.");

            // Success: v2 adds a field, preserves identity/values/priority.
            File.WriteAllText(file, Source(2));
            var published = new List<UserCodeCompileResult>();
            var outcome = coordinator.Reload(scene, currentRegistry, services.Factory, root,
                isPlaying: false, fileBusy: false, hasInputErrors: false,
                createCandidate: result => UserCodeReloadCoordinator.BuildCandidateRegistry(currentRegistry, result),
                publish: result => published.Add(result),
                disposeComponents: components =>
                {
                    foreach (var component in components.Distinct(ReferenceEqualityComparer.Instance).Reverse())
                        if (component is IDisposable disposable) disposable.Dispose();
                });
            Check(!coordinator.HasPending && !coordinator.IsReloading, "Reload must clear pending state.");
            foreach (var diagnostic in outcome.Diagnostics)
            {
                if (diagnostic.IsError) throw new Exception("Unexpected error: " + UserCodeCompiler.FormatDiagnostic(diagnostic));
            }
            Check(outcome.Success && outcome.MigratedScene is not null && published.Count == 1,
                "Success must migrate and publish once after preparation.");
            var migrated = outcome.MigratedScene!;
            var restored = migrated.Objects.Single();
            var component2 = restored.Components.Single();
            Check(restored.Id == id && restored.Name == "Hero", "Reload must preserve identity.");
            Check((int)component2.GetType().GetField("Health")!.GetValue(component2)! == 73, "Reload must preserve unsaved values.");
            Check(restored.GetStartPriority(component2) == -9 && restored.GetUpdatePriority(component2) == 5,
                "Reload must preserve Priority.");
            Check((int)component2.GetType().GetField("Added")!.GetValue(component2)! == 42,
                "New fields must keep initializers.");
            Check(ReferenceEquals(scene.Objects.Single().Components.Single(), component1),
                "Old scene must stay untouched until the caller adopts the migrated scene.");
            // Old scene is still owned by the caller; release it here in Component->service order.
            foreach (var component in scene.Objects.SelectMany(o => o.Components).Distinct(ReferenceEqualityComparer.Instance).Reverse())
                if (component is IDisposable disposable) disposable.Dispose();
            foreach (var component in migrated.Objects.SelectMany(o => o.Components).Distinct(ReferenceEqualityComparer.Instance).Reverse())
                if (component is IDisposable disposable) disposable.Dispose();

            // Compile failure: publish must not run, old state untouched.
            File.WriteAllText(file, Source(2) + "\nthis is broken;");
            published.Clear();
            var failed = coordinator.Reload(scene, currentRegistry, services.Factory, root,
                isPlaying: false, fileBusy: false, hasInputErrors: false,
                createCandidate: result => UserCodeReloadCoordinator.BuildCandidateRegistry(currentRegistry, result),
                publish: result => published.Add(result));
            Check(!failed.Success && !failed.IsDeferred && failed.Compiled is not null && !failed.Compiled.Success,
                "Compile failure must be reported.");
            Check(published.Count == 0, "Compile success alone must not publish; failure must not publish.");
            Check(ReferenceEquals(scene.Objects.Single().Components.Single(), component1), "Compile failure must keep old instances.");

            // Schema failure: type change must not publish.
            File.WriteAllText(file, Source(2).Replace("int Health = 10", "float Health = 10"));
            published.Clear();
            var schemaFailed = coordinator.Reload(scene, currentRegistry, services.Factory, root,
                isPlaying: false, fileBusy: false, hasInputErrors: false,
                createCandidate: result => UserCodeReloadCoordinator.BuildCandidateRegistry(currentRegistry, result),
                publish: result => published.Add(result));
            Check(!schemaFailed.Success && published.Count == 0, "Schema failure must not publish.");
            Check(ReferenceEquals(scene.Objects.Single().Components.Single(), component1), "Schema failure must keep old scene.");

            // Input-error deferral passes values, not controls.
            coordinator.RequestPending();
            Check(!coordinator.ShouldReloadNow(false, false, true), "Input errors must defer even with pending.");
            coordinator.ClearPending();
        }
        finally
        {
            UserCodeReloadCoordinator.Unload(compiled1);
        }
    }
}
