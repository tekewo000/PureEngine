using System.Diagnostics;
using System.Reflection;
using System.Xml.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PureEngine.Core;
using PureEngine.Editor;
using Button = Avalonia.Controls.Button;

static class UserCodeChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static object? Call(MainWindow window, string name, params object?[] args)
    {
        var result = typeof(MainWindow).GetMethod(name, Instance)!.Invoke(window, args);
        if (result is Task task) Program.Wait(task);
        return result;
    }
    private static T Field<T>(MainWindow window, string name) =>
        (T)(typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) is { } field
            ? field.GetValue(window) : typeof(MainWindow).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(window))!;
    private static Scene EditScene(MainWindow window) =>
        ((EditSceneStore)typeof(MainWindow).GetProperty("EditSceneStore", Instance)!.GetValue(window)!).Current;
    private static EditSceneStore EditStore(MainWindow window) =>
        (EditSceneStore)typeof(MainWindow).GetProperty("EditSceneStore", Instance)!.GetValue(window)!;
    private static bool HasPendingReload(MainWindow window) =>
        typeof(MainWindow).GetField("_pendingCompilation", Instance)!.GetValue(window) is not null;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private static void PumpUntil(Func<bool> condition, string message)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(15))
        {
            using var slice = new CancellationTokenSource(30);
            Dispatcher.UIThread.MainLoop(slice.Token);
        }
        Check(condition(), message);
    }
    private static string Source(int version) => $$"""
        using PureEngine.Core;
        using PureEngine.Core;
        namespace Game;
        public class Player
        {
            [Inspector] public int Health = 10;
            {{(version > 1 ? "[Inspector] public int Added = 42;" : "")}}
            public int Version => {{version}};
            [Start] public void Start() { Log.Info("user-start-{{version}}"); }
            [Update] public void Update(float dt) { }
        }
        internal class Helper { }
        public static class Utilities { }
        public abstract class Base { }
        public class Generic<T> { public class Nested { } }
        internal class Hidden { public class Nested { } }
        """;
    private static object Player(MainWindow editor) => EditScene(editor).Objects.Single().Components.Single();
    private static SceneObject? Selected(MainWindow editor) =>
        (SceneObject?)Call(editor, "GetSelectedSceneObject", []);
    private static int Version(MainWindow editor) => (int)Player(editor).GetType().GetProperty("Version")!.GetValue(Player(editor))!;
    private static TextBox HealthBox(MainWindow editor) => editor.GetVisualDescendants().OfType<TextBox>()
        .Single(box => AutomationProperties.GetName(box) == "Player.Health");

    public static void Run(string parent)
    {
        CheckLifecycleDiagnostics(parent);
        CheckEnumReload(parent);
        using var created = ProjectSession.Create(parent, "UserCode");
        var project = created.Project;
        created.Dispose();
        var workspacePath = Path.Combine(project.RootDirectory, ProjectCodeWorkspace.ProjectName);
        Check(File.Exists(workspacePath) && File.Exists(Path.Combine(project.RootDirectory, "global.json")),
            "New projects must include C# editor workspace and SDK selection.");
        var solutionPath = Path.Combine(project.RootDirectory, "PureEngine.Game.slnx");
        Check(XDocument.Load(solutionPath).Descendants("Project").Single().Attribute("Path")!.Value
            == ProjectCodeWorkspace.ProjectName, "Roslyn needs a solution pointing to the game project.");
        var workspace = XDocument.Load(workspacePath);
        Check(workspace.Descendants("HintPath").Any(path => path.Value == typeof(Scene).Assembly.Location)
            && workspace.Descendants("ImplicitUsings").Single().Value == "disable",
            "Editor workspace must reference the current engine and match runtime compiler options.");
        Check(File.Exists(workspace.Descendants("Analyzer").Single().Attribute("Include")!.Value),
            "Game workspaces must load the shipped lifecycle diagnostic suppressor.");
        var workspaceTime = File.GetLastWriteTimeUtc(workspacePath);
        ProjectCodeWorkspace.Ensure(project);
        Check(File.GetLastWriteTimeUtc(workspacePath) == workspaceTime, "Unchanged metadata must not be rewritten.");
        File.Delete(workspacePath); // Simulate an existing project made before workspace generation.
        File.Delete(solutionPath);
        var folder = Path.Combine(project.RootDirectory, "Gameplay", "Actors");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "Player.cs");
        File.WriteAllText(file, Source(1));
        var session = ProjectSession.Open(project.ManifestPath);
        Check(File.Exists(workspacePath), "Opening an existing project must fill missing editor metadata.");
        Check(File.Exists(solutionPath), "Opening an existing project must fill missing solution metadata.");
        Check(session.Components.UserTypes.Count == 1, "Only visible concrete nongeneric classes should be attachable.");
        var type = session.Components.GetTypesForFile(file).Single();
        var item = session.Scene.AddEmpty();
        item.Rename("Unsaved player");
        Check(session.Components.TryAttach(item, type), "A class from an arbitrary project folder must attach.");
        var player = item.Components.Single();
        type.GetField("Health")!.SetValue(player, 73);
        item.SetStartPriority(player, -9);
        item.SetUpdatePriority(player, 5);
        var id = item.Id;
        var editor = new MainWindow(session);
        var owner = Field<ProjectComponents>(editor, "_components");
        Check(ReferenceEquals(owner, session.Components), "Editor must adopt the session owner.");
        Check(editor.FindControl<TextBlock>("CompileStatus")!.Text!.StartsWith("Compile: ", StringComparison.Ordinal)
            && editor.FindControl<TextBlock>("CompileStatus")!.Text != "Compile: —",
            "Opening a project must show the project-open compilation time.");
        editor.Show();
        Call(editor, "MarkSceneChanged");
        Dispatcher.UIThread.RunJobs();
        try
        {
            // Keep the user's folder selected across reloads.
            typeof(MainWindow).GetField("_explorerFolder", Instance)!.SetValue(editor, "Gameplay/Actors");
            Call(editor, "RefreshProjectExplorer");
            Check(editor.FindControl<ListBox>("ProjectFiles")!.Items.Count == 1, "C# must appear in its own folder.");
            File.WriteAllText(file, Source(2) + "\npublic record Extra { }\npublic class Second { }");
            PumpUntil(() => Version(editor) == 2, "Saving C# must reload automatically.");
            Check(editor.FindControl<TextBlock>("CompileStatus")!.Text!.StartsWith("Compile: ", StringComparison.Ordinal)
                && editor.FindControl<TextBlock>("CompileStatus")!.Text != "Compile: —",
                "Reloading C# must update the compilation time.");
            Check(owner.GetTypesForFile(file).Count == 3, "Multiple classes and records must map to the source file.");
            var current = EditScene(editor).Objects.Single();
            Check(current.Id == id && current.Name == "Unsaved player"
                && current.GetStartPriority(Player(editor)) == -9 && current.GetUpdatePriority(Player(editor)) == 5
                && (int)Player(editor).GetType().GetField("Health")!.GetValue(Player(editor))! == 73
                && editor.Title!.StartsWith("* "), "Reload must preserve identity, unsaved values, Priority and dirty state.");
            Check(Field<string>(editor, "_explorerFolder") == "Gameplay/Actors", "Reload must preserve selected project folder.");
            Check((int)Player(editor).GetType().GetField("Added")!.GetValue(Player(editor))! == 42,
                "New Inspector fields must keep their initializer.");

            // Invalid text is authoring data too; resume automatically once corrected.
            HealthBox(editor).Text = "invalid";
            Dispatcher.UIThread.RunJobs();
            File.WriteAllText(file, Source(3));
            PumpUntil(() => HasPendingReload(editor), "Invalid Inspector input must defer reload.");
            Check(Version(editor) == 2 && HealthBox(editor).Text == "invalid", "Invalid text must not be discarded.");
            HealthBox(editor).Text = "81";
            Dispatcher.UIThread.RunJobs();
            PumpUntil(() => Version(editor) == 3, "Correcting Inspector input must apply the pending reload.");

            Call(editor, "StartPlay");
            Check(editor.IsPlaying, "User code must run.");
            File.WriteAllText(file, Source(4));
            PumpUntil(() => HasPendingReload(editor), "Play must defer source changes.");
            Check(Version(editor) == 3, "Play must retain its code version.");
            Call(editor, "StopPlay");
            Check(Version(editor) == 4, "Stop must apply the pending source version.");

            var good = Player(editor);
            File.WriteAllText(file, Source(5) + "\nthis is broken;");
            PumpUntil(() => editor.FindControl<TextBlock>("FileStatus")!.Text!.Contains("C# compilation failed"),
                "Compiler errors must be reported.");
            Check(ReferenceEquals(good, Player(editor)), "Compile failure must keep the exact old instances.");
            Check(Field<List<LogEntry>>(editor, "_consoleHistory").Any(entry =>
                entry.Message.Contains("Player.cs(") && entry.Message.Contains("CS")),
                "Console must include compiler file, line, and diagnostic.");
            File.WriteAllText(file, Source(5));
            PumpUntil(() => Version(editor) == 5, "Saving a correction must recover without reopening.");

            // No warning-only loss: reject incompatible definitions and keep values.
            good = Player(editor);
            foreach (var incompatible in new[] {
                Source(6).Replace("int Health = 10", "float Health = 10"),
                Source(6).Replace("[Start] public void Start()", "public void Start()"),
                Source(6).Replace("public int Version => 6;", "public Player() { throw new System.Exception(\"ctor-failure\"); } public int Version => 6;")
            })
            {
                File.WriteAllText(file, incompatible);
                Call(editor, "ReloadUserCode");
                Check(ReferenceEquals(good, Player(editor)), "Schema/constructor failure must preserve the scene.");
            }
            File.WriteAllText(file, Source(6) + "\n");
            Call(editor, "ReloadUserCode");
            Check(Version(editor) == 6, "Recovery after incompatible changes must work.");

            var moved = Path.Combine(project.RootDirectory, "MovedActors");
            // Both absolute targets are below this freshly created test project.
            Directory.Move(folder, moved);
            file = Path.Combine(moved, "Player.cs");
            PumpUntil(() => owner.GetTypesForFile(file).Count == 1, "Folder moves must update source mapping.");
            File.Move(file, file + ".disabled");
            PumpUntil(() => editor.FindControl<TextBlock>("FileStatus")!.Text!.Contains("Cannot apply"),
                "Renaming away the .cs extension must be detected.");
            Check(Version(editor) == 6, "Deleting an attached class must preserve the old scene.");
            File.Move(file + ".disabled", file);
            File.WriteAllText(file, Source(7));
            PumpUntil(() => Version(editor) == 7, "Restoring source must recover.");

            File.WriteAllText(file, Source(8).Replace("namespace Game;", "namespace Renamed.Game;")
                .Replace("class Player", "class Hero"));
            PumpUntil(() => Version(editor) == 8, "Class and namespace renames must reload automatically.");
            Check(Player(editor).GetType().FullName == "Renamed.Game.Hero"
                && owner.Registry.GetId(Player(editor).GetType()) == "user.Game.Player"
                && (int)Player(editor).GetType().GetField("Health")!.GetValue(Player(editor))! == 81
                && EditScene(editor).Objects.Single().GetStartPriority(Player(editor)) == -9,
                "Renames must retain component identity, Inspector values and Priority.");

            var legacyYaml = new SceneSerializer(owner.Registry).Serialize(EditScene(editor));
            var renamedSource = Source(9).Replace("namespace Game;", "namespace Renamed.Game;")
                .Replace("class Player", "class Hero")
                .Replace("[Inspector] public int Health = 10;",
                    "[Inspector, FormerlySerializedAs(\"Health\")] public int HitPoints { get; set; } = 10;");
            File.WriteAllText(file, renamedSource);
            PumpUntil(() => Version(editor) == 9, "Inspector member renames must reload automatically.");
            var renamedPlayer = Player(editor);
            var hitPoints = renamedPlayer.GetType().GetProperty("HitPoints")!;
            Check((int)hitPoints.GetValue(renamedPlayer)! == 81 && editor.Title!.StartsWith("* ")
                && EditScene(editor).Objects.Single().Id == id
                && EditScene(editor).Objects.Single().GetStartPriority(renamedPlayer) == -9
                && ReferenceEquals(Selected(editor), EditScene(editor).Objects.Single())
                && editor.GetVisualDescendants().OfType<TextBox>().Any(box => AutomationProperties.GetName(box) == "Hero.HitPoints" && box.Text == "81"),
                "Renamed members must retain unsaved values, identity, Priority and selection, with the new Inspector label.");
            var legacyScene = new SceneSerializer(owner.Registry).Deserialize(legacyYaml);
            Check((int)hitPoints.GetValue(legacyScene.Objects.Single().Components.Single())! == 81,
                "Unopened old scenes must load into renamed members.");
            foreach (var invalid in new[]
            {
                renamedSource.Replace("public int HitPoints", "public float HitPoints"),
                renamedSource.Replace("[Inspector] public int Added = 42;", "[Inspector, FormerlySerializedAs(\"Health\")] public int Added = 42;"),
            })
            {
                File.WriteAllText(file, invalid);
                Call(editor, "ReloadUserCode");
                Check(ReferenceEquals(Player(editor), renamedPlayer) && (int)hitPoints.GetValue(renamedPlayer)! == 81,
                    "Incompatible or ambiguous member renames must retain the old code and data.");
            }
            File.WriteAllText(file, renamedSource);
            Call(editor, "ReloadUserCode");
            Call(editor, "StartPlay");
            Check(editor.IsPlaying, "A renamed Inspector member must support Play cloning.");
            Call(editor, "StopPlay");

            // Plain renames/deletions need no migration attributes: drop old values and keep new initializers.
            Check(((Task<bool>)Call(editor, "SaveSceneAsync", false)!).GetAwaiter().GetResult() && !EditStore(editor).IsDirty,
                "Reset checks must start from a saved scene.");
            var resetSource = renamedSource.Replace(", FormerlySerializedAs(\"Health\")", "")
                .Replace("HitPoints", "Life").Replace("[Inspector] public int Added = 42;", "")
                .Replace("Version => 9", "Version => 10");
            File.WriteAllText(file, resetSource);
            PumpUntil(() => Version(editor) == 10, "Unannotated Inspector renames and deletions must reload without an error.");
            var resetPlayer = Player(editor);
            Check((int)resetPlayer.GetType().GetProperty("Life")!.GetValue(resetPlayer)! == 10
                && resetPlayer.GetType().GetField("Added") is null
                && EditScene(editor).Objects.Single().Id == id
                && EditScene(editor).Objects.Single().GetStartPriority(resetPlayer) == -9
                && ReferenceEquals(Selected(editor), EditScene(editor).Objects.Single())
                && editor.Title!.StartsWith("* "), "Resetting renamed fields must preserve identity, Priority, selection and dirty state.");
            var resetSerializer = new SceneSerializer(owner.Registry);
            var oldScene = resetSerializer.Deserialize(legacyYaml);
            Check((int)resetPlayer.GetType().GetProperty("Life")!.GetValue(oldScene.Objects.Single().Components.Single())! == 10,
                "Old scenes must open without attributes and use the new member's initializer.");
            var resetYaml = resetSerializer.Serialize(EditScene(editor));
            Check(!resetYaml.Contains("Health:") && !resetYaml.Contains("HitPoints:") && !resetYaml.Contains("Added:")
                && resetYaml.Contains("Life: 10"), "Saving must discard obsolete names and store the new member.");
            Call(editor, "StartPlay");
            Check(editor.IsPlaying, "Resetting renamed members must allow Play.");
            Call(editor, "StopPlay");

            // Failed opening of another project must not mutate active code registrations.
            var other = ProjectFile.Create(parent, "BrokenUserCodeProject", "version: 999\nobjects: []");
            File.WriteAllText(Path.Combine(other.RootDirectory, "Other.cs"), "public class Other {}");
            var activeType = Player(editor).GetType();
            try { using var failed = ProjectSession.Open(other.ManifestPath); throw new Exception("Expected failure."); }
            catch (InvalidDataException) { }
            Check(owner.Registry.GetType("user.Game.Player") == activeType, "Failed project open changed active registration.");

            // Round-trip the reloaded instance, then reopen in a new editor.
            var save = (Task<bool>)Call(editor, "SaveSceneAsync", false)!;
            Check(save.GetAwaiter().GetResult(), "Reloaded scene should save.");
            Check(File.ReadAllText(project.StartupScenePath) == resetYaml,
                "Saving after a rename must remove obsolete Inspector entries from the actual YAML file.");

            // A project reopened with older YAML must offer saving the cleaned representation as well.
            SceneFile.Write(project.StartupScenePath, legacyYaml);
            using (var staleSession = ProjectSession.Open(project.ManifestPath))
            {
                var staleEditor = new MainWindow(staleSession);
                Check(EditStore(staleEditor).IsDirty && staleEditor.Title!.StartsWith("* "),
                    "Opening a project with obsolete Inspector names must mark the scene dirty.");
                EditStore(staleEditor).MarkClean();
                staleEditor.Close();
            }
            Call(editor, "OpenScenePathAsync", project.StartupScenePath);
            Check(EditStore(editor).IsDirty, "Opening an older scene must mark cleanup as unsaved.");
            Check(((Task<bool>)Call(editor, "SaveSceneAsync", false)!).GetAwaiter().GetResult()
                && File.ReadAllText(project.StartupScenePath) == resetYaml && !EditStore(editor).IsDirty,
                "Saving a loaded older scene must clean its YAML and clear the dirty state.");
        }
        finally
        {
            EditStore(editor).MarkClean();
            editor.Close();
            Dispatcher.UIThread.RunJobs();
        }
        Check(Field<UserCodeWatcher?>(editor, "_userCodeWatcher") is null, "Closing must stop watching.");
        using var reopened = ProjectSession.Open(project.ManifestPath);
        var restored = reopened.Scene.Objects.Single();
        Check(restored.Components.Single().GetType().FullName == "Renamed.Game.Hero",
            "Renamed identity must survive closing and reopening the project.");
        Check(restored.Id == id && (int)restored.Components.Single().GetType().GetProperty("Life")!.GetValue(restored.Components.Single())! == 10,
            "Saved user components must restore after a fresh compile.");
        using var next = ProjectSession.Create(parent, "NoUserCode");
        Check(next.Components.UserTypes.Count == 0, "New projects must start without user code.");
        Check(!File.Exists(Path.Combine(next.Project.RootDirectory, "Player.cs")), "User sources must stay project-local.");
        CheckCreateCSharp(next);
        var nextWorkspace = Path.Combine(next.Project.RootDirectory, ProjectCodeWorkspace.ProjectName);
        var customProject = "<Project Sdk=\"Microsoft.NET.Sdk\"><!-- user owned --></Project>";
        File.WriteAllText(nextWorkspace, customProject);
        var sdkPath = Path.Combine(next.Project.RootDirectory, "global.json");
        File.WriteAllText(sdkPath, "{\"sdk\":{\"version\":\"10.0.100\"}}");
        ProjectCodeWorkspace.Ensure(next.Project);
        Check(File.ReadAllText(nextWorkspace) == customProject && File.ReadAllText(sdkPath).Contains("10.0.100"),
            "Hand-maintained project and SDK settings must remain untouched.");
        File.Move(nextWorkspace, Path.Combine(next.Project.RootDirectory, "Custom.csproj"));
        ProjectCodeWorkspace.Ensure(next.Project);
        Check(!File.Exists(nextWorkspace), "Do not introduce another project beside a user's existing csproj.");
        CheckIdentitySafety(parent);
        Console.WriteLine("PASS: user compilation, source mapping, real file/folder watching, Play deferral, schema/error recovery, unsaved data, persistence, and project isolation.");
    }

    private static void CheckLifecycleDiagnostics(string parent)
    {
        using var session = ProjectSession.Create(parent, "LifecycleDiagnostics");
        var root = session.Project.RootDirectory;
        var projectPath = Path.Combine(root, ProjectCodeWorkspace.ProjectName);
        File.WriteAllText(Path.Combine(root, ".editorconfig"),
            "root = true\n[*.cs]\ndotnet_diagnostic.IDE0051.severity = warning\n");
        var source = """
            using PureEngine.Core;
            using OnFrame = PureEngine.Core.UpdateAttribute;
            public sealed class Callbacks
            {
                [Start] private void Begin() { }
                [OnFrame] private void Tick() { }
                [global::PureEngine.Core.DestroyAttribute] private void End() { }
                private void Ordinary() { }
                [Other.Update] private void UnrelatedAttribute() { }
            }
            namespace Other
            {
                public sealed class UpdateAttribute : System.Attribute { }
            }
            """;
        var sourcePath = Path.Combine(root, "Callbacks.cs");
        File.WriteAllText(sourcePath, source);

        string Build()
        {
            using var process = Process.Start(new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "build", projectPath, "--nologo", "--verbosity", "minimal", "-t:Rebuild", "-p:EnforceCodeStyleInBuild=true" }
            })!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60_000))
            {
                process.Kill(entireProcessTree: true);
                throw new Exception("Lifecycle diagnostic build timed out.");
            }
            var log = output.GetAwaiter().GetResult() + errors.GetAwaiter().GetResult();
            Check(process.ExitCode == 0, log);
            return log;
        }
        static bool IsUnused(string log, string method) => log.Split('\n')
            .Any(line => line.Contains("IDE0051") && line.Contains("Callbacks." + method));

        // Prove that the real SDK analyzer reports every fixture before enabling suppression.
        var workspace = XDocument.Load(projectPath);
        workspace.Descendants("Analyzer").Remove();
        workspace.Save(projectPath);
        var baseline = Build();
        Check(IsUnused(baseline, "Begin") && IsUnused(baseline, "Tick") && IsUnused(baseline, "End")
            && IsUnused(baseline, "Ordinary") && IsUnused(baseline, "UnrelatedAttribute"), baseline);

        ProjectCodeWorkspace.Ensure(session.Project);
        var suppressed = Build();
        Check(!IsUnused(suppressed, "Begin") && !IsUnused(suppressed, "Tick") && !IsUnused(suppressed, "End")
            && IsUnused(suppressed, "Ordinary") && IsUnused(suppressed, "UnrelatedAttribute"), suppressed);
        Check(File.ReadAllText(sourcePath) == source, "Diagnostic suppression must not rewrite game code.");

        File.WriteAllText(sourcePath, source.Replace("[OnFrame] ", ""));
        Check(IsUnused(Build(), "Tick"), "Removing the lifecycle attribute must restore IDE0051.");
        Console.WriteLine("PASS: lifecycle IDE0051 suppression, aliases, unrelated attributes, ordinary methods, and attribute removal.");
    }

    private static void CheckEnumReload(string parent)
    {
        var file = Path.Combine(parent, "EnumReload.cs");
        const string code = """
            using System;
            using System.Collections.Generic;
            using PureEngine.Core;
            public enum Mode : int { None = 0, Easy = 1, Hard = 2 }
            public sealed class EnumComponent
            {
                public sealed class Stats
                {
                    [Inspector] public Mode Level = Mode.Hard;
                }
                [Inspector] public Stats Custom = new();
                [Inspector] public Stats[] Customs = [new()];
                [Inspector] public List<Stats> CustomList = [new()];
                [Inspector] public Dictionary<string, Stats> CustomMap = new() { ["key"] = new() };
                [Inspector] public Mode Value = Mode.Hard;
                [Inspector] public Mode? Maybe = Mode.Hard;
                [Inspector] public Mode? Missing = null;
                [Inspector] public Mode[] Array = [Mode.Hard];
                [Inspector] public List<Mode?> List = [Mode.Hard, null];
                [Inspector] public Dictionary<string, Mode?> Map = new() { ["key"] = Mode.Hard, ["null"] = null };
            }
            """;
        File.WriteAllText(file, code);
        using var original = new ProjectComponents();
        var first = UserCodeCompiler.CompileFiles([file]);
        Check(first.Success, "Enum fixture must compile.");
        original.Adopt(first);
        // This fixture exercises embedded values; registered types now mean Component references.
        original.Registry.Unregister(first.AttachableTypes.Single(type => type.Name == "Stats"));
        var scene = new Scene();
        var item = scene.AddEmpty();
        Check(original.TryAttach(item, first.AttachableTypes.Single(type => type.Name == "EnumComponent")), "Enum fixture must attach.");
        var serializer = new SceneSerializer(original.Registry);
        var expected = serializer.Serialize(scene);
        // Different initializers ensure migration restores edited data, not freshly constructed defaults.
        var changed = code.Replace("Mode.Hard", "Mode.Easy");
        foreach (var source in new[]
        {
            changed,
            changed.Replace("Hard = 2", "Hard = 2, Expert = 3"),
            changed.Replace("[Inspector] public Mode Level", "[Inspector, FormerlySerializedAs(\"Level\")] public Mode Rank"),
        })
        {
            File.WriteAllText(file, source);
            using var candidate = new ProjectComponents();
            var compiled = UserCodeCompiler.CompileFiles([file]);
            Check(compiled.Success, "Compatible enum fixture must compile.");
            candidate.Adopt(compiled);
            candidate.Registry.Unregister(compiled.AttachableTypes.Single(type => type.Name == "Stats"));
            Check(compiled.AttachableTypes.Single(type => type.Name == "EnumComponent")
                != first.AttachableTypes.Single(type => type.Name == "EnumComponent"), "Reload must use a new assembly.");
            var migrated = SceneCodeMigrator.Migrate(scene, original.Registry, candidate.Registry);
            Check(new SceneSerializer(candidate.Registry).Serialize(migrated).Replace("Rank:", "Level:") == expected,
                "Recompiled enum and custom class values, including former names and collections, must survive migration.");
        }
        foreach (var source in new[]
        {
            changed.Replace("Mode", "OtherMode"),
            changed.Replace("Mode : int", "Mode : long"),
            changed.Replace("Hard = 2", "Hard = 3"),
            changed.Replace("Hard = 2", "Renamed = 2"),
            changed.Replace("public enum Mode", "[Flags] public enum Mode"),
            changed.Replace("public Mode Value = Mode.Easy", "public int Value = 1"),
            changed.Replace("public Mode Level = Mode.Easy", "public int Level = 1"),
            changed.Replace("Stats", "OtherStats"),
        })
        {
            File.WriteAllText(file, source);
            using var candidate = new ProjectComponents();
            var compiled = UserCodeCompiler.CompileFiles([file]);
            Check(compiled.Success, "Incompatible enum fixture must still compile.");
            candidate.Adopt(compiled);
            foreach (var type in compiled.AttachableTypes.Where(type => type.Name is "Stats" or "OtherStats"))
                candidate.Registry.Unregister(type);
            try
            {
                SceneCodeMigrator.Migrate(scene, original.Registry, candidate.Registry);
                throw new Exception("Incompatible enum change was accepted.");
            }
            catch (InvalidDataException) { }
            Check(serializer.Serialize(scene) == expected, "Failed enum migration must preserve the original scene.");
        }
        Console.WriteLine("PASS: recompiled Inspector enums and collections preserve values; incompatible definitions preserve the old scene.");
    }

    private static void CheckIdentitySafety(string parent)
    {
        using var created = ProjectSession.Create(parent, "IdentityUpgrade");
        var project = created.Project;
        created.Dispose();
        var source = Path.Combine(project.RootDirectory, "Legacy.cs");
        var code = "using PureEngine.Core; public class Legacy { [Inspector] public int Value = 5; }";
        File.WriteAllText(source, code);
        // Emulate a scene made by the old version, without any identity catalog.
        // A4: Adopt into a dedicated owner while keeping save compatibility (typeId and types.json).
        using var legacyOwner = new ProjectComponents();
        var compiled = UserCodeCompiler.CompileFiles([source]);
        Check(compiled.Success, "Legacy test compilation failed.");
        legacyOwner.Adopt(compiled);
        var scene = new Scene();
        var item = scene.AddEmpty();
        legacyOwner.TryAttach(item, compiled.AttachableTypes.Single());
        compiled.AttachableTypes.Single().GetField("Value")!.SetValue(item.Components.Single(), 99);
        SceneFile.Write(project.StartupScenePath, new SceneSerializer(legacyOwner.Registry).Serialize(scene));
        legacyOwner.Dispose();
        File.WriteAllText(source, code.Replace("public class Legacy", "namespace NewNamespace; public class Legacy"));
        using var opened = ProjectSession.Open(project.ManifestPath);
        var component = opened.Scene.Objects.Single().Components.Single();
        Check(component.GetType().FullName == "NewNamespace.Legacy"
            && opened.Components.Registry.GetId(component.GetType()) == "user.Legacy"
            && (int)component.GetType().GetField("Value")!.GetValue(component)! == 99,
            "Pre-catalog scenes must survive a uniquely identifiable namespace change.");
        opened.Dispose();

        var duo = Path.Combine(project.RootDirectory, "Duo.cs");
        File.WriteAllText(duo, "public class A {} public class B {}");
        using (var first = ProjectSession.Open(project.ManifestPath)) { first.Dispose(); }
        var metadataPath = Path.Combine(project.RootDirectory, ".pureengine", "types.json");
        var metadata = File.ReadAllText(metadataPath);
        File.WriteAllText(duo, "public class C {} public class D {}");
        var ambiguous = UserCodeCompiler.CompileProject(project.RootDirectory);
        Check(!ambiguous.Success && ambiguous.Diagnostics.Any(d => d.Id == "PE-IDENTITY")
            && File.ReadAllText(metadataPath) == metadata, "Ambiguous renames must fail without changing IDs.");
        File.WriteAllText(duo, "namespace Moved; public class A {} public class B {}");
        using var moved = ProjectSession.Open(project.ManifestPath);
        Check(moved.Components.Registry.GetType("user.A").FullName == "Moved.A"
            && moved.Components.Registry.GetType("user.B").FullName == "Moved.B",
            "Multiple classes may change namespace when names identify them uniquely.");
    }

    private static void CheckCreateCSharp(ProjectSession session)
    {
        var editor = new MainWindow(session);
        var owner = Field<ProjectComponents>(editor, "_components");
        editor.Show();
        Dispatcher.UIThread.RunJobs();
        void Create(string menuName, string? name)
        {
            editor.FindControl<MenuItem>(menuName)!.RaiseEvent(
                new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var dialog = editor.OwnedWindows.Single(window => window.Title == "Create C#");
            if (name is not null) dialog.GetVisualDescendants().OfType<TextBox>().Single().Text = name;
            dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, name is null ? "Cancel" : "OK"))
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        }
        try
        {
            Create("TreeCreateCSharpMenu", "CreatedFromTree");
            var path = Path.Combine(session.Project.ScenesDirectory, "CreatedFromTree.cs");
            Check(File.ReadAllText(path).Replace("\r\n", "\n") == "public sealed class CreatedFromTree\n{\n\n}\n",
                "C# creation must use exactly the requested filename and template.");
            Create("FilesCreateCSharpMenu", "CreatedFromFiles.cs");
            var second = Path.Combine(session.Project.ScenesDirectory, "CreatedFromFiles.cs");
            Check(File.Exists(second) && !File.Exists(second + ".cs"), "The .cs extension must be optional.");
            var original = File.ReadAllText(path);
            Create("FilesCreateCSharpMenu", "CreatedFromTree");
            Check(File.ReadAllText(path) == original
                && editor.FindControl<TextBlock>("FileStatus")!.Text!.Contains("already exists"),
                "Duplicate names must not overwrite existing files.");
            foreach (var invalid in new[] { "class", "123Bad", "../Escape", "Bad Name" })
            {
                Create("FilesCreateCSharpMenu", invalid);
                Check(editor.FindControl<TextBlock>("FileStatus")!.Text!.Contains("class name"), "Invalid class names must be rejected.");
            }
            Create("FilesCreateCSharpMenu", null);
            Check(Directory.GetFiles(session.Project.ScenesDirectory, "*.cs").Length == 2,
                "Cancellation and invalid names must not create files.");
            PumpUntil(() => owner.GetTypesForFile(path).Count == 1 && owner.GetTypesForFile(second).Count == 1,
                "Created scripts must enter the normal automatic compilation flow.");
        }
        finally
        {
            editor.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
