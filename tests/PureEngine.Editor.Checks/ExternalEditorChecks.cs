using PureEngine.Editor;

internal static class ExternalEditorChecks
{
    public static void Run()
    {
        Check(ExternalEditor.IsCSharpFile("Player.cs"), "Lowercase .cs must count as a C# file.");
        Check(ExternalEditor.IsCSharpFile("Player.CS"), "C# extension matching must ignore case.");
        Check(!ExternalEditor.IsCSharpFile("notes.txt"), "Other extensions must not count as C# files.");
        Check(!ExternalEditor.IsCSharpFile(null), "Missing paths must not count as C# files.");

        var candidates = ExternalEditor.ZedCandidates();
        Check(candidates.Length > 0, "At least one Zed executable must be configured.");
        Check(candidates[0] == "zed", "The PATH-based zed command must be tried first.");

        var start = ExternalEditor.BuildZedStartInfo("/tmp/Game/Player.cs", candidates[0]);
        Check(start.FileName == candidates[0], "Zed invocation must use the selected executable.");
        Check(!start.UseShellExecute, "Zed invocation must resolve the executable without a shell.");
        Check(start.ArgumentList.Count == 1 && start.ArgumentList[0] == "/tmp/Game/Player.cs",
            "Zed invocation must pass the file path as a single argument.");

        Check(!ExternalEditor.TryOpenCSharpInZed("/tmp/Game/notes.txt", out var notCSharp)
            && notCSharp is not null, "Opening a non-C# file must fail with a message.");
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".cs");
        Check(!ExternalEditor.TryOpenCSharpInZed(missing, out var notFound)
            && notFound is not null, "Opening a missing C# file must fail with a message.");

        Console.WriteLine("PASS: external Zed opening validates C# files and builds the launch command.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
