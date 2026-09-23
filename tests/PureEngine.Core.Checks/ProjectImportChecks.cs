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

            // 重複なしはそのまま、重複時は連番になる（拡張子あり／なし両対応）。
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

            // 単一ファイルのコピーと同一フォルダのno-op。
            var sourceFile = Path.Combine(outside, "logo.png");
            File.WriteAllBytes(sourceFile, [1, 2, 3]);
            var copied = ProjectFileImporter.CopyFileInto(sourceFile, target);
            Check(File.Exists(copied) && File.ReadAllBytes(copied).SequenceEqual(new byte[] { 1, 2, 3 }), "File copy must preserve bytes.");
            var secondCopy = ProjectFileImporter.CopyFileInto(sourceFile, target);
            Check(secondCopy.EndsWith("logo (2).png", StringComparison.Ordinal) && File.Exists(secondCopy), "Second copy must be numbered.");
            var sameFolder = ProjectFileImporter.CopyFileInto(first, target);
            Check(sameFolder == Path.GetFullPath(first), "Dropping a file onto its own folder must be a no-op.");

            // フォルダの再帰コピーと自己配下への拒否。
            var sourceDir = Path.Combine(outside, "Assets");
            Directory.CreateDirectory(Path.Combine(sourceDir, "Sub"));
            File.WriteAllText(Path.Combine(sourceDir, "root.txt"), "root");
            File.WriteAllText(Path.Combine(sourceDir, "Sub", "nested.txt"), "nested");
            var copiedDir = ProjectFileImporter.CopyDirectoryInto(sourceDir, target);
            Check(File.ReadAllText(Path.Combine(copiedDir, "root.txt")) == "root"
                && File.ReadAllText(Path.Combine(copiedDir, "Sub", "nested.txt")) == "nested", "Directory copy must be recursive.");
            Reject(() => ProjectFileImporter.CopyDirectoryInto(target, Path.Combine(target, "Child")), "Copying a folder into itself was accepted.");

            // 複数パスの取り込み（重複排除・欠損拒否）。
            var extra = Path.Combine(outside, "extra.txt");
            File.WriteAllText(extra, "extra");
            var imported = ProjectFileImporter.ImportLocalPaths(target, [sourceFile, extra, sourceFile]);
            Check(imported.Count == 2 && imported.All(File.Exists), "Import must deduplicate and copy every source.");
            Reject(() => ProjectFileImporter.ImportLocalPaths(target, [Path.Combine(outside, "missing.txt")]), "Missing source was accepted.");
        }
        finally
        {
            var expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (Path.GetDirectoryName(testRoot) != expectedParent || !Path.GetFileName(testRoot).StartsWith("PureEngine-ImportChecks-"))
                throw new InvalidOperationException("Unsafe test cleanup path.");
            Directory.Delete(testRoot, recursive: true);
        }
    }
}
