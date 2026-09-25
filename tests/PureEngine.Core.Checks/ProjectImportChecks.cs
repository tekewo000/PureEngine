using PureEngine.Editor;

static class ProjectImportChecks
{
    public static void Run()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
        static void Reject(Action action, string message)
        {
            try { action(); }
            catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException) { return; }
            throw new Exception(message);
        }

        var testRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PureEngine-ImportChecks-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(testRoot);
        try
        {
            var target = Path.Combine(testRoot, "project");
            Directory.CreateDirectory(target);
            var outside = Path.Combine(testRoot, "outside");
            Directory.CreateDirectory(outside);

            // Keep fresh names as-is; number duplicates (with and without extensions).
            var first = Path.Combine(target, "a.txt");
            File.WriteAllText(first, "a");
            Check(ProjectFileImporter.GetUniqueDestinationPath(target, "b.txt") == Path.Combine(target, "b.txt"), "Unique path must keep a fresh name.");
            Check(ProjectFileImporter.GetUniqueDestinationPath(target, "a.txt") == Path.Combine(target, "a (2).txt"), "Duplicate file must be numbered.");
            File.WriteAllText(Path.Combine(target, "a (2).txt"), "a2");
            Check(ProjectFileImporter.GetUniqueDestinationPath(target, "a.txt") == Path.Combine(target, "a (3).txt"), "Numbering must skip existing (2).");
            Directory.CreateDirectory(Path.Combine(target, "Docs"));
            Check(ProjectFileImporter.GetUniqueDestinationPath(target, "Docs") == Path.Combine(target, "Docs (2)"), "Duplicate folder must be numbered.");
            Reject(() => ProjectFileImporter.GetUniqueDestinationPath(target, "../escape.txt"), "Path traversal in a file name was accepted.");
            Reject(() => ProjectFileImporter.GetUniqueDestinationPath(target, ""), "Empty file name was accepted.");
            Reject(() => ProjectFileImporter.GetUniqueDestinationPath(target, ".."), "Parent directory name was accepted.");
            Reject(() => ProjectFileImporter.GetUniqueDestinationPath(target, "a.txt."), "Trailing dot alias was accepted.");
            Directory.CreateDirectory(Path.Combine(target, "Assets.v1"));
            Check(ProjectFileImporter.GetUniqueDestinationPath(target, "Assets.v1", isDirectory: true)
                == Path.Combine(target, "Assets.v1 (2)"), "Dotted folder names must keep their suffix.");

            // Single-file copy and same-folder no-op.
            var sourceFile = Path.Combine(outside, "logo.png");
            File.WriteAllBytes(sourceFile, [1, 2, 3]);
            var copied = ProjectFileImporter.CopyFileInto(sourceFile, target);
            Check(File.Exists(copied) && File.ReadAllBytes(copied).SequenceEqual(new byte[] { 1, 2, 3 }), "File copy must preserve bytes.");
            var secondCopy = ProjectFileImporter.CopyFileInto(sourceFile, target);
            Check(secondCopy.EndsWith("logo (2).png", StringComparison.Ordinal) && File.Exists(secondCopy), "Second copy must be numbered.");
            var sameFolder = ProjectFileImporter.CopyFileInto(first, target);
            Check(sameFolder == Path.GetFullPath(first), "Dropping a file onto its own folder must be a no-op.");
            Check(ProjectFileImporter.ImportLocalPaths(target + Path.DirectorySeparatorChar, [first]).Count == 0,
                "A trailing separator must not turn a same-folder file drop into a duplicate.");

            // Recursive folder copy and rejection of copies into their own subtree.
            var sourceDir = Path.Combine(outside, "Assets");
            Directory.CreateDirectory(Path.Combine(sourceDir, "Sub"));
            File.WriteAllText(Path.Combine(sourceDir, "root.txt"), "root");
            File.WriteAllText(Path.Combine(sourceDir, "Sub", "nested.txt"), "nested");
            var copiedDir = ProjectFileImporter.CopyDirectoryInto(sourceDir, target);
            Check(File.ReadAllText(Path.Combine(copiedDir, "root.txt")) == "root"
                && File.ReadAllText(Path.Combine(copiedDir, "Sub", "nested.txt")) == "nested", "Directory copy must be recursive.");
            Reject(() => ProjectFileImporter.CopyDirectoryInto(target, Path.Combine(target, "Child")), "Copying a folder into itself was accepted.");
            Check(!Directory.Exists(Path.Combine(target, "Child")), "Rejected imports must not create the target folder.");
            Reject(() => ProjectFileImporter.CopyDirectoryInto(Path.GetPathRoot(target)!, target), "Copying a filesystem root into itself was accepted.");
            Check(ProjectFileImporter.ImportLocalPaths(target + Path.DirectorySeparatorChar,
                [copiedDir + Path.DirectorySeparatorChar]).Count == 0, "Same-parent folder drops must ignore trailing separators.");

            // Multi-path import (deduplication and missing-source rejection).
            var extra = Path.Combine(outside, "extra.txt");
            File.WriteAllText(extra, "extra");
            var imported = ProjectFileImporter.ImportLocalPaths(target, [sourceFile, extra, sourceFile]);
            Check(imported.Count == 2 && imported.All(File.Exists), "Import must deduplicate and copy every source.");
            Reject(() => ProjectFileImporter.ImportLocalPaths(target, [Path.Combine(outside, "missing.txt")]), "Missing source was accepted.");

            using var stream = new MemoryStream([4, 5, 6]);
            var streamed = ProjectFileImporter.CopyStreamIntoAsync(stream, target, "a.txt").GetAwaiter().GetResult();
            Check(File.ReadAllText(first) == "a" && File.ReadAllBytes(streamed).SequenceEqual(new byte[] { 4, 5, 6 }),
                "Stream import must preserve existing files and copy bytes.");
            using var broken = new BrokenStream();
            Reject(() => ProjectFileImporter.CopyStreamIntoAsync(broken, target, "broken.txt").GetAwaiter().GetResult(),
                "Stream failures must propagate.");
            Check(!File.Exists(Path.Combine(target, "broken.txt")), "Failed stream copies must remove partial files.");

            // Internal moves within the Project pane.
            var moveSource = Path.Combine(outside, "move.txt");
            File.WriteAllText(moveSource, "move");
            var moveTarget = Path.Combine(target, "MoveTarget");
            Directory.CreateDirectory(moveTarget);
            var moved = ProjectFileImporter.MoveFileInto(moveSource, moveTarget);
            Check(File.ReadAllText(moved) == "move" && !File.Exists(moveSource), "File move must relocate bytes.");
            var sameFolderMove = ProjectFileImporter.MoveFileInto(moved, moveTarget);
            Check(sameFolderMove == Path.GetFullPath(moved), "Moving a file onto its own folder must be a no-op.");
            File.WriteAllText(moveSource, "move");
            var movedCollision = ProjectFileImporter.MoveFileInto(moveSource, moveTarget);
            Check(movedCollision.EndsWith("move (2).txt", StringComparison.Ordinal) && File.Exists(movedCollision), "Move collisions must be numbered.");
            var moveDirSource = Path.Combine(outside, "MoveFolder");
            Directory.CreateDirectory(Path.Combine(moveDirSource, "Sub"));
            File.WriteAllText(Path.Combine(moveDirSource, "root.txt"), "root");
            File.WriteAllText(Path.Combine(moveDirSource, "Sub", "nested.txt"), "nested");
            var movedDir = ProjectFileImporter.MoveDirectoryInto(moveDirSource, moveTarget);
            Check(File.ReadAllText(Path.Combine(movedDir, "root.txt")) == "root"
                && File.ReadAllText(Path.Combine(movedDir, "Sub", "nested.txt")) == "nested"
                && !Directory.Exists(moveDirSource), "Directory move must be recursive.");
            var sameParentMove = ProjectFileImporter.MoveDirectoryInto(movedDir, moveTarget);
            Check(sameParentMove == Path.GetFullPath(movedDir), "Moving a folder onto its own parent must be a no-op.");
            Reject(() => ProjectFileImporter.MoveDirectoryInto(moveTarget, Path.Combine(moveTarget, "Child")), "Moving a folder into itself was accepted.");
            Reject(() => ProjectFileImporter.MoveFileInto(Path.Combine(outside, "missing.txt"), moveTarget), "Missing move source was accepted.");
        }
        finally
        {
            var expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
#pragma warning disable CA2219 // Fail closed: an unsafe cleanup path must never reach Directory.Delete.
            if (Path.GetDirectoryName(testRoot) != expectedParent || !Path.GetFileName(testRoot).StartsWith("PureEngine-ImportChecks-"))
                throw new InvalidOperationException("Unsafe test cleanup path.");
#pragma warning restore CA2219
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private sealed class BrokenStream : MemoryStream
    {
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            await destination.WriteAsync(new byte[] { 1 }, cancellationToken);
            throw new IOException("Simulated failure after a partial write.");
        }
    }
}
