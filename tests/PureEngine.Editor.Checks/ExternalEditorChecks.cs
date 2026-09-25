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

        var start = ExternalEditor.BuildZedStartInfo("/tmp/Game/Player.cs", "/tmp/Game", candidates[0]);
        Check(start.FileName == candidates[0], "Zed invocation must use the selected executable.");
        Check(!start.UseShellExecute, "Zed invocation must resolve the executable without a shell.");
        Check(start.ArgumentList.Count == 2 && start.ArgumentList[0] == "/tmp/Game" && start.ArgumentList[1] == "/tmp/Game/Player.cs",
            "Zed invocation must open the project root first and then show the file.");

        var fileOnly = ExternalEditor.BuildZedStartInfo("/tmp/Game/Player.cs", null, candidates[0]);
        Check(fileOnly.ArgumentList.Count == 1 && fileOnly.ArgumentList[0] == "/tmp/Game/Player.cs",
            "Zed invocation without a project root must pass the file path alone.");

        var outside = ExternalEditor.BuildZedStartInfo("/elsewhere/Player.cs", "/tmp/Game", candidates[0]);
        Check(outside.ArgumentList.Count == 1 && outside.ArgumentList[0] == "/elsewhere/Player.cs",
            "Zed invocation for a file outside the project must pass the file path alone.");

        Check(!ExternalEditor.TryOpenCSharpInZed("/tmp/Game/notes.txt", "/tmp/Game", out var notCSharp)
            && notCSharp is not null, "Opening a non-C# file must fail with a message.");
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".cs");
        Check(!ExternalEditor.TryOpenCSharpInZed(missing, "/tmp/Game", out var notFound)
            && notFound is not null, "Opening a missing C# file must fail with a message.");

        Console.WriteLine("PASS: external Zed opening validates C# files and builds the launch command.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
