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

    /// <summary>Builds the process invocation for a Zed executable and file.</summary>
    public static ProcessStartInfo BuildZedStartInfo(string fullPath, string executable)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(fullPath);
        return start;
    }

    /// <summary>Tries to open a C# file in Zed. Returns false with a user-facing message on failure.</summary>
    public static bool TryOpenCSharpInZed(string fullPath, out string? error)
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
                using var process = Process.Start(BuildZedStartInfo(fullPath, candidate));
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
