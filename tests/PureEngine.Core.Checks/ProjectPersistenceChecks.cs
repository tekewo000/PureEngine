using PureEngine.Core;
using PureEngine.Editor;
using YamlDotNet.Core;

static class ProjectPersistenceChecks
{
    private static readonly string[] InitialScenes = ["Scenes/Main.pure.scene.yaml"];

    public static void Run()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
        static void Reject(Action action, string message)
        {
            try { action(); }
            catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or YamlException) { return; }
            throw new Exception(message);
        }

        var testRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PureEngine-ProjectChecks-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(testRoot);
        try
        {
            using var owner = new PureEngine.Editor.ProjectComponents();
            var serializer = new SceneSerializer(owner.Registry);
            var scene = new Scene();
            var item = scene.AddEmpty();
            item.Attach(new PureEngine.Editor.Samples.PlayerStats { Name = "001", Hp = 25 });
            var yaml = serializer.Serialize(scene);
            var project = ProjectFile.Create(testRoot, "日本語 Game", yaml);
            var opened = ProjectFile.Open(project.ManifestPath);
            Check(opened.Document.Name == "日本語 Game" && opened.Document.StartupScene == "Scenes/Main.pure.scene.yaml",
                "Project metadata did not survive.");
            var restored = serializer.Deserialize(File.ReadAllText(opened.StartupScenePath));
            Check(restored.Objects[0].Id == item.Id && restored.Objects[0].GetComponent<PureEngine.Editor.Samples.PlayerStats>()!.Hp == 25,
                "Creating a project must preserve the current scene.");
            Check(project.ListScenes().SequenceEqual(InitialScenes), "Initial scene listing is wrong.");

            var secondPath = Path.Combine(project.ScenesDirectory, project.NextSceneName());
            SceneFile.Write(secondPath, serializer.Serialize(new Scene()));
            Check(project.NextSceneName() == "Scene2.pure.scene.yaml", "New scene names must not overwrite existing scenes.");
            var nested = Path.Combine(project.ScenesDirectory, "Levels");
            Directory.CreateDirectory(nested);
            var thirdPath = Path.Combine(nested, "Level.pure.scene.yaml");
            SceneFile.Write(thirdPath, yaml);
            File.WriteAllText(Path.Combine(project.ScenesDirectory, "notes.txt"), "not a scene");
            Check(project.ListScenes().Count == 3 && project.ListScenes().Contains("Scenes/Levels/Level.pure.scene.yaml"),
                "Nested scenes must be listed, unrelated files must not.");
            project.SetStartupScene(thirdPath);
            Check(ProjectFile.Open(project.ManifestPath).StartupScenePath == thirdPath, "Startup scene was not persisted.");

            var manifestBefore = File.ReadAllText(project.ManifestPath);
            Reject(() => ProjectFile.Create(testRoot, "日本語 Game", "wrong"), "An existing project was overwritten.");
            Check(File.ReadAllText(project.ManifestPath) == manifestBefore && File.ReadAllText(project.ResolveScenePath("Scenes/Main.pure.scene.yaml")) == yaml,
                "Rejected project creation changed existing files.");
            Reject(() => ProjectFile.Create(testRoot, "../escape", yaml), "A project name escaped its parent.");
            Reject(() => project.ResolveScenePath("../outside.pure.scene.yaml"), "Relative traversal accepted.");
            Reject(() => project.ResolveScenePath(thirdPath), "An absolute manifest scene path was accepted.");
            Reject(() => project.ValidateScenePath(Path.Combine(testRoot, "outside.pure.scene.yaml")), "Outside save accepted.");
            Reject(() => project.ValidateScenePath(Path.Combine(project.ScenesDirectory, "Scene.yaml")), "Invalid scene extension accepted.");
            Reject(() => project.SetStartupScene(Path.Combine(project.ScenesDirectory, "Missing.pure.scene.yaml")), "Missing startup scene accepted.");
            Check(File.ReadAllText(project.ManifestPath) == manifestBefore, "Failed startup change damaged manifest.");

            File.WriteAllText(project.ManifestPath, manifestBefore.Replace("version: 1", "version: 99"));
            Reject(() => ProjectFile.Open(project.ManifestPath), "Unsupported project version accepted.");
            File.WriteAllText(project.ManifestPath, manifestBefore.Replace("Scenes/Levels/Level.pure.scene.yaml", "../outside.pure.scene.yaml"));
            Reject(() => ProjectFile.Open(project.ManifestPath), "Escaping startup scene accepted.");
            File.WriteAllText(project.ManifestPath, "version: 1\nname: A\n");
            Reject(() => ProjectFile.Open(project.ManifestPath), "Incomplete project accepted.");
            File.WriteAllText(project.ManifestPath, manifestBefore);

            var movedRoot = Path.Combine(testRoot, "Moved");
            Check(project.RootDirectory.StartsWith(testRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && movedRoot.StartsWith(testRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal), "Move must stay in the test directory.");
            Directory.Move(project.RootDirectory, movedRoot);
            var moved = ProjectFile.Open(Path.Combine(movedRoot, "Project.pure.project.yaml"));
            Check(serializer.Deserialize(File.ReadAllText(moved.StartupScenePath)).Objects[0].Id == item.Id,
                "Moving the entire project broke scene references.");
            Check(!Directory.EnumerateDirectories(testRoot, ".pure-project-*").Any(), "Project staging folders leaked.");
        }
        finally
        {
            var expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
#pragma warning disable CA2219 // Fail closed: an unsafe cleanup path must never reach Directory.Delete.
            if (Path.GetDirectoryName(testRoot) != expectedParent || !Path.GetFileName(testRoot).StartsWith("PureEngine-ProjectChecks-"))
                throw new InvalidOperationException("Unsafe test cleanup path.");
#pragma warning restore CA2219
            Directory.Delete(testRoot, recursive: true);
        }
    }
}
