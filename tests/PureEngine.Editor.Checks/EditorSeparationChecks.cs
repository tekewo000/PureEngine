using PureEngine.Runtime;
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

        store.Services.Dispose();
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
        using var owner = new ProjectComponents();
        owner.Adopt(UserCodeCompiler.CompileProject(root));
        var state = new EditSceneStore(new Scene());
        try
        {
            var playerType = owner.GetTypesForFile(file).Single();
            var item = state.Current.AddEmpty();
            item.Rename("Hero");
            var component = state.Services.Factory(playerType);
            item.Attach(component);
            playerType.GetField("Health")!.SetValue(component, 73);
            item.SetStartPriority(component, -9);
            item.SetUpdatePriority(component, 5);
            state.MarkChanged();
            var oldServices = state.Services;
            File.WriteAllText(file, Source(2));
            var result = coordinator.Apply(state, owner, UserCodeCompiler.CompileProject(root));
            Check(result.Adopted && result.Error is null && !coordinator.IsReloading,
                "Successful preparation must adopt the code, scene and services together.");
            var restored = state.Current.Objects.Single();
            var renewed = restored.Components.Single();
            Check(restored.Id == item.Id && restored.Name == "Hero" && state.IsDirty,
                "Identity and unsaved state must survive adoption.");
            Check((int)renewed.GetType().GetField("Health")!.GetValue(renewed)! == 73
                && (int)renewed.GetType().GetField("Added")!.GetValue(renewed)! == 42,
                "Current values must survive and new fields use their initializers.");
            Check(restored.GetStartPriority(renewed) == -9 && restored.GetUpdatePriority(renewed) == 5,
                "Priorities must survive adoption.");
            try { _ = oldServices.Services; throw new Exception("Old services must be disposed."); }
            catch (ObjectDisposedException) { }
            var goodScene = state.Current;
            var goodServices = state.Services;
            foreach (var broken in new[] { Source(2) + " this is broken;", Source(2).Replace("int Health = 10", "float Health = 10") })
            {
                File.WriteAllText(file, broken);
                var failed = coordinator.Apply(state, owner, UserCodeCompiler.CompileProject(root));
                Check(!failed.Adopted && ReferenceEquals(state.Current, goodScene)
                    && ReferenceEquals(state.Services, goodServices)
                    && owner.Registry.Types.Contains(renewed.GetType()),
                    "Compilation and schema failures must preserve the scene, services and registry.");
            }
        }
        finally
        {
            foreach (var component in state.Current.Objects.SelectMany(o => o.Components).OfType<IDisposable>()) component.Dispose();
            state.Services.Dispose();
        }
    }
}
