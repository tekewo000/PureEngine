using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

static class ProjectDropChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static void Run()
    {
        var format = (DataFormat<string>)typeof(PureEngine.Editor.MainWindow)
            .GetField("ProjectPathFormat", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!
            .GetValue(null)!;
        using (var transfer = new DataTransfer())
        {
            transfer.Add(DataTransferItem.Create(format, "/tmp/source.txt"));
            Check(transfer.TryGetValue(format) == "/tmp/source.txt",
                "Project pane move payload must round-trip.");
        }
        var editor = new PureEngine.Editor.MainWindow();
        editor.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var tree = editor.FindControl<TreeView>("ProjectTree")!;
            var files = editor.FindControl<ListBox>("ProjectFiles")!;
            Check(DragDrop.GetAllowDrop(tree), "ProjectTree must accept OS file drops.");
            Check(DragDrop.GetAllowDrop(files), "ProjectFiles must accept OS file drops and internal moves.");
        }
        finally
        {
            editor.Close();
            Dispatcher.UIThread.RunJobs();
        }
        Console.WriteLine("PASS: project pane accepts OS file drops and internal moves.");
    }
}
