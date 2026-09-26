using System.Diagnostics;
using System.Numerics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using PureEngine.Rendering;
using PureEngine.Runtime;

namespace PureEngine.Editor;

/// <summary>Produces a relocatable Windows folder from saved source and explicitly selected runtime content.</summary>
public static class WindowsGameBuild
{
    public const string ExecutableName = "PureEngine.Player.exe";
    private const string OwnershipFile = ".pureengine-build";
    private static readonly JsonSerializerOptions ManifestJsonOptions = new() { WriteIndented = true };

    public static string FindEngineRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "src", "PureEngine.Player", "PureEngine.Player.csproj"))
                && File.Exists(Path.Combine(directory.FullName, "global.json")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Build requires the PureEngine source checkout and its pinned .NET SDK.");
    }

    public static async Task<string> BuildAsync(ProjectFile project, string destination, string engineRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ValidateDestination(project.RootDirectory, engineRoot, destination);
        destination = Path.GetFullPath(destination);
        engineRoot = Path.GetFullPath(engineRoot);
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".pure-build-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            // Never adopt the editor's last successful compilation or trust an external-editor csproj.
            var compiled = UserCodeCompiler.CompileProject(project.RootDirectory, cancellationToken);
            try
            {
                if (!compiled.Success)
                    throw new InvalidDataException("Build compilation failed:\n" + string.Join("\n",
                        compiled.Diagnostics.Where(item => item.IsError).Select(item => $"{item.FilePath}:{item.Line}: {item.Message}")));
                await PublishPlayer(engineRoot, staging, cancellationToken);
                CollectContent(project.RootDirectory, Path.Combine(staging, "Data"));
                if (compiled.AssemblyBytes is { } bytes)
                    await File.WriteAllBytesAsync(Path.Combine(staging, "Game.dll"), bytes, cancellationToken);
                var manifest = new GamePackageManifest
                {
                    Version = 1,
                    Name = project.Document.Name ?? throw new InvalidDataException("The project requires a name."),
                    StartupScene = project.Document.StartupScene!,
                    GameAssembly = compiled.AssemblyBytes is null ? null : "Game.dll",
                    Types = compiled.TypeIds.ToDictionary(pair => pair.Value, pair => pair.Key.FullName!),
                };
                await File.WriteAllTextAsync(Path.Combine(staging, "package.json"),
                    JsonSerializer.Serialize(manifest, ManifestJsonOptions), cancellationToken);
                WriteNotices(engineRoot, staging);
                ValidatePublishedFiles(staging);
                using (var package = GamePackage.Open(staging))
                {
                    package.Validate();
                    ValidateImages(package.Assets);
                }
                // Persist new identities only after successful validation, before publishing the package.
                UserCodeIdentity.Save(compiled);
                await File.WriteAllTextAsync(Path.Combine(staging, OwnershipFile), "PureEngine Windows x64 folder build, version 1\n", cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                ValidateDestination(project.RootDirectory, engineRoot, destination);
                PublishStaging(staging, destination);
                return Path.Combine(destination, ExecutableName);
            }
            finally { compiled.LoadContext?.Unload(); }
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    public static void ValidateDestination(string projectRoot, string engineRoot, string destination)
    {
        ValidateWindowsRelativePaths([destination[(Path.GetPathRoot(destination)?.Length ?? 0)..]]);
        // On Windows GetFullPath also expands existing 8.3 aliases (including the existing prefix
        // of a new destination), so containment compares long paths rather than caller spelling.
        destination = Path.GetFullPath(destination);
        ValidateWindowsRelativePaths([destination[Path.GetPathRoot(destination)!.Length..]]);
        RejectLinkedAncestors(destination);
        foreach (var protectedRoot in new[] { projectRoot, engineRoot })
        {
            var root = Path.GetFullPath(protectedRoot);
            RejectLinkedAncestors(root);
            if (Contains(root, destination) || Contains(destination, root))
                throw new InvalidDataException("Build output must be outside both the game project and the engine checkout.");
        }
        if (File.Exists(destination)) throw new IOException("The build destination is a file.");
        if (Directory.Exists(destination) && !File.Exists(Path.Combine(destination, OwnershipFile)))
            throw new IOException("Choose a new folder or an existing PureEngine build folder; unrelated folders are never replaced.");
    }

    private static void RejectLinkedAncestors(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException($"Build paths cannot use symbolic links or junctions: {directory.FullName}");
    }

    /// <summary>Applies Windows component rules regardless of the producer's operating system.</summary>
    public static void ValidateWindowsName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.EndsWith('.') || name.EndsWith(' ')
            || name.Any(character => character < 32 || "<>:\"/\\|?*".Contains(character)))
            throw new InvalidDataException($"Not a valid Windows file or folder name: {name}");
        var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" or "CONIN$" or "CONOUT$"
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal)
                || stem.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(stem[3])))
            throw new InvalidDataException($"Windows device names cannot be used for build files or folders: {name}");
    }

    /// <summary>Rejects names and case-aliased directory components that cannot survive Windows extraction.</summary>
    public static void ValidateWindowsRelativePaths(IEnumerable<string> paths)
    {
        Dictionary<string, string> spellings = [with(StringComparer.OrdinalIgnoreCase)];
        foreach (var path in paths)
        {
            var prefix = "";
            foreach (var name in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                ValidateWindowsName(name);
                prefix = prefix.Length == 0 ? name : Path.Combine(prefix, name);
                if (spellings.TryGetValue(prefix, out var previous) && previous != prefix)
                    throw new InvalidDataException($"Package paths collide on Windows: {previous} and {prefix}");
                spellings[prefix] = prefix;
            }
        }
    }

    private static void ValidatePackagePaths(string root)
    {
        var entries = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories);
        ValidateWindowsRelativePaths(entries.Select(entry => Path.GetRelativePath(root, entry)));
    }

    private static bool Contains(string root, string path) =>
        string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Copies only supported runtime formats; source, editor metadata, and caches never enter a package.</summary>
    public static void CollectContent(string projectRoot, string destination)
    {
        RejectLinkedAncestors(projectRoot);
        Directory.CreateDirectory(destination);
        var pending = new Stack<string>();
        pending.Push(projectRoot);
        var files = new List<string>();
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var name = Path.GetFileName(entry);
                var attributes = File.GetAttributes(entry);
                if (name.StartsWith('.') || name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("obj", StringComparison.OrdinalIgnoreCase)) continue;
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException($"Runtime content cannot use links: {entry}");
                if (attributes.HasFlag(FileAttributes.Directory)) { pending.Push(entry); continue; }
                if (!IsRuntimeContent(name)) continue;
                files.Add(entry);
            }
        }
        ValidateWindowsRelativePaths(files.Select(entry => Path.GetRelativePath(projectRoot, entry)));
        foreach (var entry in files)
        {
            var output = Path.Combine(destination, Path.GetRelativePath(projectRoot, entry));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.Copy(entry, output);
        }
    }

    private static bool IsRuntimeContent(string name) =>
        name.EndsWith(".pure.scene.yaml", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".pure.prefab.yaml", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".pure.asset.yaml", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".pure.loc.yaml", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".pureasset.yaml", StringComparison.OrdinalIgnoreCase)
        || ProjectAssets.IsSupportedImage(name);

    /// <summary>Uses the actual renderer's decoder and atlas rules, without a GPU or game callbacks.</summary>
    public static void ValidateImages(ProjectAssets assets)
    {
        using var draw = new DrawList();
        foreach (var image in assets.Images.Values)
        {
            try
            {
                draw.ResetAtlas();
                draw.Image(image.Id.ToString("N"), File.ReadAllBytes(image.FullPath), Vector2.One,
                    Matrix3x2.Identity, Vector4.One, new Vector4(0, 0, 1, 1));
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException)
            {
                throw new InvalidDataException($"Packaged image cannot be rendered: {image.FullPath}: {error.Message}", error);
            }
        }
    }

    private static async Task PublishPlayer(string engineRoot, string staging, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = engineRoot, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "publish", "src/PureEngine.Player/PureEngine.Player.csproj", "-c", "Release", "-r", "win-x64",
            "--self-contained", "true", "-p:PublishTrimmed=false", "-p:PublishAot=false", "-p:PublishSingleFile=false", "-p:UseAppHost=true", "-o", staging })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start the pinned .NET SDK.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        var output = await stdout;
        var errors = await stderr;
        if (process.ExitCode != 0) throw new IOException($"Player publish failed ({process.ExitCode}).\n{output}\n{errors}");
    }

    public static void ValidatePublishedFiles(string root)
    {
        ValidatePackagePaths(root);
        foreach (var file in new[] { ExecutableName, "PureEngine.Player.dll", "PureEngine.Player.deps.json",
            "PureEngine.Player.runtimeconfig.json", "coreclr.dll", "hostfxr.dll", "hostpolicy.dll" })
            if (!File.Exists(Path.Combine(root, file))) throw new InvalidDataException($"Incomplete self-contained Windows package: {file}");
        // The SDK's resolved deployment graph is authoritative for the Player and its dependencies.
        // Framework facades can carry optional type forwarders to assemblies outside that graph.
        using var dependencies = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "PureEngine.Player.deps.json")));
        var targetName = dependencies.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString()!;
        var target = dependencies.RootElement.GetProperty("targets").GetProperty(targetName);
        foreach (var library in target.EnumerateObject())
        {
            foreach (var kind in new[] { "runtime", "native", "resources" })
            {
                if (!library.Value.TryGetProperty(kind, out var entries)) continue;
                foreach (var asset in entries.EnumerateObject())
                {
                    var name = Path.GetFileName(asset.Name);
                    if (name == "_._") continue;
                    var path = kind == "resources"
                        ? Path.Combine(root, asset.Value.GetProperty("locale").GetString()!, name)
                        : Path.Combine(root, name);
                    if (!File.Exists(path))
                        throw new InvalidDataException($"Missing packaged {kind} dependency: {library.Name} requires {asset.Name}.");
                }
            }
        }
        // Roslyn's game DLL is intentionally not an MSBuild input, so its direct dependencies need
        // an explicit check in addition to the SDK graph. Never allow Editor-only references.
        var gameAssembly = Path.Combine(root, "Game.dll");
        if (File.Exists(gameAssembly))
        {
            using var stream = File.OpenRead(gameAssembly);
            using var image = new PEReader(stream);
            if (!image.HasMetadata) throw new InvalidDataException("The game assembly has no managed metadata.");
            var metadata = image.GetMetadataReader();
            foreach (var handle in metadata.AssemblyReferences)
            {
                var reference = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
                if (!File.Exists(Path.Combine(root, reference + ".dll")))
                    throw new InvalidDataException($"Missing packaged dependency: Game.dll requires {reference}.");
            }
        }
    }

    /// <summary>Same-volume rename publishes atomically; a failed replacement restores the previous successful build.</summary>
    public static void PublishStaging(string staging, string destination)
    {
        if (Directory.Exists(destination) && !File.Exists(Path.Combine(destination, OwnershipFile)))
            throw new IOException("Refusing to replace a folder not owned by PureEngine Build.");
        var backup = destination + ".previous-" + Guid.NewGuid().ToString("N");
        var previous = Directory.Exists(destination);
        if (previous) Directory.Move(destination, backup);
        try { Directory.Move(staging, destination); }
        catch
        {
            if (previous) Directory.Move(backup, destination);
            throw;
        }
        // Failure to clean a backup must not turn successful publication into a reported build failure.
        if (previous)
        {
            try { Directory.Delete(backup, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void WriteNotices(string engineRoot, string staging)
    {
        var assetsPath = Path.Combine(engineRoot, "src", "PureEngine.Player", "obj", "project.assets.json");
        using var assets = JsonDocument.Parse(File.ReadAllText(assetsPath));
        string[] folders = [.. assets.RootElement.GetProperty("packageFolders").EnumerateObject().Select(item => item.Name)];
        var notices = new StringBuilder("PureEngine Windows Player\n\nThird-party package notices and license metadata follow. Original license/notice files are included under licenses/.\n\n");
        var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in assets.RootElement.GetProperty("libraries").EnumerateObject())
        {
            if (library.Value.GetProperty("type").GetString() != "package") continue;
            packages.Add(library.Value.GetProperty("path").GetString()!);
        }
        using var runtime = JsonDocument.Parse(File.ReadAllText(Path.Combine(staging, "PureEngine.Player.runtimeconfig.json")));
        foreach (var framework in runtime.RootElement.GetProperty("runtimeOptions").GetProperty("includedFrameworks").EnumerateArray())
            packages.Add($"{framework.GetProperty("name").GetString()!.ToLowerInvariant()}.runtime.win-x64/{framework.GetProperty("version").GetString()}");
        foreach (var relative in packages.Order(StringComparer.Ordinal))
        {
            var source = folders.Select(folder => Path.Combine(folder, relative)).FirstOrDefault(Directory.Exists)
                ?? throw new FileNotFoundException($"License metadata not found for {relative}.");
            notices.AppendLine(relative);
            var target = Path.Combine(staging, "licenses", relative);
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (!name.Contains("license", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("notice", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("copying", StringComparison.OrdinalIgnoreCase)
                    && !name.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)) continue;
                var output = Path.Combine(target, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.Copy(file, output);
            }
        }
        File.WriteAllText(Path.Combine(staging, "THIRD-PARTY-NOTICES.txt"), notices.ToString());
    }
}
