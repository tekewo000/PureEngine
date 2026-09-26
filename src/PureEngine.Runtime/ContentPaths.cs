namespace PureEngine.Runtime;

/// <summary>Containment and link checks shared by read-only packages and image asset indexing.</summary>
public static class ContentPaths
{
    public static void Validate(string root, string path)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        path = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Specify a path inside the content folder.");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (var entry = path; !string.Equals(entry, root, comparison); entry = Path.GetDirectoryName(entry)!)
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(entry); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Links cannot be used for content files or folders.");
        }
    }

    public static string Resolve(string root, string relative)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relative);
        if (Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new InvalidDataException("Content paths must be relative.");
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
        Validate(root, path);
        return path;
    }
}
