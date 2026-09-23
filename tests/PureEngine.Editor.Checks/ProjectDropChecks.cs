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
        var editor = new PureEngine.Editor.MainWindow();
        editor.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var tree = editor.FindControl<TreeView>("ProjectTree")!;
            var files = editor.FindControl<ListBox>("ProjectFiles")!;
            Check(DragDrop.GetAllowDrop(tree), "ProjectTree must accept OS file drops.");
            Check(DragDrop.GetAllowDrop(files), "ProjectFiles must accept OS file drops.");
        }
        finally
        {
            editor.Close();
            Dispatcher.UIThread.RunJobs();
        }
        Console.WriteLine("PASS: project pane accepts OS file drops.");
    }
}
