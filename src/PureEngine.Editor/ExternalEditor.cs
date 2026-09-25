using System.Diagnostics;

namespace PureEngine.Editor;

/// <summary>Opens project C# files in the external editor (Zed).</summary>
public static class ExternalEditor
{
    /// <summary>Checks the C# extension. Callers also check the explorer kind.</summary>
    public static bool IsCSharpFile(string? path) =>
        path is not null && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>Zed executables to try in order. Covers PATH plus well-known install locations.</summary>
    public static string[] ZedCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            List<string> candidates = ["zed"];
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
                candidates.Add(Path.Combine(localAppData, "Programs", "Zed", "zed.exe"));
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(programFiles))
                candidates.Add(Path.Combine(programFiles, "Zed", "zed.exe"));
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(programFilesX86))
                candidates.Add(Path.Combine(programFilesX86, "Zed", "zed.exe"));
            return [.. candidates];
        }
        if (OperatingSystem.IsLinux())
            return ["zed", "zeditor"];
        return ["zed"];
    }

    /// <summary>Builds the process invocation for a Zed executable, project root, and file. The root comes first so Zed opens the project workspace and shows the file.</summary>
    public static ProcessStartInfo BuildZedStartInfo(string fullPath, string? projectRoot, string executable)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
        };
        if (projectRoot is not null && IsWithinProject(fullPath, projectRoot))
            start.ArgumentList.Add(projectRoot);
        start.ArgumentList.Add(fullPath);
        return start;
    }

    /// <summary>Checks whether the file lives under the project root. Files outside fall back to opening the file alone.</summary>
    private static bool IsWithinProject(string fullPath, string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(projectRoot))
            return false;
        string root;
        string file;
        try
        {
            root = Path.GetFullPath(projectRoot);
            file = Path.GetFullPath(fullPath);
        }
        catch (Exception pathError) when (pathError is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(root, file, comparison))
            return false;
        var relative = Path.GetRelativePath(root, file);
        return !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>Tries to open a C# file in Zed from the project root. Returns false with a user-facing message on failure.</summary>
    public static bool TryOpenCSharpInZed(string fullPath, string? projectRoot, out string? error)
    {
        if (!IsCSharpFile(fullPath))
        {
            error = $"Not a C# file: {fullPath}";
            return false;
        }
        if (!File.Exists(fullPath))
        {
            error = $"C# file not found: {fullPath}";
            return false;
        }
        Exception? lastError = null;
        foreach (var candidate in ZedCandidates())
        {
            try
            {
                using var process = Process.Start(BuildZedStartInfo(fullPath, projectRoot, candidate));
                if (process is null)
                {
                    lastError = new InvalidOperationException($"Could not start {candidate}.");
                    continue;
                }
                error = null;
                return true;
            }
            catch (Exception launchError) when (launchError is System.ComponentModel.Win32Exception or FileNotFoundException or DirectoryNotFoundException)
            {
                lastError = launchError;
            }
            catch (Exception launchError)
            {
                error = $"Could not open in Zed: {launchError.GetBaseException().Message}";
                return false;
            }
        }
        error = lastError is not null
            ? $"Could not open in Zed. Install Zed and make sure the 'zed' command is on PATH. ({lastError.GetBaseException().Message})"
            : "Could not open in Zed. Install Zed and make sure the 'zed' command is on PATH.";
        return false;
    }
}
