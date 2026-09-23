using PureEngine.Editor;
using SkiaSharp;

internal static class ProjectAssetChecks
{
    public static void Run(string root)
    {
        var projectDir = Path.Combine(root, "AssetChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projectDir);
        var source = Path.Combine(projectDir, "source.png");
        File.WriteAllBytes(source, CreatePng(32, 16, SKColors.CornflowerBlue));
        var entry = ProjectAssets.ImportImage(projectDir, source);
        Check(entry.Id != Guid.Empty && File.Exists(entry.FullPath) && File.Exists(entry.FullPath + ".pureasset.yaml"),
            "Import must copy the image and write a sidecar with a new ID.");
        var scanned = ProjectAssets.Scan(projectDir);
        Check(scanned.Images.Count == 1 && scanned.Images.ContainsKey(entry.Id)
            && scanned.Diagnostics.Count == 0, "Scan must resolve the imported image without diagnostics.");
        var bytes = scanned.LoadImageBytes();
        Check(bytes.TryGetValue(entry.Id, out var loaded) && loaded.Length > 0,
            "Image bytes must load for the preview.");

        var secondSource = Path.Combine(projectDir, "second.jpg");
        File.WriteAllBytes(secondSource, CreatePng(8, 8, SKColors.OrangeRed));
        File.Move(secondSource, Path.ChangeExtension(secondSource, ".jpg"));
        var second = ProjectAssets.ImportImage(projectDir, Path.ChangeExtension(secondSource, ".jpg"));
        Check(second.Id != entry.Id, "Each import must issue a new ID.");
        scanned = ProjectAssets.Scan(projectDir);
        Check(scanned.Images.Count == 2, "Second import must resolve.");

        var orphanImage = Path.Combine(projectDir, "Assets", "manual.png");
        File.WriteAllBytes(orphanImage, CreatePng(4, 4, SKColors.Green));
        scanned = ProjectAssets.Scan(projectDir);
        Check(scanned.Images.Count == 2 && scanned.Diagnostics.Any(text => text.Contains("manual.png")),
            "Images without sidecars must stay unresolved with a diagnostic.");

        var orphanSidecar = orphanImage + ".pureasset.yaml";
        File.WriteAllText(orphanSidecar, "version: 1\nid: " + Guid.NewGuid().ToString("D") + "\nkind: image\n");
        File.Delete(orphanImage);
        scanned = ProjectAssets.Scan(projectDir);
        Check(scanned.Diagnostics.Any(text => text.Contains("only the registration remains")),
            "Sidecars without images must be reported.");

        File.WriteAllBytes(orphanImage, CreatePng(4, 4, SKColors.Green));
        File.WriteAllText(orphanSidecar, "not: yaml: :");
        scanned = ProjectAssets.Scan(projectDir);
        Check(scanned.Diagnostics.Any(text => text.Contains("corrupted")),
            "Broken sidecars must be reported without resolving.");

        var duplicateId = entry.Id.ToString("D");
        File.WriteAllText(orphanSidecar, $"version: 1\nid: {duplicateId}\nkind: image\n");
        scanned = ProjectAssets.Scan(projectDir);
        Check(!scanned.Images.ContainsKey(entry.Id)
            && scanned.Diagnostics.Any(text => text.Contains("duplicated")),
            "Duplicate IDs must resolve to neither file and be reported.");

        Reject(() => ProjectAssets.ImportImage(projectDir, Path.Combine(projectDir, "missing.png")),
            "Missing source import accepted.");
        var textFile = Path.Combine(projectDir, "note.txt");
        File.WriteAllText(textFile, "hello");
        Reject(() => ProjectAssets.ImportImage(projectDir, textFile), "Non-image import accepted.");
        Check(!ProjectAssets.IsSupportedImage(textFile) && ProjectAssets.IsSupportedImage(source),
            "Image extension check failed.");
        PathBoundaries(root);
        Console.WriteLine("PASS: image import, scan, duplicate/missing/broken diagnostics, and byte loading.");
    }

    private static void PathBoundaries(string root)
    {
        var area = Path.Combine(root, "AssetLinks-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(area, "Project");
        var outside = Path.Combine(area, "Outside");
        var assets = Path.Combine(project, "Assets");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(outside);
        var source = Path.Combine(area, "source.png");
        File.WriteAllBytes(source, CreatePng(2, 2, SKColors.Blue));
        CreateDirectoryLink(assets, outside);
        try
        {
            Reject(() => ProjectAssets.ImportImage(project, source), "Import followed an Assets junction.");
            var scan = ProjectAssets.Scan(project);
            Check(scan.Images.Count == 0 && scan.Diagnostics.Count > 0, "Scan must diagnose an Assets junction without traversing it.");
            Check(!Directory.EnumerateFileSystemEntries(outside).Any(), "Rejected import wrote outside the project.");
        }
        finally { Directory.Delete(assets); } // Remove the link itself, never recursively delete its target.

        var entry = ProjectAssets.ImportImage(project, source);
        var snapshot = ProjectAssets.Scan(project);
        var held = Path.Combine(project, "AssetsBeforeSwap");
        Check(Path.GetFullPath(assets).StartsWith(Path.GetFullPath(project) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && Path.GetFullPath(held).StartsWith(Path.GetFullPath(project) + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            "Both directory move paths must stay in this test project.");
        Directory.Move(assets, held);
        File.WriteAllBytes(Path.Combine(outside, Path.GetFileName(entry.FullPath)), CreatePng(2, 2, SKColors.Red));
        CreateDirectoryLink(assets, outside);
        try { Check(snapshot.LoadImageBytes().Count == 0, "Load must revalidate paths after an index was created."); }
        finally { Directory.Delete(assets); }

        Directory.CreateDirectory(assets);
        var blockedSidecar = Path.Combine(assets, "source.png.pureasset.yaml");
        Directory.CreateDirectory(blockedSidecar);
        Reject(() => ProjectAssets.ImportImage(project, source), "Import should fail when a sidecar destination is a directory.");
        Check(!File.Exists(Path.Combine(assets, "source.png")) && Directory.Exists(blockedSidecar)
            && !Directory.EnumerateFiles(assets, "*.tmp").Any(), "Failed import must roll back only files it created.");
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { _ = Directory.CreateSymbolicLink(link, target); return; }
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
        };
        start.Environment["PUREENGINE_TEST_LINK"] = link;
        start.Environment["PUREENGINE_TEST_TARGET"] = target;
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("New-Item -ItemType Junction -Path $env:PUREENGINE_TEST_LINK -Target $env:PUREENGINE_TEST_TARGET -ErrorAction Stop | Out-Null");
        using var process = System.Diagnostics.Process.Start(start)!;
        if (!process.WaitForExit(10000))
        {
            process.Kill();
            throw new TimeoutException("Test junction creation timed out.");
        }
        Check(process.ExitCode == 0, "Test junction creation failed.");
    }

    private static byte[] CreatePng(int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException)
        { return; }
        throw new InvalidOperationException(message);
    }
}
