using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PureEngine.Core;
using PureEngine.Editor;

static class EditorChromeChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Click(MenuItem item)
    {
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    public static void Run()
    {
        var editor = new MainWindow();
        editor.Show();
        Dispatcher.UIThread.RunJobs();
        var store = (EditSceneStore)typeof(MainWindow).GetField("_editScene", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
        try
        {
            var sceneTab = editor.FindControl<TabItem>("SceneViewTab")!;
            var placeholder = editor.FindControl<TextBlock>("GamePlaceholder")!;
            var selection = editor.FindControl<TextBlock>("SelectionStatus")!;
            var zoom = editor.FindControl<TextBlock>("SceneZoomStatus")!;
            Check(Equals(sceneTab.Header, "Scene View"), "A clean scene must show a plain Scene View tab.");
            Check(placeholder.IsVisible, "The Game tab must explain itself before Play.");
            Check(selection.Text == "No selection", "The status bar must show the empty selection.");
            Check(zoom.Text == "Scene View: 100%", "The status bar must show the initial zoom.");

            var menu = editor.FindControl<Grid>("SceneSurface")!.ContextMenu!;
            Click(menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Add Empty")));
            Check(Equals(sceneTab.Header, "Scene View *"), "The Scene tab must carry the same unsaved marker as the title.");
            Check(selection.Text == "Selected: Empty", "The status bar must follow the Stuffs selection.");

            typeof(MainWindow).GetField("_sceneZoom", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor, 1.5f);
            typeof(MainWindow).GetMethod("UpdateStatusBarSegments", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null);
            Check(zoom.Text == "Scene View: 150%", "The status bar must follow the Scene View zoom.");

            var ui = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "UI"));
            Click(ui.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Image")));
            var axisBox = editor.GetVisualDescendants().OfType<TextBox>()
                .FirstOrDefault(box => Equals(box.GetValue(AutomationProperties.NameProperty), "Transform.LocalPosition.X"));
            Check(axisBox is not null && axisBox.Classes.Contains("numericField"),
                "Numeric Inspector fields must use the numeric face.");

            typeof(MainWindow).GetMethod("StartPlay", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null);
            Check(!placeholder.IsVisible, "The Game placeholder must hide while playing.");
            typeof(MainWindow).GetMethod("StopPlay", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null);
            Check(placeholder.IsVisible, "The Game placeholder must return after Stop.");
        }
        finally
        {
            store.MarkClean();
            editor.Close();
            Dispatcher.UIThread.RunJobs();
        }
        Console.WriteLine("PASS: Editor chrome status segments, dirty tab marker, Game placeholder, and numeric field face.");
    }
}
