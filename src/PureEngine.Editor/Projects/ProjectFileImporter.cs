namespace PureEngine.Editor;

/// <summary>
/// OSからProjectペインへのD&amp;D取り込みにおけるファイルシステム操作。
/// UI（AvaloniaのIStorageItem解決）から切り離した純粋ロジックで、単体Checksから直接検証できる。
/// </summary>
public static class ProjectFileImporter
{
    private static StringComparison PathComparison() => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// targetDirectory直下でdesiredNameと重複しないフルパスを返す。
    /// ファイルは拡張子の前に" (2)"を挿入し、フォルダは末尾に" (2)"を付与する。
    /// </summary>
    public static string GetUniqueDestinationPath(string targetDirectory, string desiredName)
    {
        if (string.IsNullOrWhiteSpace(desiredName))
            throw new ArgumentException("ファイル名を指定してください。", nameof(desiredName));
        if (desiredName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || desiredName.Contains('/') || desiredName.Contains('\\'))
            throw new ArgumentException("ファイル名に使えない文字が含まれています。", nameof(desiredName));

        targetDirectory = Path.GetFullPath(targetDirectory);
        var candidate = Path.Combine(targetDirectory, desiredName);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
            return candidate;

        var stem = Path.GetFileNameWithoutExtension(desiredName);
        var extension = Path.GetExtension(desiredName);
        // 拡張子なし（フォルダ等）は末尾に番号を付ける。拡張子ありは拡張子の前に挿入する。
        for (var number = 2; ; number++)
        {
            var numbered = string.IsNullOrEmpty(extension)
                ? $"{stem} ({number})"
                : $"{stem} ({number}){extension}";
            candidate = Path.Combine(targetDirectory, numbered);
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }

    /// <summary>単一ファイルをtargetDirectoryへコピーし、コピー先フルパスを返す。同名時は連番を付与する。</summary>
    public static string CopyFileInto(string sourceFilePath, string targetDirectory)
    {
        if (!File.Exists(sourceFilePath))
            throw new FileNotFoundException("コピー元のファイルが見つかりません。", sourceFilePath);
        Directory.CreateDirectory(targetDirectory);
        targetDirectory = Path.GetFullPath(targetDirectory);
        var sourceFull = Path.GetFullPath(sourceFilePath);

        // 同一フォルダへのD&Dは複製ではなく何もしない（OSのExplorerと同様）。
        if (string.Equals(Path.GetDirectoryName(sourceFull), targetDirectory, PathComparison()))
            return sourceFull;

        var destination = GetUniqueDestinationPath(targetDirectory, Path.GetFileName(sourceFull));
        File.Copy(sourceFull, destination);
        return destination;
    }

    /// <summary>単一フォルダをtargetDirectory配下へ再帰コピーし、コピー先フルパスを返す。同名時は連番を付与する。</summary>
    public static string CopyDirectoryInto(string sourceDirectoryPath, string targetDirectory)
    {
        if (!Directory.Exists(sourceDirectoryPath))
            throw new DirectoryNotFoundException($"コピー元のフォルダが見つかりません: {sourceDirectoryPath}");
        Directory.CreateDirectory(targetDirectory);
        targetDirectory = Path.GetFullPath(targetDirectory);
        var sourceFull = Path.GetFullPath(sourceDirectoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        // targetがsource配下（または同一）の場合は無限再帰になるため拒否する。
        if (string.Equals(targetDirectory, sourceFull, PathComparison())
            || (targetDirectory + Path.DirectorySeparatorChar).StartsWith(sourceFull + Path.DirectorySeparatorChar, PathComparison()))
            throw new IOException("フォルダを自身の配下にはコピーできません。");

        // 同一親へのD&Dは複製ではなく何もしない。
        if (string.Equals(Path.GetDirectoryName(sourceFull), targetDirectory, PathComparison()))
            return sourceFull;

        var destination = GetUniqueDestinationPath(targetDirectory, Path.GetFileName(sourceFull));
        CopyDirectoryRecursive(sourceFull, destination);
        return destination;
    }

    /// <summary>ローカルパス群をtargetDirectoryへ取り込む。戻り値は取り込み先フルパスの列（スキップ分を除く）。</summary>
    public static IReadOnlyList<string> ImportLocalPaths(string targetDirectory, IEnumerable<string> sourcePaths)
    {
        Directory.CreateDirectory(targetDirectory);
        targetDirectory = Path.GetFullPath(targetDirectory);
        var imported = new List<string>();
        var seen = new HashSet<string>(StringComparer.FromComparison(PathComparison()));
        foreach (var source in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(source)) continue;
            string full;
            try { full = Path.GetFullPath(source); }
            catch { throw new IOException($"無効なパスです: {source}"); }

            if (!seen.Add(full)) continue;
            if (File.Exists(full))
            {
                var destination = CopyFileInto(full, targetDirectory);
                // 同一フォルダへのドロップ（no-op）は報告対象外にする。
                if (!string.Equals(destination, full, PathComparison()))
                    imported.Add(destination);
            }
            else if (Directory.Exists(full))
            {
                var destination = CopyDirectoryInto(full, targetDirectory);
                if (!string.Equals(destination, full, PathComparison()))
                    imported.Add(destination);
            }
            else
            {
                throw new FileNotFoundException("コピー元のファイルまたはフォルダが見つかりません。", full);
            }
        }
        return imported;
    }

    private static void CopyDirectoryRecursive(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
        {
            // リンク切れや再取得不可のエントリは取り込み対象外にする。
            FileAttributes attributes;
            try { attributes = File.GetAttributes(file); }
            catch { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.TopDirectoryOnly))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(directory); }
            catch { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
            CopyDirectoryRecursive(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}
