using System.Diagnostics;
using System.Text.Json;
using PureEngine.Core;
using PureEngine.Editor;
using PureEngine.Runtime;
using SkiaSharp;

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
        Reject(() => WindowsGameBuild.ValidateDestination(project, engine, Path.Combine(root, "Invalid.")), "Trailing-dot output was normalized instead of rejected.");
        Reject(() => WindowsGameBuild.ValidateDestination(project, engine, Path.Combine(root, "Invalid ")), "Trailing-space output was normalized instead of rejected.");
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
        ValidateDeploymentGraph(root);
        ValidateWindowsNames();
        RejectLinkedRoots(root, project, engine);
        RejectShortAliases(root, project, engine);
        SaveConflicts(root);
        ImageValidation(root);
        Console.WriteLine("PASS: Windows build content allowlist, output boundaries, rollback and incomplete package rejection.");
    }

    private static void ValidateWindowsNames()
    {
        foreach (var name in new[] { "", ".", "..", "trailing.", "trailing ", "CON", "nul.png", "CoM1.txt",
            "LPT9", "com¹.jpg", "aux .yaml", "a<b", "a>b", "a:b", "a\"b", "a/b", "a\\b", "a|b", "a?b", "a*b", "a\u0001b" })
            Reject(() => WindowsGameBuild.ValidateWindowsName(name), $"Invalid Windows name accepted: {name}");
        foreach (var name in new[] { "Game", "com10", "console", "Scene.pure.scene.yaml", "assets-1" })
            WindowsGameBuild.ValidateWindowsName(name);
        Reject(() => WindowsGameBuild.ValidateWindowsRelativePaths(["licenses/font.txt", "Licenses/package.txt"]),
            "Case-aliased package directories accepted.");
        Reject(() => WindowsGameBuild.ValidateWindowsRelativePaths(["Data/asset.png", "Data/ASSET.png"]),
            "Case-aliased package files accepted.");
        Reject(() => WindowsGameBuild.ValidateWindowsRelativePaths(["Data/CON/asset.png"]),
            "Windows device directory accepted.");
        WindowsGameBuild.ValidateWindowsRelativePaths(["licenses/font.txt", "licenses/package.txt"]);
    }

    private static void RejectLinkedRoots(string root, string project, string engine)
    {
        var alias = Path.Combine(root, "Alias");
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            start.Environment["PUREENGINE_TEST_LINK"] = alias;
            start.Environment["PUREENGINE_TEST_TARGET"] = root;
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("New-Item -ItemType Junction -Path $env:PUREENGINE_TEST_LINK -Target $env:PUREENGINE_TEST_TARGET -ErrorAction Stop | Out-Null");
            using var process = Process.Start(start)!;
            if (!process.WaitForExit(10000))
            {
                process.Kill();
                throw new TimeoutException("Build test junction creation timed out.");
            }
            Check(process.ExitCode == 0, "Could not create the build root test junction.");
        }
        else _ = Directory.CreateSymbolicLink(alias, root);
        try
        {
            var destination = Path.Combine(project, "HiddenOutput");
            Reject(() => WindowsGameBuild.ValidateDestination(Path.Combine(alias, "Project"), engine, destination),
                "Aliased project ancestor bypassed output containment.");
            Reject(() => WindowsGameBuild.ValidateDestination(project, Path.Combine(alias, "Engine"), Path.Combine(engine, "HiddenOutput")),
                "Aliased engine ancestor bypassed output containment.");
            Reject(() => WindowsGameBuild.ValidateDestination(alias, engine, Path.Combine(root, "OtherOutput")),
                "Linked project root was accepted.");
            Check(!Directory.Exists(destination), "Root-link validation modified the project.");
        }
        finally
        {
            Check(File.GetAttributes(alias).HasFlag(FileAttributes.ReparsePoint), "Refusing to remove a test link replaced with a real directory.");
            Directory.Delete(alias);
        }
    }

    private static void SaveConflicts(string root)
    {
        using var documents = new EditorDocuments();
        var path = Path.Combine(root, "Conflict.pure.asset.yaml");
        File.WriteAllText(path, "saved version");
        var asset = new PureEngine.Editor.Samples.PlayerStats { Hp = 11 };
        var table = new PureEngine.Editor.Samples.PlayerStats { Hp = 22 };
        documents.Asset = new DataAssetEditState(path, asset, Guid.NewGuid(), "sample.player-stats") { Dirty = true };
        documents.Table.Rows.Add(new DataAssetEditState(Path.Combine(root, ".", "Conflict.pure.asset.yaml"),
            table, documents.Asset.Id, "sample.player-stats") { Dirty = true });
        Check(documents.BuildSaveConflict() is not null, "Independent dirty edits to the same asset were not blocked before saving.");
        Check(asset.Hp == 11 && table.Hp == 22 && documents.Asset.Dirty && documents.Table.IsDirty
            && File.ReadAllText(path) == "saved version", "Conflict detection changed an edit or the saved file.");
        documents.Asset.Dirty = false;
        Check(documents.BuildSaveConflict() is null, "A clean asset view blocked saving the dirty table.");
        documents.Asset.Dirty = true;
        documents.Table.Rows[0].Dirty = false;
        Check(documents.BuildSaveConflict() is null, "A clean table row blocked saving the dirty asset.");
    }

    private static void RejectShortAliases(string root, string project, string engine)
    {
        if (!OperatingSystem.IsWindows()) return;
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        };
        start.Environment["PUREENGINE_TEST_ROOT"] = root;
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("(New-Object -ComObject Scripting.FileSystemObject).GetFolder($env:PUREENGINE_TEST_ROOT).ShortPath");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(10000))
        {
            process.Kill();
            throw new TimeoutException("Build test short-path lookup timed out.");
        }
        Check(process.ExitCode == 0, "Could not query the Windows short-path fixture.");
        var alias = output.GetAwaiter().GetResult().Trim();
        if (string.Equals(alias, root, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("SKIP: fixture volume does not provide a distinct 8.3 alias.");
            return;
        }
        Check(string.Equals(Path.GetFullPath(alias), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase),
            "The pinned runtime did not canonicalize the 8.3 fixture.");
        Reject(() => WindowsGameBuild.ValidateDestination(Path.Combine(alias, "Project"), engine, Path.Combine(project, "HiddenOutput")),
            "8.3 project alias bypassed output containment.");
        Reject(() => WindowsGameBuild.ValidateDestination(project, Path.Combine(alias, "Engine"), Path.Combine(engine, "HiddenOutput")),
            "8.3 engine alias bypassed output containment.");
        Reject(() => WindowsGameBuild.ValidateDestination(project, engine, Path.Combine(alias, "Project", "HiddenOutput")),
            "8.3 output alias bypassed project containment.");
    }

    private static void ImageValidation(string root)
    {
        var project = Path.Combine(root, "ImageValidation");
        Directory.CreateDirectory(project);
        var source = Path.Combine(root, "BuildImage.png");
        File.WriteAllBytes(source, Encode(2, 2));
        var image = ProjectAssets.ImportImage(project, source);
        var assets = ProjectAssets.Scan(project);
        WindowsGameBuild.ValidateImages(assets);
        File.WriteAllText(image.FullPath, "corrupt PNG with a valid asset sidecar");
        Reject(() => WindowsGameBuild.ValidateImages(assets), "Corrupt packaged image was accepted.");
        File.WriteAllBytes(image.FullPath, Encode(2047, 1));
        Reject(() => WindowsGameBuild.ValidateImages(assets), "Image exceeding the renderer's atlas dimensions was accepted.");
    }

    private static byte[] Encode(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.Blue);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void ValidateDeploymentGraph(string root)
    {
        var package = Path.Combine(root, "DependencyGraph");
        Directory.CreateDirectory(package);
        foreach (var name in new[] { WindowsGameBuild.ExecutableName, "PureEngine.Player.dll",
            "PureEngine.Player.runtimeconfig.json", "coreclr.dll", "hostfxr.dll", "hostpolicy.dll", "Required.dll", "RequiredNative.dll" })
            File.WriteAllText(Path.Combine(package, name), "");
        File.Copy(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "mscorlib.dll"),
            Path.Combine(package, "mscorlib.dll"));
        File.WriteAllText(Path.Combine(package, "PureEngine.Player.deps.json"), """
            {
              "runtimeTarget": { "name": "net11.0/win-x64" },
              "targets": {
                "net11.0/win-x64": {
                  "Player/1.0": {
                    "runtime": { "Required.dll": {}, "mscorlib.dll": {} },
                    "native": { "runtimes/win-x64/native/RequiredNative.dll": {} }
                  }
                }
              }
            }
            """);
        WindowsGameBuild.ValidatePublishedFiles(package);
        File.Delete(Path.Combine(package, "RequiredNative.dll"));
        Reject(() => WindowsGameBuild.ValidatePublishedFiles(package), "Missing native deployment dependency accepted.");
        File.WriteAllText(Path.Combine(package, "RequiredNative.dll"), "");
        File.Delete(Path.Combine(package, "Required.dll"));
        Reject(() => WindowsGameBuild.ValidatePublishedFiles(package), "Missing managed deployment dependency accepted.");
        File.WriteAllText(Path.Combine(package, "Required.dll"), "");
        File.Copy(typeof(WindowsGameBuild).Assembly.Location, Path.Combine(package, "Game.dll"));
        Reject(() => WindowsGameBuild.ValidatePublishedFiles(package), "Game assembly with unpackaged dependencies accepted.");
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
        File.WriteAllText(source, Source("Renamed", 2));
        var imageSource = Path.Combine(root, "Rejected.png");
        File.WriteAllBytes(imageSource, [0, 1, 2, 3]);
        var rejectedImage = ProjectAssets.ImportImage(project.RootDirectory, imageSource);
        foreach (var bytes in new[] { File.ReadAllBytes(imageSource), Encode(2047, 1) })
        {
            File.WriteAllBytes(rejectedImage.FullPath, bytes);
            try
            {
                await WindowsGameBuild.BuildAsync(project, output, engineRoot);
                throw new InvalidOperationException("Unrenderable packaged image unexpectedly built.");
            }
            catch (InvalidDataException) { }
            Check(before.SequenceEqual(File.ReadAllBytes(Path.Combine(output, "Game.dll"))),
                "Image validation failure modified the previous successful package.");
        }
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
