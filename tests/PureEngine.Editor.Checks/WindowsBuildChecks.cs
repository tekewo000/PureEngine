using System.Diagnostics;
using System.Text.Json;
using PureEngine.Core;
using PureEngine.Editor;
using PureEngine.Runtime;

internal static class WindowsBuildChecks
{
    public static void Run(string parent)
    {
        var root = Path.Combine(parent, "WindowsBuild");
        var project = Path.Combine(root, "Project");
        var engine = Path.Combine(root, "Engine");
        var output = Path.Combine(root, "Output");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(engine);
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "keep.txt"), "previous");
        Reject(() => WindowsGameBuild.ValidateDestination(project, engine, output), "Unowned destination accepted.");
        Reject(() => WindowsGameBuild.ValidateDestination(project, engine, project), "Project replacement accepted.");
        Reject(() => WindowsGameBuild.ValidateDestination(project, engine, Path.Combine(project, "Build")), "Output inside source accepted.");
        Reject(() => WindowsGameBuild.ValidateDestination(project, engine, root), "Output containing source accepted.");
        Reject(() => WindowsGameBuild.ValidateDestination(project, engine, Path.Combine(engine, "Build")), "Output inside engine accepted.");
        File.WriteAllText(Path.Combine(output, ".pureengine-build"), "PureEngine");
        WindowsGameBuild.ValidateDestination(project, engine, output);
        Reject(() => WindowsGameBuild.PublishStaging(Path.Combine(root, "Absent"), output), "Missing staging accepted.");
        Check(File.ReadAllText(Path.Combine(output, "keep.txt")) == "previous", "Failed publish lost the previous build.");
        var stage = Path.Combine(root, "Stage");
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "new.txt"), "next");
        WindowsGameBuild.PublishStaging(stage, output);
        Check(File.ReadAllText(Path.Combine(output, "new.txt")) == "next" && !File.Exists(Path.Combine(output, "keep.txt")),
            "Successful publish did not replace the package as a unit.");

        foreach (var relative in new[] { "Scenes/Main.pure.scene.yaml", "Data/Stats.pure.asset.yaml", "Props.pure.prefab.yaml",
            "Localization.pure.loc.yaml", "Assets/Image.png", "Assets/Image.png.pureasset.yaml", "Game.cs", "PureEngine.Game.csproj",
            ".pureengine/types.json", "obj/Cached.pure.scene.yaml", "bin/Stale.pure.asset.yaml", ".cache/Hidden.png", "README.md" })
        {
            var file = Path.Combine(project, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, relative);
        }
        var data = Path.Combine(root, "Collected");
        WindowsGameBuild.CollectContent(project, data);
        var files = Directory.GetFiles(data, "*", SearchOption.AllDirectories);
        Check(files.Length == 6 && files.All(file => !file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)),
            "Content selection included source, editor metadata, or cache files.");
        Reject(() => WindowsGameBuild.ValidatePublishedFiles(data), "Incomplete self-contained package accepted.");
        Console.WriteLine("PASS: Windows build content allowlist, output boundaries, rollback and incomplete package rejection.");
    }

    public static async Task Smoke(string parent)
    {
        var root = Path.Combine(parent, "PackageSmoke");
        Directory.CreateDirectory(root);
        var registry = new ComponentRegistry();
        ComponentAssets.RegisterBuiltins(registry);
        var project = ProjectFile.Create(root, "Game", new SceneSerializer(registry).Serialize(new Scene()));
        var source = Path.Combine(project.RootDirectory, "Actor.cs");
        File.WriteAllText(source, Source("Original", 1));
        using (var components = new ProjectComponents())
        {
            var initial = UserCodeCompiler.CompileProject(project.RootDirectory);
            Check(initial.Success, "Smoke source failed to compile.");
            components.Adopt(initial);
            var scene = new Scene();
            Check(components.TryAttach(scene.AddEmpty(), components.Registry.GetType("user.Original")), "Could not attach smoke actor.");
            SceneFile.Write(project.StartupScenePath, new SceneSerializer(components.Registry).Serialize(scene));
        }
        // The build must use this newly saved source, not the editor's previously compiled type.
        File.WriteAllText(source, Source("Renamed", 2));
        var output = Path.Combine(root, "Built");
        var engineRoot = WindowsGameBuild.FindEngineRoot();
        await WindowsGameBuild.BuildAsync(project, output, engineRoot);
        using (var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "package.json"))))
            Check(manifest.RootElement.GetProperty("Types").GetProperty("user.Original").GetString() == "Renamed",
                "Build lost the stable pre-rename type ID.");
        Check(!File.Exists(Path.Combine(output, "smoke-started.txt")), "Build validation executed a game lifecycle callback.");
        var before = File.ReadAllBytes(Path.Combine(output, "Game.dll"));
        File.WriteAllText(source, "this is not valid C#");
        try
        {
            await WindowsGameBuild.BuildAsync(project, output, engineRoot);
            throw new InvalidOperationException("Invalid saved source unexpectedly built.");
        }
        catch (InvalidDataException) { }
        Check(before.SequenceEqual(File.ReadAllBytes(Path.Combine(output, "Game.dll"))), "Compilation failure modified the previous package.");
        var relocated = Path.Combine(root, "Relocated");
        Directory.Move(output, relocated);
        // Delete the test-owned project: loading must depend exclusively on the relocated folder.
        Directory.Delete(project.RootDirectory, recursive: true);
        using (var package = GamePackage.Open(relocated)) package.Validate();
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo(Path.Combine(relocated, WindowsGameBuild.ExecutableName))
            {
                WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            start.ArgumentList.Add("--smoke-test");
            start.Environment["DOTNET_ROOT"] = Path.Combine(root, "NoRuntime");
            start.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
            using var process = Process.Start(start) ?? throw new IOException("Could not launch packaged Player.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }
            Check(process.ExitCode == 0, $"Packaged Player failed: {await stdout}\n{await stderr}");
            Check(File.ReadAllText(Path.Combine(relocated, "smoke-started.txt")) == "2", "Player did not execute the newly saved game DLL after relocation.");
        }
        Console.WriteLine("PASS: full Windows self-contained publish, current source, stable renamed ID, failure protection and source-free relocated package.");
    }

    private static string Source(string name, int marker) => $$"""
        using PureEngine.Core;
        public class {{name}}
        {
            [Start] public void Start() => System.IO.File.WriteAllText(
                System.IO.Path.Combine(System.AppContext.BaseDirectory, "smoke-started.txt"), "{{marker}}");
        }
        """;

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is IOException or InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
