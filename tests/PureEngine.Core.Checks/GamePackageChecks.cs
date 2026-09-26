using System.Text.Json;
using PureEngine.Core;
using PureEngine.Editor;
using PureEngine.Editor.Samples;
using PureEngine.Runtime;

internal static class GamePackageChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), $"PureEngine-package-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var data = Path.Combine(root, GamePackage.DataDirectoryName);
            Directory.CreateDirectory(data);
            var manifest = new GamePackageManifest { Name = "Package check", StartupScene = "Main.pure.scene.yaml" };
            WriteManifest(root, manifest);
            var registry = new ComponentRegistry();
            GameRegistration.RegisterBuiltins(registry);
            var scene = new Scene();
            var authoringBattle = new BattleSession();
            var authoring = new InjectedPlayer(new RandomService(), authoringBattle) { Hp = 43 };
            scene.AddEmpty().Attach(authoring);
            File.WriteAllText(Path.Combine(data, manifest.StartupScene), new SceneSerializer(registry).Serialize(scene));
            using (var package = GamePackage.Open(root))
            {
                package.Validate();
                using var first = package.CreateSession();
                using var second = package.CreateSession();
                var firstPlayer = first.Runtime.Scene.Objects.Single().GetComponent<InjectedPlayer>()!;
                var secondPlayer = second.Runtime.Scene.Objects.Single().GetComponent<InjectedPlayer>()!;
                Require(firstPlayer.Starts == 0 && firstPlayer.Hp == 43, "Preparing a package must not start components.");
                Require(!ReferenceEquals(firstPlayer.Session, secondPlayer.Session), "Packaged runs must have isolated scoped services.");
                Require(first.Localization is not null && !ReferenceEquals(first.Localization, second.Localization),
                    "Each packaged run must expose its own localization service.");
                first.Start();
                first.Step(0.1f);
                first.Dispose();
                Require(firstPlayer.Starts == 1 && firstPlayer.Updates == 1 && firstPlayer.Destroys == 1
                    && firstPlayer.Disposes == 1 && firstPlayer.Session.DisposeCalls == 1,
                    "Packaged lifecycle and service cleanup must run exactly once.");
                package.Dispose();
                Require(secondPlayer.Disposes == 1 && secondPlayer.Session.DisposeCalls == 1,
                    "Package disposal must release even an unstarted owned run.");
            }
            authoring.Dispose();
            authoringBattle.Dispose();

            var imageScene = new Scene();
            imageScene.AddEmpty().Attach(new Image { Sprite = new Sprite(Guid.NewGuid()) });
            File.WriteAllText(Path.Combine(data, manifest.StartupScene), new SceneSerializer(registry).Serialize(imageScene));
            using (var package = GamePackage.Open(root))
                Reject(package.Validate, "Missing image IDs must fail package validation.");
            File.WriteAllText(Path.Combine(data, manifest.StartupScene), new SceneSerializer(registry).Serialize(new Scene()));
            File.WriteAllText(Path.Combine(data, "Broken.pure.prefab.yaml"), "version: 1\nid: invalid\n");
            Reject(() => { using var _ = GamePackage.Open(root); }, "Corrupt prefab documents must fail package open.");
            File.Delete(Path.Combine(data, "Broken.pure.prefab.yaml"));

            var prefabScene = new Scene();
            var prefabRoot = prefabScene.AddEmpty();
            prefabRoot.Attach(new PlayerStats { Hp = 7 });
            var prefabId = Guid.NewGuid();
            var prefabYaml = new PrefabSerializer(registry).Serialize(prefabScene, prefabRoot, prefabId);
            File.WriteAllText(Path.Combine(data, "One.pure.prefab.yaml"), prefabYaml);
            File.WriteAllText(Path.Combine(data, "Duplicate.pure.prefab.yaml"), prefabYaml);
            Reject(() => { using var _ = GamePackage.Open(root); }, "Duplicate prefab IDs must fail package open.");
            File.Delete(Path.Combine(data, "Duplicate.pure.prefab.yaml"));
            using (var package = GamePackage.Open(root))
            {
                package.Validate();
                Require(package.Prefabs.Find(prefabId) is not null, "Prefab IDs must survive packaging.");
            }
            var prefabPath = Path.Combine(data, "One.pure.prefab.yaml");
            File.WriteAllText(prefabPath, prefabYaml.Replace("Hp:", "OldHp:", StringComparison.Ordinal));
            using (var package = GamePackage.Open(root))
                RejectMigration(package.Validate, "One.pure.prefab.yaml");
            File.WriteAllText(prefabPath, prefabYaml);
            var extraScenePath = Path.Combine(data, "Stale.pure.scene.yaml");
            File.WriteAllText(extraScenePath, new SceneSerializer(registry).Serialize(prefabScene)
                .Replace("Hp:", "OldHp:", StringComparison.Ordinal));
            using (var package = GamePackage.Open(root))
                RejectMigration(package.Validate, "Stale.pure.scene.yaml");
            File.Delete(extraScenePath);
            PrecompiledGame(root, data, manifest);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void PrecompiledGame(string root, string data, GamePackageManifest manifest)
    {
        var source = Path.Combine(root, "Source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "Game.cs"), """
            using System;
            using Microsoft.Extensions.DependencyInjection;
            using PureEngine.Core;
            public sealed class PackageProbe(LocalizationService localization) : IDisposable
            {
                public static int Starts;
                public static int Disposals;
                [Inspector] public int Value = 19;
                [Start] private void Start() { Starts++; localization.CurrentLanguage = "en"; }
                public void Dispose() { Disposals++; }
            }
            public static class PackageRegistration
            {
                public static void ConfigureGameServices(IServiceCollection services)
                {
                    services.AddScoped<LocalizationService>();
                }
            }
            """);
        var compiled = UserCodeCompiler.CompileProject(source);
        try
        {
            Require(compiled.Success && compiled.AssemblyBytes is not null,
                "Package fixture compilation failed: " + string.Join("; ", compiled.Diagnostics.Select(item => item.Message)));
            File.WriteAllBytes(Path.Combine(root, "Game.dll"), compiled.AssemblyBytes!);
            manifest.GameAssembly = "Game.dll";
            manifest.Types = new Dictionary<string, string> { ["user.original-stable-id"] = "PackageProbe" };
            WriteManifest(root, manifest);
            File.WriteAllText(Path.Combine(data, manifest.StartupScene), $$"""
                version: 3
                objects:
                - id: {{Guid.NewGuid():D}}
                  name: Probe
                  siblingIndex: 0
                  components:
                  - id: {{Guid.NewGuid():D}}
                    typeId: user.original-stable-id
                    values:
                      Value: 73
                """);
        }
        finally
        {
            ProjectComponents.Release(compiled);
            Directory.Delete(source, recursive: true);
        }
        using (var package = GamePackage.Open(root))
        {
            package.Validate();
            var type = package.Registry.GetType("user.original-stable-id");
            Require(type.FullName == "PackageProbe" && (int)type.GetField("Starts")!.GetValue(null)! == 0,
                "Validation must preserve renamed stable IDs without running Start.");
            var startupPath = Path.Combine(data, manifest.StartupScene);
            var startupYaml = File.ReadAllText(startupPath);
            var staleYaml = startupYaml.Replace("Value:", "OldValue:", StringComparison.Ordinal);
            File.WriteAllText(startupPath, staleYaml);
            var previousDisposals = (int)type.GetField("Disposals")!.GetValue(null)!;
            RejectMigration(package.Validate, manifest.StartupScene);
            RejectMigration(() => { using var _ = package.CreateSession(); }, manifest.StartupScene);
            Require((int)type.GetField("Starts")!.GetValue(null)! == 0
                && (int)type.GetField("Disposals")!.GetValue(null)! == previousDisposals + 2,
                "Migration rejection must dispose restored components without starting them.");
            Require(File.ReadAllText(startupPath) == staleYaml, "Package validation must not rewrite stale files.");
            File.WriteAllText(startupPath, startupYaml);
            using var session = package.CreateSession();
            var probe = session.Runtime.Scene.Objects.Single().Components.Single();
            Require((int)type.GetField("Value")!.GetValue(probe)! == 73, "Precompiled component values must restore by stable ID.");
            session.Start();
            Require(session.Localization?.CurrentLanguage == "en", "Game DI and the renderer must share the host localization type and instance.");
        }
        // A disposed package must not retain a Windows file lock on its precompiled assembly.
        File.Move(Path.Combine(root, "Game.dll"), Path.Combine(root, "Moved.dll"));
        manifest.GameAssembly = "Moved.dll";
        manifest.Types["user.original-stable-id"] = "Missing.Type";
        WriteManifest(root, manifest);
        Reject(() => { using var _ = GamePackage.Open(root); }, "Missing mapped types must fail package open.");
    }

    private static void WriteManifest(string root, GamePackageManifest manifest) =>
        File.WriteAllText(Path.Combine(root, GamePackage.ManifestFileName), JsonSerializer.Serialize(manifest));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void RejectMigration(Action operation, string path)
    {
        try { operation(); }
        catch (AggregateException error)
        {
            Require(error.Flatten().InnerExceptions.Any(cause => cause is InvalidDataException
                && cause.Message.Contains(path, StringComparison.Ordinal)
                && cause.Message.Contains("requires migration", StringComparison.Ordinal)),
                $"Migration rejection must identify the stale file: {path}.");
            return;
        }
        throw new InvalidOperationException($"Migration-required content must not be accepted: {path}.");
    }

    private static void Reject(Action operation, string message)
    {
        try { operation(); }
        catch (Exception error) when (error is InvalidDataException or AggregateException) { return; }
        throw new InvalidOperationException(message);
    }
}
