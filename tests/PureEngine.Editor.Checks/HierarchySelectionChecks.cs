using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using ChevronPath = Avalonia.Controls.Shapes.Path;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PureEngine.Core;
using PureEngine.Editor;
using Button = Avalonia.Controls.Button;

/// <summary>Guards Stuffs selection during drag gestures. A press must not move the Inspector until the click completes.</summary>
internal static class HierarchySelectionChecks
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
            var store = (EditSceneStore)typeof(MainWindow)
                .GetProperty("EditSceneStore", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(editor)!;
            var scene = store.Current;
            var target = scene.AddEmpty();
            target.Rename("InspectorTarget");
            var source = scene.AddEmpty();
            source.Rename("DragSource");
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var components = (ProjectComponents)typeof(MainWindow).GetProperty("Components", flags)!.GetValue(editor)!;
            components.Registry.Register<ReferenceEditorChecks.TestRefHolder>("checks.drag-holder");
            var holder = new ReferenceEditorChecks.TestRefHolder();
            target.Attach(holder);
            var button = new PureEngine.Core.Button();
            source.Attach(button);
            typeof(MainWindow).GetMethod("SyncHierarchyForTest", flags)!.Invoke(editor, []);
            typeof(MainWindow).GetMethod("SelectSceneObjectForTest", flags)!.Invoke(editor, [target]);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

            var tree = Control<TreeView>(editor, "SceneObjects");
            var sourceRow = editor.GetVisualDescendants().OfType<TreeViewItem>()
                .Single(item => ReferenceEquals(((HierarchyNode)item.DataContext!).Ref, source));
            var center = new Point(sourceRow.Bounds.Width / 2, sourceRow.Bounds.Height / 2);
            var point = sourceRow.TranslatePoint(center, editor)!.Value;

            editor.MouseDown(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            var pressedSelection = ((HierarchyNode)tree.SelectedItem!).Ref;
            Check(ReferenceEquals(pressedSelection, target),
                "Pressing a Stuffs row must not move the selection until the click completes, so the Inspector stays on the drop target.");

            editor.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            var releasedSelection = ((HierarchyNode)tree.SelectedItem!).Ref;
            Check(ReferenceEquals(releasedSelection, source),
                "Releasing without a drag must complete the click and select the pressed row.");

            typeof(MainWindow).GetMethod("SelectSceneObjectForTest", flags)!.Invoke(editor, [target]);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            var reference = editor.GetVisualDescendants().OfType<ComboBox>().Single(combo =>
                Equals(combo.GetValue(Avalonia.Automation.AutomationProperties.NameProperty), "TestRefHolder.Target"));
            var dropPoint = reference.TranslatePoint(new Point(reference.Bounds.Width / 2, reference.Bounds.Height / 2), editor)!.Value;
            editor.MouseDown(point, MouseButton.Left);
            editor.MouseMove(point + new Vector(8, 0), RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
            Check(ReferenceEquals(((HierarchyNode)tree.SelectedItem!).Ref, target),
                "Starting a drag must preserve selection and the Inspector target.");
            editor.MouseMove(dropPoint, RawInputModifiers.LeftMouseButton);
            editor.MouseUp(dropPoint, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Check(ReferenceEquals(holder.Target, button), "A real Stuffs drag must assign the Inspector reference.");
            Check(ReferenceEquals(((HierarchyNode)tree.SelectedItem!).Ref, target),
                "Reference drop must not select the dragged source.");

            var chevronChild = scene.AddEmpty();
            chevronChild.Rename("ChevronChild");
            chevronChild.SetParent(target);
            typeof(MainWindow).GetMethod("SyncHierarchyForTest", flags)!.Invoke(editor, []);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            var parentRow = editor.GetVisualDescendants().OfType<TreeViewItem>()
                .Single(item => ReferenceEquals(((HierarchyNode)item.DataContext!).Ref, target));
            var expander = parentRow.GetVisualDescendants().OfType<ToggleButton>()
                .Single(button => button.Name == "PART_ExpandCollapseChevron");
            static ChevronPath? ExpanderGlyph(ToggleButton toggle) =>
                toggle.GetVisualDescendants().OfType<ChevronPath>().SingleOrDefault();
            ChevronPath? WaitForExpanderGlyph()
            {
                ChevronPath? glyph = null;
                for (var attempt = 0; glyph is null && attempt < 5; attempt++)
                {
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                    Dispatcher.UIThread.RunJobs();
                    glyph = ExpanderGlyph(expander);
                }
                return glyph;
            }
            var collapsedGlyph = WaitForExpanderGlyph();
            Check(collapsedGlyph is not null && ReferenceEquals(collapsedGlyph.Data, Application.Current?.FindResource("Icon.ChevronRight")),
                "Collapsed Stuffs rows must show the shared ChevronRight icon.");
            ((HierarchyNode)parentRow.DataContext!).IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            Check(ReferenceEquals(ExpanderGlyph(expander)?.Data, Application.Current?.FindResource("Icon.ChevronDown")),
                "Expanded Stuffs rows must show the shared ChevronDown icon.");
        }
        finally
        {
            CloseEditor(editor);
        }
        Console.WriteLine("PASS: Stuffs press preserves selection, release selects, a real drag assigns the Inspector reference, and expanders use the shared chevrons.");
    }
}
