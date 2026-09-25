namespace PureEngine.Editor;

/// <summary>
/// File-system operations for D&amp;D import from the OS into the Project pane.
/// Pure logic decoupled from UI (Avalonia IStorageItem resolution), directly verifiable from unit checks.
/// </summary>
public static class ProjectFileImporter
{
    private static StringComparison PathComparison() => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Returns a full path under targetDirectory that does not collide with desiredName.
    /// Files insert " (2)" before the extension, while folders append " (2)" at the end.
    /// </summary>
    public static string GetUniqueDestinationPath(string targetDirectory, string desiredName, bool isDirectory = false)
    {
        if (string.IsNullOrWhiteSpace(desiredName))
            throw new ArgumentException("Specify a file name.", nameof(desiredName));
        if (desiredName is "." or ".." || desiredName.EndsWith('.') || desiredName != desiredName.Trim()
            || desiredName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || desiredName.Contains('/') || desiredName.Contains('\\'))
            throw new ArgumentException("The file name contains characters that cannot be used.", nameof(desiredName));

        targetDirectory = Path.GetFullPath(targetDirectory);
        var candidate = Path.Combine(targetDirectory, desiredName);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
            return candidate;

        var stem = isDirectory ? desiredName : Path.GetFileNameWithoutExtension(desiredName);
        var extension = isDirectory ? "" : Path.GetExtension(desiredName);
        // Without an extension (folders and similar), append the number at the end. With an extension, insert it before the extension.
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

    /// <summary>Copies a single file into targetDirectory and returns the destination full path. Appends a sequence number on name collision.</summary>
    public static string CopyFileInto(string sourceFilePath, string targetDirectory)
    {
        if (!File.Exists(sourceFilePath))
            throw new FileNotFoundException("The source file was not found.", sourceFilePath);
        if ((File.GetAttributes(sourceFilePath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Links cannot be imported.");
        Directory.CreateDirectory(targetDirectory);
        targetDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
        var sourceFull = Path.GetFullPath(sourceFilePath);

        // Dropping into the same folder is a no-op rather than a copy (same as the OS Explorer).
        if (string.Equals(Path.GetDirectoryName(sourceFull), targetDirectory, PathComparison()))
            return sourceFull;

        var destination = GetUniqueDestinationPath(targetDirectory, Path.GetFileName(sourceFull));
        File.Copy(sourceFull, destination);
        return destination;
    }

    /// <summary>Recursively copies a single folder under targetDirectory and returns the destination full path. Appends a sequence number on name collision.</summary>
    public static string CopyDirectoryInto(string sourceDirectoryPath, string targetDirectory)
    {
        if (!Directory.Exists(sourceDirectoryPath))
            throw new DirectoryNotFoundException($"The source folder was not found: {sourceDirectoryPath}");
        if ((File.GetAttributes(sourceDirectoryPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Links cannot be imported.");
        targetDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
        var sourceFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectoryPath));

        // Reject when target is under (or equal to) source because it would recurse infinitely.
        var relative = Path.GetRelativePath(sourceFull, targetDirectory);
        if (!Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("Cannot copy a folder into itself.");

        // Dropping into the same parent is a no-op rather than a copy.
        if (string.Equals(Path.GetDirectoryName(sourceFull), targetDirectory, PathComparison()))
            return sourceFull;

        Directory.CreateDirectory(targetDirectory);
        var destination = GetUniqueDestinationPath(targetDirectory, Path.GetFileName(sourceFull), isDirectory: true);
        CopyDirectoryRecursive(sourceFull, destination);
        return destination;
    }

    /// <summary>Moves a single file into targetDirectory and returns the destination full path. Appends a sequence number on name collision.</summary>
    public static string MoveFileInto(string sourceFilePath, string targetDirectory)
    {
        if (!File.Exists(sourceFilePath))
            throw new FileNotFoundException("The source file was not found.", sourceFilePath);
        if ((File.GetAttributes(sourceFilePath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Links cannot be moved.");
        Directory.CreateDirectory(targetDirectory);
        targetDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
        var sourceFull = Path.GetFullPath(sourceFilePath);

        // Moving into the same folder is a no-op rather than a rename (same as the OS Explorer).
        if (string.Equals(Path.GetDirectoryName(sourceFull), targetDirectory, PathComparison()))
            return sourceFull;

        var destination = GetUniqueDestinationPath(targetDirectory, Path.GetFileName(sourceFull));
        File.Move(sourceFull, destination);
        return destination;
    }

    /// <summary>Moves a single folder under targetDirectory and returns the destination full path. Appends a sequence number on name collision.</summary>
    public static string MoveDirectoryInto(string sourceDirectoryPath, string targetDirectory)
    {
        if (!Directory.Exists(sourceDirectoryPath))
            throw new DirectoryNotFoundException($"The source folder was not found: {sourceDirectoryPath}");
        if ((File.GetAttributes(sourceDirectoryPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Links cannot be moved.");
        targetDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
        var sourceFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectoryPath));

        // Reject when target is under (or equal to) source because it would nest into itself.
        var relative = Path.GetRelativePath(sourceFull, targetDirectory);
        if (!Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("Cannot move a folder into itself.");

        // Moving into the same parent is a no-op rather than a rename.
        if (string.Equals(Path.GetDirectoryName(sourceFull), targetDirectory, PathComparison()))
            return sourceFull;

        Directory.CreateDirectory(targetDirectory);
        var destination = GetUniqueDestinationPath(targetDirectory, Path.GetFileName(sourceFull), isDirectory: true);
        Directory.Move(sourceFull, destination);
        return destination;
    }

    /// <summary>Imports local paths into targetDirectory. Returns the imported destination full paths (excluding skipped entries).</summary>
    public static IReadOnlyList<string> ImportLocalPaths(string targetDirectory, IEnumerable<string> sourcePaths)
    {
        Directory.CreateDirectory(targetDirectory);
        targetDirectory = Path.GetFullPath(targetDirectory);
        var imported = new List<string>();
        HashSet<string> seen = [with(StringComparer.FromComparison(PathComparison()))];
        foreach (var source in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(source)) continue;
            string full;
            try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source)); }
            catch { throw new IOException($"Invalid path: {source}"); }

            if (!seen.Add(full)) continue;
            if (File.Exists(full))
            {
                var destination = CopyFileInto(full, targetDirectory);
                // Exclude drops into the same folder (no-op) from the report.
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
                throw new FileNotFoundException("The source file or folder was not found.", full);
            }
        }
        return imported;
    }

    /// <summary>Copies a stream to a new file and removes the incomplete file on failure.</summary>
    public static async Task<string> CopyStreamIntoAsync(Stream source, string targetDirectory, string name)
    {
        Directory.CreateDirectory(targetDirectory);
        var destination = GetUniqueDestinationPath(targetDirectory, name);
        // When CreateNew fails, do not delete files created by other operations.
        var write = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            await using (write)
                await source.CopyToAsync(write);
        }
        catch
        {
            File.Delete(destination);
            throw;
        }
        return destination;
    }

    private static void CopyDirectoryRecursive(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
        {
            // Exclude broken links and entries that cannot be retrieved again from import.
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
