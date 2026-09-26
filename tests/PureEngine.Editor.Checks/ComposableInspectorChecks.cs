using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PureEngine.Core;
using PureEngine.Editor;
using Button = Avalonia.Controls.Button;

static class ComposableInspectorChecks
{
    public static void Run(MainWindow editor)
    {
        var store = (EditSceneStore)typeof(MainWindow).GetProperty("EditSceneStore", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(editor)!;
        var components = (ProjectComponents)typeof(MainWindow).GetProperty("Components", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(editor)!;
        components.Registry.Register<Probe>("checks.composable-probe");
        var item = store.Current.AddEmpty();
        var target = store.Current.AddEmpty();
        target.Rename("ComposableTarget");
        var probe = new Probe
        {
            References = [new() { ["first"] = new() { Target = target } }, new() { ["second"] = new() { Target = target } }],
        };
        item.Attach(probe);
        typeof(MainWindow).GetMethod("SyncHierarchyForTest", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!.Invoke(editor, []);
        typeof(MainWindow).GetMethod("SelectSceneObjectForTest", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!.Invoke(editor, [item]);
        Dispatcher.UIThread.RunJobs();

        Box(editor, "Probe.Value.Inner.Number").Text = "17";
        Pump();
        Check(probe.Value.Inner.Number == 17, "Struct property writes must reach every parent.");
        Box(editor, "Probe.Value.Inner.Text").Text = "preserved";
        Pump();
        Check(probe.Value.Inner.Number == 17 && probe.Value.Inner.Text == "preserved", "Sibling writes must read fresh boxed parents.");
        Click(editor, "Probe.Optional.Create");
        Box(editor, "Probe.Optional.Inner.Number").Text = "23";
        Pump();
        Check(probe.Optional?.Inner.Number == 23, "Nullable struct edits must write back.");
        Click(editor, "Probe.Optional.Null");
        Check(probe.Optional is null, "Nullable structs must clear to null.");

        Click(editor, "Probe.Nested[0].Collapse");
        Click(editor, "Probe.Nested[0].Value[0].Collapse");
        Box(editor, "Probe.Nested[0].Value[0][0].Inner.Number").Text = "31";
        Pump();
        Check(probe.Nested[0]["key"][0].Inner.Number == 31, "List/dictionary/array/struct edits must reach the root.");
        CheckKeyTyping(editor, probe);
        Check(probe.Nested[0]["renamed"][0].Inner.Number == 31, "Dictionary renames must preserve nested values.");

        Box(editor, "Probe.Matrix[1,1].Inner.Number").Text = "41";
        Pump();
        Check(probe.Matrix[1, 1].Inner.Number == 41, "Multidimensional struct cells must write back.");
        Box(editor, "Probe.Matrix.Dimension[0]").Text = "3";
        Click(editor, "Probe.Matrix.Resize");
        Check(probe.Matrix.GetLength(0) == 3 && probe.Matrix[1, 1].Inner.Number == 41, "Resize must preserve coordinates.");
        Box(editor, "Probe.Matrix.Dimension[1]").Text = "0";
        Click(editor, "Probe.Matrix.Resize");
        Check(probe.Matrix.GetLength(0) == 3 && probe.Matrix.GetLength(1) == 0, "Resize must retain empty dimensions.");
        Box(editor, "Probe.Matrix.Dimension[0]").Text = "-1";
        Click(editor, "Probe.Matrix.Resize");
        Check(probe.Matrix.GetLength(0) == 3, "Invalid shape must preserve the old array.");
        Box(editor, "Probe.Matrix.Dimension[0]").Text = "2";
        Box(editor, "Probe.Matrix.Dimension[1]").Text = "2";
        Click(editor, "Probe.Matrix.Resize");
        Check(probe.Matrix.Length == 4, "Empty shapes must be resizable back to editable cells.");

        Check(!editor.GetVisualDescendants().OfType<TextBox>().Any(box => Equals(box.GetValue(AutomationProperties.NameProperty), "Probe.Large[32]")),
            "Only one bounded collection page should be materialized.");
        Click(editor, "Probe.Large.Next");
        Box(editor, "Probe.Large[32]").Text = "99";
        Pump();
        Check(probe.Large[32] == 99, "Later pages must edit the correct element.");
        Check(editor.Title!.StartsWith("* ", StringComparison.Ordinal), "Deep value edits must dirty the owning scene.");
        CheckNestedMissing(editor, store, item, probe, target);
        Console.WriteLine("PASS: deep struct writeback, nullable structs, nested containers, shaped array resizing, invalid shape retention, and paged editing.");
    }

    private static void CheckKeyTyping(MainWindow editor, Probe probe)
    {
        var box = Box(editor, "Probe.Nested[0].Key[0]");
        box.Focus();
        box.SelectAll();
        foreach (var character in "renamed")
        {
            editor.KeyTextInput(character.ToString());
            Pump();
            Check(box.IsFocused && editor.GetVisualDescendants().Contains(box), "Typing a key must retain the active editor and focus.");
            Check(probe.Nested[0].ContainsKey("key"), "Typing must not prematurely rename the key.");
        }
        Check(box.Text == "renamed" && editor.ViewModel.Inspector.HasInputErrors, "Pending rename must retain all typed text and guard saving.");
        PressKey(box, Key.Escape);
        Check(box.Text == "key" && !editor.ViewModel.Inspector.HasInputErrors, "Escape must discard the pending key.");
        box.Text = "other";
        PressKey(box, Key.Enter);
        Check(probe.Nested[0].ContainsKey("key") && editor.ViewModel.Inspector.HasInputErrors, "Duplicate key must not commit.");
        box.Text = "";
        PressKey(box, Key.Enter);
        Check(probe.Nested[0].ContainsKey("key") && editor.ViewModel.Inspector.HasInputErrors, "Empty key must not commit.");
        box.Text = "renamed";
        PressKey(box, Key.Enter);
        Check(!editor.ViewModel.Inspector.HasInputErrors, "Committed rename must clear pending input errors.");
        box.Text = "stale";
        PressKey(box, Key.Enter);
        Check(!probe.Nested[0].ContainsKey("stale") && !editor.ViewModel.Inspector.HasInputErrors, "Detached key events must not mutate data or validation.");
    }

    private static void PressKey(TextBox box, Key key)
    {
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
        Pump();
    }

    private static void CheckNestedMissing(MainWindow editor, EditSceneStore store, SceneObject item, Probe probe, SceneObject target)
    {
        Click(editor, "Probe.References[1].Collapse");
        Click(editor, "Probe.References[1].Value[0].Target.Clear");
        Check(probe.References[1]["second"].Target is null, "Reference Clear inside a struct must write back.");
        var combo = editor.GetVisualDescendants().OfType<ComboBox>()
            .Single(control => Equals(control.GetValue(AutomationProperties.NameProperty), "Probe.References[1].Value[0].Target"));
        combo.SelectedItem = combo.Items.Cast<object>().Single(option => option.ToString() == "ComposableTarget");
        Pump();
        Check(ReferenceEquals(probe.References[1]["second"].Target, target), "Reference selection inside a struct must write back.");
        store.Current.Remove(target);
        typeof(MainWindow).GetMethod("SelectSceneObjectForTest", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!.Invoke(editor, [item]);
        Pump();
        Check(probe.References[1]["second"].Target is null, "Deleted nested struct references must become null.");
        var id = item.GetComponentId(probe);
        Click(editor, "Probe.References.Remove[0]");
        Check(store.Current.References.TryGetMissing(id, "References[0][second].Target", out var missing) && missing == target.Id,
            "Outer row deletion must move descendant Missing IDs.");
        Click(editor, "Probe.References[0].Collapse");
        var key = Box(editor, "Probe.References[0].Key[0]");
        key.Focus();
        key.Text = "renamed";
        Box(editor, "Probe.Value.Inner.Number").Focus();
        Pump();
        Check(store.Current.References.TryGetMissing(id, "References[0][renamed].Target", out missing) && missing == target.Id,
            "Outer key rename must move descendant Missing IDs.");
        Click(editor, "Probe.References[0].Value[0].Target.Clear");
        Check(!store.Current.References.TryGetMissing(id, "References[0][renamed].Target", out _), "Explicit Clear must remove descendant Missing metadata.");
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static TextBox Box(MainWindow editor, string name) => editor.GetVisualDescendants().OfType<TextBox>()
        .Single(box => Equals(box.GetValue(AutomationProperties.NameProperty), name));

    private static void Click(MainWindow editor, string name)
    {
        editor.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.GetValue(AutomationProperties.NameProperty), name))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
    }

    public struct InnerValue
    {
        [Inspector] public int Number { get; set; }
        [Inspector] public string? Text { get; set; }
    }

    public struct OuterValue
    {
        [Inspector] public InnerValue Inner { get; set; }
    }

    public struct ReferenceValue
    {
        [Inspector] public SceneObject? Target { get; set; }
    }

    public sealed class Probe
    {
        [Inspector] public OuterValue Value { get; set; }
        [Inspector] public OuterValue? Optional { get; set; }
        [Inspector] public List<Dictionary<string, OuterValue[]>> Nested { get; set; } = [new() { ["key"] = [new()], ["other"] = [new()] }];
        [Inspector] public OuterValue[,] Matrix { get; set; } = new OuterValue[2, 2];
        [Inspector] public int[] Large { get; set; } = new int[70];
        [Inspector] public List<Dictionary<string, ReferenceValue>> References { get; set; } = [];
    }
}
