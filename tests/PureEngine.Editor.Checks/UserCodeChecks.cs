using System.Diagnostics;
using System.Reflection;
using System.Xml.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PureEngine.Core;
using PureEngine.Editor;

static class UserCodeChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static object? Call(MainWindow window, string name, params object?[] args) =>
        typeof(MainWindow).GetMethod(name, Instance)!.Invoke(window, args);
    private static T Field<T>(MainWindow window, string name) =>
        (T)typeof(MainWindow).GetField(name, Instance)!.GetValue(window)!;
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
        using PureEngine.Core.Attributes;
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
    private static object Player(MainWindow editor) => Field<Scene>(editor, "_scene").Objects.Single().Components.Single();
    private static int Version(MainWindow editor) => (int)Player(editor).GetType().GetProperty("Version")!.GetValue(Player(editor))!;
    private static TextBox HealthBox(MainWindow editor) => editor.GetVisualDescendants().OfType<TextBox>()
        .Single(box => AutomationProperties.GetName(box) == "Player.Health");

    public static void Run(string parent)
    {
        var project = ProjectSession.Create(parent, "UserCode").Project;
        var workspacePath = Path.Combine(project.RootDirectory, ProjectCodeWorkspace.ProjectName);
        Check(File.Exists(workspacePath) && File.Exists(Path.Combine(project.RootDirectory, "global.json")),
            "New projects must include C# editor workspace and SDK selection.");
        var solutionPath = Path.Combine(project.RootDirectory, "PureEngine.Game.slnx");
        Check(XDocument.Load(solutionPath).Descendants("Project").Single().Attribute("Path")!.Value
            == ProjectCodeWorkspace.ProjectName, "Roslyn needs a solution pointing to the game project.");
        var workspace = XDocument.Load(workspacePath);
        Check(workspace.Descendants("HintPath").Single().Value == typeof(Scene).Assembly.Location
            && workspace.Descendants("ImplicitUsings").Single().Value == "disable",
            "Editor workspace must reference the current engine and match runtime compiler options.");
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
        Check(ComponentAssets.UserTypes.Count == 1, "Only visible concrete nongeneric classes should be attachable.");
        var type = ComponentAssets.GetTypesForFile(file).Single();
        var item = session.Scene.AddEmpty();
        item.Rename("Unsaved player");
        Check(ComponentAssets.TryAttach(item, type), "A class from an arbitrary project folder must attach.");
        var player = item.Components.Single();
        type.GetField("Health")!.SetValue(player, 73);
        item.SetStartPriority(player, -9);
        item.SetUpdatePriority(player, 5);
        var id = item.Id;
        var editor = new MainWindow(session);
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
            Check(ComponentAssets.GetTypesForFile(file).Count == 3, "Multiple classes and records must map to the source file.");
            var current = Field<Scene>(editor, "_scene").Objects.Single();
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
            PumpUntil(() => Field<bool>(editor, "_userCodePendingReload"), "Invalid Inspector input must defer reload.");
            Check(Version(editor) == 2 && HealthBox(editor).Text == "invalid", "Invalid text must not be discarded.");
            HealthBox(editor).Text = "81";
            Dispatcher.UIThread.RunJobs();
            PumpUntil(() => Version(editor) == 3, "Correcting Inspector input must apply the pending reload.");

            Call(editor, "StartPlay");
            Check(editor.IsPlaying, "User code must run.");
            File.WriteAllText(file, Source(4));
            PumpUntil(() => Field<bool>(editor, "_userCodePendingReload"), "Play must defer source changes.");
            Check(Version(editor) == 3, "Play must retain its code version.");
            Call(editor, "StopPlay");
            Check(Version(editor) == 4, "Stop must apply the pending source version.");

            var good = Player(editor);
            File.WriteAllText(file, Source(5) + "\nthis is broken;");
            PumpUntil(() => editor.FindControl<TextBlock>("FileStatus")!.Text!.Contains("コンパイルに失敗"),
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
                Source(6).Replace("[Inspector] public int Health = 10;", ""),
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
            PumpUntil(() => ComponentAssets.GetTypesForFile(file).Count == 1, "Folder moves must update source mapping.");
            File.Move(file, file + ".disabled");
            PumpUntil(() => editor.FindControl<TextBlock>("FileStatus")!.Text!.Contains("反映できません"),
                "Renaming away the .cs extension must be detected.");
            Check(Version(editor) == 6, "Deleting an attached class must preserve the old scene.");
            File.Move(file + ".disabled", file);
            File.WriteAllText(file, Source(7));
            PumpUntil(() => Version(editor) == 7, "Restoring source must recover.");

            File.WriteAllText(file, Source(8).Replace("namespace Game;", "namespace Renamed.Game;")
                .Replace("class Player", "class Hero"));
            PumpUntil(() => Version(editor) == 8, "Class and namespace renames must reload automatically.");
            Check(Player(editor).GetType().FullName == "Renamed.Game.Hero"
                && ComponentAssets.Registry.GetId(Player(editor).GetType()) == "user.Game.Player"
                && (int)Player(editor).GetType().GetField("Health")!.GetValue(Player(editor))! == 81
                && Field<Scene>(editor, "_scene").Objects.Single().GetStartPriority(Player(editor)) == -9,
                "Renames must retain component identity, Inspector values and Priority.");

            // Failed opening of another project must not mutate active code registrations.
            var other = ProjectFile.Create(parent, "BrokenUserCodeProject", "version: 999\nobjects: []");
            File.WriteAllText(Path.Combine(other.RootDirectory, "Other.cs"), "public class Other {}");
            var activeType = Player(editor).GetType();
            try { ProjectSession.Open(other.ManifestPath); throw new Exception("Expected failure."); }
            catch (InvalidDataException) { }
            Check(ComponentAssets.Registry.GetType("user.Game.Player") == activeType, "Failed project open changed active registration.");

            // Round-trip the reloaded instance, then reopen in a new editor.
            var save = (Task<bool>)Call(editor, "SaveSceneAsync", false)!;
            Check(save.GetAwaiter().GetResult(), "Reloaded scene should save.");
        }
        finally
        {
            typeof(MainWindow).GetField("_sceneDirty", Instance)!.SetValue(editor, false);
            editor.Close();
            Dispatcher.UIThread.RunJobs();
        }
        Check(Field<UserCodeWatcher?>(editor, "_userCodeWatcher") is null, "Closing must stop watching.");
        var reopened = ProjectSession.Open(project.ManifestPath);
        var restored = reopened.Scene.Objects.Single();
        Check(restored.Components.Single().GetType().FullName == "Renamed.Game.Hero",
            "Renamed identity must survive closing and reopening the project.");
        Check(restored.Id == id && (int)restored.Components.Single().GetType().GetField("Health")!.GetValue(restored.Components.Single())! == 81,
            "Saved user components must restore after a fresh compile.");
        var next = ProjectSession.Create(parent, "NoUserCode");
        Check(ComponentAssets.UserTypes.Count == 0, "Switching projects must clear previous user code.");
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

    private static void CheckIdentitySafety(string parent)
    {
        var project = ProjectSession.Create(parent, "IdentityUpgrade").Project;
        var source = Path.Combine(project.RootDirectory, "Legacy.cs");
        var code = "using PureEngine.Core.Attributes; public class Legacy { [Inspector] public int Value = 5; }";
        File.WriteAllText(source, code);
        // Emulate a scene made by the old version, without any identity catalog.
        var compiled = UserCodeCompiler.CompileFiles([source]);
        Check(compiled.Success, "Legacy test compilation failed.");
        ComponentAssets.SetUserCode(compiled);
        var scene = new Scene();
        var item = scene.AddEmpty();
        ComponentAssets.TryAttach(item, compiled.AttachableTypes.Single());
        compiled.AttachableTypes.Single().GetField("Value")!.SetValue(item.Components.Single(), 99);
        SceneFile.Write(project.StartupScenePath, new SceneSerializer(ComponentAssets.Registry).Serialize(scene));
        File.WriteAllText(source, code.Replace("public class Legacy", "namespace NewNamespace; public class Legacy"));
        var opened = ProjectSession.Open(project.ManifestPath);
        var component = opened.Scene.Objects.Single().Components.Single();
        Check(component.GetType().FullName == "NewNamespace.Legacy"
            && ComponentAssets.Registry.GetId(component.GetType()) == "user.Legacy"
            && (int)component.GetType().GetField("Value")!.GetValue(component)! == 99,
            "Pre-catalog scenes must survive a uniquely identifiable namespace change.");

        var duo = Path.Combine(project.RootDirectory, "Duo.cs");
        File.WriteAllText(duo, "public class A {} public class B {}");
        _ = ProjectSession.Open(project.ManifestPath);
        var metadataPath = Path.Combine(project.RootDirectory, ".pureengine", "types.json");
        var metadata = File.ReadAllText(metadataPath);
        File.WriteAllText(duo, "public class C {} public class D {}");
        var ambiguous = UserCodeCompiler.CompileProject(project.RootDirectory);
        Check(!ambiguous.Success && ambiguous.Diagnostics.Any(d => d.Id == "PE-IDENTITY")
            && File.ReadAllText(metadataPath) == metadata, "Ambiguous renames must fail without changing IDs.");
        File.WriteAllText(duo, "namespace Moved; public class A {} public class B {}");
        _ = ProjectSession.Open(project.ManifestPath);
        Check(ComponentAssets.Registry.GetType("user.A").FullName == "Moved.A"
            && ComponentAssets.Registry.GetType("user.B").FullName == "Moved.B",
            "Multiple classes may change namespace when names identify them uniquely.");
        ComponentAssets.ClearUserCode();
    }

    private static void CheckCreateCSharp(ProjectSession session)
    {
        var editor = new MainWindow(session);
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
                && editor.FindControl<TextBlock>("FileStatus")!.Text!.Contains("既にあります"),
                "Duplicate names must not overwrite existing files.");
            foreach (var invalid in new[] { "class", "123Bad", "../Escape", "Bad Name" })
            {
                Create("FilesCreateCSharpMenu", invalid);
                Check(editor.FindControl<TextBlock>("FileStatus")!.Text!.Contains("クラス名"), "Invalid class names must be rejected.");
            }
            Create("FilesCreateCSharpMenu", null);
            Check(Directory.GetFiles(session.Project.ScenesDirectory, "*.cs").Length == 2,
                "Cancellation and invalid names must not create files.");
            PumpUntil(() => ComponentAssets.GetTypesForFile(path).Count == 1 && ComponentAssets.GetTypesForFile(second).Count == 1,
                "Created scripts must enter the normal automatic compilation flow.");
        }
        finally
        {
            editor.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
