using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PureEngine.Core;
using PureEngine.Editor;
using Button = Avalonia.Controls.Button;

/// <summary>Guards Stuffs multi-selection, Ctrl+D duplication, and Delete-key removal.</summary>
internal static class StuffsMultiChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static T Control<T>(MainWindow window, string name) where T : Control =>
        window.FindControl<T>(name)!;

    private static MainWindow CreateEditor()
    {
        _ = Log.Drain();
        var editor = new MainWindow { WindowState = WindowState.Normal, Width = 1280, Height = 800 };
        Control<Grid>(editor, "SceneViewport").Children.Clear();
        Control<Grid>(editor, "GameViewport").Children.Clear();
        editor.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        _ = Log.Drain();
        return editor;
    }

    private static void CloseEditor(MainWindow editor)
    {
        var store = (EditSceneStore)typeof(MainWindow)
            .GetProperty("EditSceneStore", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(editor)!;
        store.MarkClean();
        editor.Close();
        Dispatcher.UIThread.RunJobs();
        var dialog = editor.OwnedWindows.SingleOrDefault(window => window.Title == "Unsaved Scene");
        if (dialog is not null)
        {
            dialog.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, "Discard"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        }
    }

    public static void Run()
    {
        var editor = CreateEditor();
        try
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var store = (EditSceneStore)typeof(MainWindow).GetProperty("EditSceneStore", flags)!.GetValue(editor)!;
            var scene = store.Current;
            var tree = Control<TreeView>(editor, "SceneObjects");
            Check(tree.SelectionMode == SelectionMode.Multiple, "Stuffs must allow multiple selection.");
            Check(Control<MenuItem>(editor, "DuplicateObjectMenuItem") is not null, "Stuffs context menu must offer Duplicate.");

            var first = scene.AddEmpty();
            first.Rename("First");
            var second = scene.AddEmpty();
            second.Rename("Second");
            var third = scene.AddEmpty();
            third.Rename("Third");
            typeof(MainWindow).GetMethod("SyncHierarchyForTest", flags)!.Invoke(editor, []);
            Dispatcher.UIThread.RunJobs();
            typeof(MainWindow).GetMethod("SelectSceneObjectsForTest", flags)!.Invoke(editor, [(object)new SceneObject?[] { first, second }]);
            Dispatcher.UIThread.RunJobs();
            var selected = (IReadOnlyList<SceneObject>)typeof(MainWindow).GetMethod("GetSelectedSceneObjects", flags)!.Invoke(editor, [])!;
            Check(selected.Count == 2 && ReferenceEquals(selected[0], first) && ReferenceEquals(selected[1], second),
                "Multi-selection must keep every selected object in display order.");
            Check(ReferenceEquals(typeof(MainWindow).GetMethod("GetSelectedSceneObject", flags)!.Invoke(editor, []), first),
                "The Inspector primary must be the first selected object.");
            typeof(MainWindow).GetMethod("SyncHierarchyForTest", flags)!.Invoke(editor, []);
            Dispatcher.UIThread.RunJobs();
            selected = (IReadOnlyList<SceneObject>)typeof(MainWindow).GetMethod("GetSelectedSceneObjects", flags)!.Invoke(editor, [])!;
            Check(selected.Count == 2, "Rebuilding the tree must preserve a multi-selection.");
            Check(Control<MenuItem>(editor, "DeleteObjectMenuItem").IsEnabled, "Delete must stay enabled for a multi-selection.");
            Check(Control<MenuItem>(editor, "DuplicateObjectMenuItem").IsEnabled, "Duplicate must stay enabled for a multi-selection.");

            var copies = (System.Collections.IList)typeof(MainWindow).GetMethod("DuplicateSelectedForTest", flags)!.Invoke(editor, [])!;
            Dispatcher.UIThread.RunJobs();
            Check(copies.Count == 2 && scene.Objects.Count == 5, "Duplicating two selections must create two copies.");
            Check(scene.RootObjects[0] == first && scene.RootObjects[1].Name == "First" && scene.RootObjects[2] == second,
                "Each duplicate must sit immediately after its source.");
            Check(!ReferenceEquals(scene.RootObjects[1], first) && scene.RootObjects[1].Id != first.Id,
                "Duplicates must use fresh IDs instead of reusing the source identity.");
            selected = (IReadOnlyList<SceneObject>)typeof(MainWindow).GetMethod("GetSelectedSceneObjects", flags)!.Invoke(editor, [])!;
            Check(selected.Count == 2 && selected.All(item => item.Name is "First" or "Second") && !selected.Contains(first) && !selected.Contains(second),
                "Duplication must select the new copies.");
            Check(store.IsDirty, "Duplication must mark the scene dirty.");
            store.MarkClean();

            typeof(MainWindow).GetMethod("DeleteSelectedObject", flags)!.Invoke(editor, []);
            Dispatcher.UIThread.RunJobs();
            Check(scene.Objects.Count == 3, "Deleting the selected copies must remove only the copies.");
            selected = (IReadOnlyList<SceneObject>)typeof(MainWindow).GetMethod("GetSelectedSceneObjects", flags)!.Invoke(editor, [])!;
            Check(selected.Count == 1 && ReferenceEquals(selected[0], second), "Delete must move the selection to a surviving sibling.");
            Check(store.IsDirty, "Delete must mark the scene dirty.");
            store.MarkClean();

            var parent = scene.AddEmpty();
            parent.Rename("Parent");
            var child = scene.AddEmpty();
            child.Rename("Child");
            child.SetParent(parent);
            typeof(MainWindow).GetMethod("SyncHierarchyForTest", flags)!.Invoke(editor, []);
            typeof(MainWindow).GetMethod("SelectSceneObjectsForTest", flags)!.Invoke(editor, [(object)new SceneObject?[] { parent, child }]);
            copies = (System.Collections.IList)typeof(MainWindow).GetMethod("DuplicateSelectedForTest", flags)!.Invoke(editor, [])!;
            Check(copies.Count == 1, "Duplicating a parent together with its child must copy the subtree once.");
            var copyParent = scene.Objects.Single(item => item.Name == "Parent" && !ReferenceEquals(item, parent));
            Check(copyParent.Children.Count == 1 && copyParent.Children[0].Name == "Child",
                "The duplicated subtree must keep its child.");
            typeof(MainWindow).GetMethod("SelectSceneObjectsForTest", flags)!.Invoke(editor, [(object)new SceneObject?[] { copyParent, copyParent.Children[0] }]);
            typeof(MainWindow).GetMethod("DeleteSelectedObject", flags)!.Invoke(editor, []);
            Check(!scene.Objects.Contains(copyParent) && scene.Objects.Contains(parent) && scene.Objects.Contains(child),
                "Deleting a parent together with its child must remove the subtree once and keep the original.");

            typeof(MainWindow).GetMethod("SelectSceneObjectForTest", flags)!.Invoke(editor, [third]);
            tree.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.D, KeyModifiers = KeyModifiers.Control });
            Dispatcher.UIThread.RunJobs();
            Check(scene.Objects.Count(item => item.Name == "Third") == 2, "Ctrl+D on the Stuffs pane must duplicate the selection.");
            var thirdCopy = scene.Objects.Last(item => item.Name == "Third");
            typeof(MainWindow).GetMethod("SelectSceneObjectForTest", flags)!.Invoke(editor, [thirdCopy]);
            tree.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Delete });
            Dispatcher.UIThread.RunJobs();
            Check(!scene.Objects.Contains(thirdCopy) && scene.Objects.Contains(third), "Delete on the Stuffs pane must remove the selection.");
        }
        finally
        {
            CloseEditor(editor);
        }
        Console.WriteLine("PASS: Stuffs multi-selection, Ctrl+D duplication, and Delete-key removal.");
    }
}
