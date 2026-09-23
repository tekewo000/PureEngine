using PureEngine.Runtime;
using PureEngine.Core;
using PureEngine.Editor;

static class ProjectIsolationChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static string Source(int marker) => $$"""
        using PureEngine.Core;
        namespace Game;
        public class Shared
        {
            [Inspector] public int Value = {{marker}};
            public int Marker => {{marker}};
        }
        """;

    public static void Run(string parent)
    {
        var root = Path.Combine(parent, "Isolation");
        Directory.CreateDirectory(root);

        // Hold two projects at once. Keep them independent, including same-name classes.
        using var createdA = ProjectSession.Create(root, "ProjA");
        using var createdB = ProjectSession.Create(root, "ProjB");
        var dirA = createdA.Project.RootDirectory;
        var dirB = createdB.Project.RootDirectory;
        createdA.Dispose();
        createdB.Dispose();
        File.WriteAllText(Path.Combine(dirA, "Shared.cs"), Source(10));
        File.WriteAllText(Path.Combine(dirB, "Shared.cs"), Source(20));

        using var sessionA = ProjectSession.Open(Path.Combine(dirA, "Project.pure.project.yaml"));
        using var sessionB = ProjectSession.Open(Path.Combine(dirB, "Project.pure.project.yaml"));
        Check(sessionA.Components.UserTypes.Count == 1 && sessionB.Components.UserTypes.Count == 1,
            "Both projects must resolve their own user type.");
        Check(!ReferenceEquals(sessionA.Components.Registry, sessionB.Components.Registry),
            "Registries must not be shared.");
        var typeA = sessionA.Components.Registry.GetType("user.Game.Shared");
        var typeB = sessionB.Components.Registry.GetType("user.Game.Shared");
        Check(!ReferenceEquals(typeA, typeB) && typeA.FullName == typeB.FullName,
            "Same-name classes must resolve to independent types.");
        Check((int)typeA.GetProperty("Marker")!.GetValue(Activator.CreateInstance(typeA)!)! == 10
            && (int)typeB.GetProperty("Marker")!.GetValue(Activator.CreateInstance(typeB)!)! == 20,
            "Same-name classes must carry their own code.");

        // Type resolution, saving, and Play must stay independent.
        var itemA = sessionA.Scene.AddEmpty();
        itemA.Rename("A");
        Check(sessionA.Components.TryAttach(itemA, typeA), "Project A must attach its own type.");
        typeA.GetField("Value")!.SetValue(itemA.Components.Single(), 11);
        var yamlA = new SceneSerializer(sessionA.Components.Registry).Serialize(sessionA.Scene);
        var restoredA = new SceneSerializer(sessionA.Components.Registry).Deserialize(yamlA);
        Check((int)restoredA.Objects.Single().Components.Single().GetType().GetField("Value")!
            .GetValue(restoredA.Objects.Single().Components.Single())! == 11,
            "Project A save/load must preserve its own values.");

        var itemB = sessionB.Scene.AddEmpty();
        itemB.Rename("B");
        Check(sessionB.Components.TryAttach(itemB, typeB), "Project B must attach its own type.");
        typeB.GetField("Value")!.SetValue(itemB.Components.Single(), 22);
        var yamlB = new SceneSerializer(sessionB.Components.Registry).Serialize(sessionB.Scene);
        var restoredB = new SceneSerializer(sessionB.Components.Registry).Deserialize(yamlB);
        Check((int)restoredB.Objects.Single().Components.Single().GetType().GetField("Value")!
            .GetValue(restoredB.Objects.Single().Components.Single())! == 22,
            "Project B save/load must preserve its own values.");

        using (var playA = PlaySession.Prepare(sessionA.Scene, sessionA.Components.Registry, GameServices.ForProject(sessionA.Components)))
        {
            playA.Start();
            playA.Step(0.016f);
            Check(playA.Runtime.IsRunning, "Project A Play must run.");
        }
        using (var playB = PlaySession.Prepare(sessionB.Scene, sessionB.Components.Registry, GameServices.ForProject(sessionB.Components)))
        {
            playB.Start();
            playB.Step(0.016f);
            Check(playB.Runtime.IsRunning, "Project B Play must run.");
        }
        var copyB = new SceneSerializer(sessionB.Components.Registry).Clone(sessionB.Scene);
        Check((int)copyB.Objects.Single().Components.Single().GetType().GetField("Value")!
            .GetValue(copyB.Objects.Single().Components.Single())! == 22,
            "Project B must be unaffected by Project A Play.");

        // Reloading one side must not change the other.
        File.WriteAllText(Path.Combine(dirA, "Shared.cs"), Source(30));
        var recompiledA = UserCodeCompiler.CompileProject(dirA);
        Check(recompiledA.Success, "Project A recompile must succeed.");
        var candidateA = sessionA.Components.CreateCandidateRegistry(recompiledA);
        _ = SceneCodeMigrator.Migrate(sessionA.Scene, sessionA.Components.Registry, candidateA);
        sessionA.Components.Adopt(recompiledA);
        var newTypeA = sessionA.Components.Registry.GetType("user.Game.Shared");
        Check(!ReferenceEquals(newTypeA, typeA)
            && (int)newTypeA.GetProperty("Marker")!.GetValue(Activator.CreateInstance(newTypeA)!)! == 30,
            "Project A reload must swap to the new code.");
        Check(ReferenceEquals(sessionB.Components.Registry.GetType("user.Game.Shared"), typeB),
            "Project A reload must not change Project B.");

        // A failure on one side must not change the other. Keep the previous state on failure.
        File.WriteAllText(Path.Combine(dirA, "Shared.cs"), Source(30) + "\nthis is broken;");
        var brokenA = UserCodeCompiler.CompileProject(dirA);
        Check(!brokenA.Success, "Broken source must fail.");
        var keptA = sessionA.Components.Registry.GetType("user.Game.Shared");
        Check(ReferenceEquals(keptA, newTypeA), "Failure must keep the previous registration.");
        Check(ReferenceEquals(sessionB.Components.Registry.GetType("user.Game.Shared"), typeB),
            "Project A failure must not change Project B.");
        File.WriteAllText(Path.Combine(dirA, "Shared.cs"), Source(31));

        // The other side must keep working after one side exits. Shutdown order: dispose components, then services, then request code unload.
        var sessionC = ProjectSession.Open(Path.Combine(dirA, "Project.pure.project.yaml"));
        var typeC = sessionC.Components.Registry.GetType("user.Game.Shared");
        Check((int)typeC.GetProperty("Marker")!.GetValue(Activator.CreateInstance(typeC)!)! == 31,
            "Reopened Project A must reflect the fixed source.");
        sessionC.Dispose();
        Check(sessionB.Components.Registry.GetType("user.Game.Shared") == typeB,
            "Disposing Project A must not change Project B.");
        var yamlAfter = new SceneSerializer(sessionB.Components.Registry).Serialize(sessionB.Scene);
        var restoredAfter = new SceneSerializer(sessionB.Components.Registry).Deserialize(yamlAfter);
        Check(restoredAfter.Objects.Single().Components.Single().GetType() == typeB,
            "Project B save/load must work after Project A exit.");
        using (var playAfter = PlaySession.Prepare(sessionB.Scene, sessionB.Components.Registry, GameServices.ForProject(sessionB.Components)))
        {
            playAfter.Start();
            playAfter.Step(0.016f);
            Check(playAfter.Runtime.IsRunning, "Project B Play must work after Project A exit.");
        }

        Console.WriteLine("PASS: two-project type/save/Play isolation, reload/failure/exit independence, and per-project ownership.");
    }
}
