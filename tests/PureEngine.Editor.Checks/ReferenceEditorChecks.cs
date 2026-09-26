using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PureEngine.Core;
using PureEngine.Editor;
using Button = Avalonia.Controls.Button;

static class ReferenceEditorChecks
{
    public static void Run(MainWindow editor)
    {
        static void Check(bool condition, string message)
        {
            if (!condition)
                throw new Exception(message);
        }
        static ComboBox Combo(MainWindow window, string automationName) => window.GetVisualDescendants().OfType<ComboBox>()
            .Single(combo => Equals(combo.GetValue(AutomationProperties.NameProperty) as string, automationName));
        static Button ButtonByName(MainWindow window, string automationName) => window.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.GetValue(AutomationProperties.NameProperty) as string, automationName));
        static void Click(Button button)
        {
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        }
        static void Select(MainWindow window, SceneObject item) =>
            typeof(MainWindow).GetMethod("SelectSceneObjectForTest",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
                .Invoke(window, [item]);
        var editStore = (EditSceneStore)typeof(MainWindow).GetProperty("EditSceneStore", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(editor)!;
        var components = (ProjectComponents)typeof(MainWindow).GetProperty("Components", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(editor)!;
        if (!components.Registry.Ids.Contains("checks.test-holder"))
            components.Registry.Register<TestRefHolder>("checks.test-holder");
        var scene = editStore.Current;

        var holderObject = scene.AddEmpty();
        holderObject.Rename("RefHolder");
        var holder = new TestRefHolder();
        holderObject.Attach(holder);
        var targetObject = scene.AddEmpty();
        targetObject.Rename("RefTarget");
        var targetButton = new PureEngine.Core.Button();
        targetObject.Attach(targetButton);
        typeof(MainWindow).GetMethod("SyncHierarchyForTest", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .Invoke(editor, []);
        Dispatcher.UIThread.RunJobs();
        Select(editor, holderObject);
        Dispatcher.UIThread.RunJobs();

        var combo = Combo(editor, $"{nameof(TestRefHolder)}.Target");
        Check(combo.SelectedItem?.ToString() == "None", "Reference must start as None.");
        var options = ((System.Collections.IEnumerable)combo.ItemsSource!).Cast<object>().ToList();
        Check(options.Count >= 2, "Reference must list scene candidates.");
        var targetOption = options.FirstOrDefault(option => option.ToString()!.Contains("RefTarget", StringComparison.Ordinal));
        Check(targetOption is not null, "Reference candidates must include the scene Button.");
        combo.SelectedItem = targetOption;
        Dispatcher.UIThread.RunJobs();
        Check(ReferenceEquals(holder.Target, targetButton), "ComboBox selection must connect the live Button.");
        Check(editor.Title!.StartsWith("* "), "Reference edit must mark dirty.");
        var info = editor.GetVisualDescendants().OfType<TextBlock>()
            .Single(block => Equals(block.GetValue(AutomationProperties.NameProperty) as string, $"{nameof(TestRefHolder)}.Target.Info"));
        var targetButtonId = targetObject.GetComponentId(targetButton);
        Check(!info.IsVisible, "Assigned references must stay single-line; details live in the tooltip.");
        Check(!combo.SelectedItem!.ToString()!.Contains(targetButtonId.ToString("D"), StringComparison.Ordinal),
            "Reference options must hide IDs; the tooltip carries them.");
        var tip = ToolTip.GetTip(combo) as string;
        Check(tip is not null && tip.Contains("RefTarget", StringComparison.Ordinal) && tip.Contains(targetButtonId.ToString("D"), StringComparison.Ordinal),
            $"Reference tooltip must show the target name and ID, got '{tip}'.");
        var clearButton = ButtonByName(editor, $"{nameof(TestRefHolder)}.Target.Clear");
        var clearContent = clearButton.Content as PathIcon;
        Check(clearContent is not null && ReferenceEquals(clearContent.Data, Application.Current?.FindResource("Icon.DismissCircle")),
            "Reference clear must be an inline remove button.");
        Check(clearButton.Classes.Contains("dismissButton"), "Reference clear must use the round dismiss button style.");
        Check(clearButton.Width == clearButton.Height && clearButton.Width > 0
            && clearButton.CornerRadius.TopLeft == clearButton.Width / 2 && clearButton.CornerRadius.TopRight == clearButton.Width / 2
            && clearButton.CornerRadius.BottomLeft == clearButton.Width / 2 && clearButton.CornerRadius.BottomRight == clearButton.Width / 2,
            "Dismiss buttons must be circular, not square buttons with a circle glyph.");

        Click(ButtonByName(editor, $"{nameof(TestRefHolder)}.Target.Clear"));
        Check(holder.Target is null, "Reference Clear must null the member.");
        Check(!scene.References.TryGetMissing(holderObject.GetComponentId(holder), "Target", out _), "Clear must drop Missing IDs.");
        editStore.MarkClean();
        Click(ButtonByName(editor, $"{nameof(TestRefHolder)}.Target.Clear"));
        Check(!editStore.IsDirty, "Clearing None must not mark the scene dirty.");

        holder.Target = new PureEngine.Core.Button();
        Select(editor, targetObject);
        Select(editor, holderObject);
        Dispatcher.UIThread.RunJobs();
        Check(Combo(editor, $"{nameof(TestRefHolder)}.Target").SelectedItem?.ToString()!.StartsWith("Detached:", StringComparison.Ordinal) == true,
            "Detached Component must remain a reference slot so it can be reassigned.");
        Click(ButtonByName(editor, $"{nameof(TestRefHolder)}.Target.Clear"));

        var freshOptions = ((System.Collections.IEnumerable)Combo(editor, $"{nameof(TestRefHolder)}.Target").ItemsSource!).Cast<object>().ToList();
        var freshTarget = freshOptions.FirstOrDefault(option => option.ToString()!.Contains("RefTarget", StringComparison.Ordinal));
        Check(freshTarget is not null, "Reference candidates must persist after Clear.");
        Combo(editor, $"{nameof(TestRefHolder)}.Target").SelectedItem = freshTarget;
        Dispatcher.UIThread.RunJobs();
        Check(ReferenceEquals(holder.Target, targetButton), "Reassign must reconnect.");
        var holderComponentId = holderObject.GetComponentId(holder);

        var resolve = typeof(MainWindow).GetMethod("TryResolveDraggedReference", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] resolveArgs = [targetObject.Id, typeof(PureEngine.Core.Button), null, null];
        var valid = (bool)resolve.Invoke(editor, resolveArgs)!;
        Check(valid && ReferenceEquals(resolveArgs[2], targetButton), "Drag resolution must find the dragged component.");
        object?[] invalidArgs = [Guid.NewGuid(), typeof(PureEngine.Core.Button), null, null];
        Check(!(bool)resolve.Invoke(editor, invalidArgs)!, "Unknown drag IDs must be rejected.");
        var holderIdArgs = new object?[] { holderObject.Id, typeof(SceneObject), null, null };
        Check((bool)resolve.Invoke(editor, holderIdArgs)! && ReferenceEquals(holderIdArgs[2], holderObject), "SceneObject drag must resolve.");
        object?[] wrongTypeArgs = [holderObject.Id, typeof(PureEngine.Core.Button), null, null];
        Check(!(bool)resolve.Invoke(editor, wrongTypeArgs)!, "Drag without the expected component must be rejected.");
        var refParent = scene.AddEmpty();
        refParent.Rename("RefParent");
        var refParentTransform = new Transform();
        refParent.Attach(refParentTransform);
        var refChild = scene.AddEmpty();
        refChild.Rename("RefChild");
        var refChildTransform = new Transform();
        refChild.Attach(refChildTransform);
        refChild.SetParent(refParent);
        object?[] ownArgs = [refParent.Id, typeof(Transform), null, null];
        Check((bool)resolve.Invoke(editor, ownArgs)! && ReferenceEquals(ownArgs[2], refParentTransform),
            "Drag of a parent must resolve its own component instead of failing on descendants.");
        var bareParent = scene.AddEmpty();
        bareParent.Rename("RefBareParent");
        var nestedChild = scene.AddEmpty();
        nestedChild.Rename("RefNestedChild");
        var nestedButton = new PureEngine.Core.Button();
        nestedChild.Attach(nestedButton);
        nestedChild.SetParent(bareParent);
        object?[] nestedArgs = [bareParent.Id, typeof(PureEngine.Core.Button), null, null];
        Check((bool)resolve.Invoke(editor, nestedArgs)! && ReferenceEquals(nestedArgs[2], nestedButton),
            "Drag of an object without the component must resolve a unique descendant.");
        scene.Remove(refParent);
        scene.Remove(bareParent);

        Click(ButtonByName(editor, $"{nameof(TestRefHolder)}.Target.Clear"));
        using var dragData = new DataTransfer();
        var dragFormat = (DataFormat<string>)typeof(MainWindow).GetField("SceneObjectIdFormat", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        dragData.Add(DataTransferItem.Create(dragFormat, targetObject.Id.ToString("D")));
        var dropTarget = Combo(editor, $"{nameof(TestRefHolder)}.Target");
        var dropRow = dropTarget.GetVisualAncestors().OfType<Grid>().First();
        var dragOver = new DragEventArgs(DragDrop.DragOverEvent, dragData, dropRow, default, KeyModifiers.None);
        dropRow.RaiseEvent(dragOver);
        Check(dragOver.Handled && dragOver.DragEffects == DragDropEffects.Copy, "Reference DragOver must accept the Stuffs payload on the field row.");
        dropRow.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, dragData, dropRow, default, KeyModifiers.None));
        Dispatcher.UIThread.RunJobs();
        Check(ReferenceEquals(holder.Target, targetButton), "Routed reference Drop must assign the live Button.");

        holder.Config = new ReferenceConfig { Buttons = [targetButton, targetButton], Map = new() { ["button"] = targetButton } };
        holder.Buttons = [targetButton, targetButton];

        scene.Remove(targetObject);
        Dispatcher.UIThread.RunJobs();
        typeof(MainWindow).GetMethod("SyncHierarchyForTest", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .Invoke(editor, []);
        Select(editor, holderObject);
        Dispatcher.UIThread.RunJobs();
        static bool HasButton(MainWindow window, string automationName) => window.GetVisualDescendants().OfType<Button>()
            .Any(button => Equals(button.GetValue(AutomationProperties.NameProperty) as string, automationName));
        Check(HasButton(editor, $"{nameof(TestRefHolder)}.Config.Buttons.Add"), "Reference lists must keep Add.");
        Check(HasButton(editor, $"{nameof(TestRefHolder)}.Config.Buttons.Null"), "Reference lists must keep a separate Set Null.");
        Check(HasButton(editor, $"{nameof(TestRefHolder)}.Config.Buttons.Clear"), "Reference lists must offer bulk clear.");
        Check(HasButton(editor, $"{nameof(TestRefHolder)}.Buttons.Clear"), "Reference arrays must offer bulk clear.");
        Check(holder.Target is null, "Target deletion must null the reference.");
        Check(scene.References.TryGetMissing(holderComponentId, "Target", out var missing) && missing == targetButtonId,
            "Deletion must keep the Missing ID.");
        var missingCombo = Combo(editor, $"{nameof(TestRefHolder)}.Target");
        Check(missingCombo.SelectedItem?.ToString()!.StartsWith("Missing:", StringComparison.Ordinal) == true,
            "Missing must display as Missing, not None.");
        Check(editor.Title!.StartsWith("* "), "Deletion must mark dirty.");

        var nestedRemove = ButtonByName(editor, $"{nameof(TestRefHolder)}.Config.Buttons.Remove[0]");
        Check(nestedRemove.Classes.Contains("dismissButton") && nestedRemove.Content is PathIcon nestedRemoveIcon
            && ReferenceEquals(nestedRemoveIcon.Data, Application.Current?.FindResource("Icon.DismissCircle")),
            "Collection row remove must use the round dismiss button.");
        Check(nestedRemove.Width == nestedRemove.Height && nestedRemove.Width > 0
            && nestedRemove.CornerRadius.TopLeft == nestedRemove.Width / 2,
            "Collection row remove must be circular.");
        Click(nestedRemove);
        Check(holder.Config.Buttons.Count == 1
            && scene.References.TryGetMissing(holderComponentId, "Config.Buttons[0]", out var nestedMissing) && nestedMissing == targetButtonId
            && !scene.References.TryGetMissing(holderComponentId, "Config.Buttons[1]", out _), "Removing a nested list row must move its retained ID.");
        var keyBox = editor.GetVisualDescendants().OfType<TextBox>().Single(box =>
            Equals(box.GetValue(AutomationProperties.NameProperty), $"{nameof(TestRefHolder)}.Config.Map.Key[0]"));
        keyBox.Text = "renamed";
        keyBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Dispatcher.UIThread.RunJobs();
        Check(scene.References.TryGetMissing(holderComponentId, "Config.Map[renamed]", out var renamedMissing) && renamedMissing == targetButtonId,
            "Renaming a nested dictionary key must move its retained ID.");
        Click(ButtonByName(editor, $"{nameof(TestRefHolder)}.Buttons.Remove[0]"));
        Check(holder.Buttons.Length == 1 && scene.References.TryGetMissing(holderComponentId, "Buttons[0]", out _)
            && !scene.References.TryGetMissing(holderComponentId, "Buttons[1]", out _), "Removing an array row must move its retained ID.");

        foreach (var item in scene.Objects.ToArray())
        {
            if (!ReferenceEquals(item, holderObject))
                scene.Remove(item);
        }
        typeof(MainWindow).GetMethod("SyncHierarchyForTest", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .Invoke(editor, []);
        Select(editor, holderObject);
        Dispatcher.UIThread.RunJobs();

        typeof(MainWindow).GetMethod("StartPlay", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(editor, []);
        Dispatcher.UIThread.RunJobs();
        var playing = (bool)typeof(MainWindow).GetProperty("IsPlaying")!.GetValue(editor)!;
        if (!playing)
        {
            var status = editor.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(block => block.Name == "FileStatus")?.Text ?? "<no status>";
            throw new Exception($"Play must start with references. Status: {status}");
        }
        var duringPlay = holder.Target;
        combo.SelectedItem = combo.ItemsSource!.Cast<object>().First();
        Dispatcher.UIThread.RunJobs();
        Check(ReferenceEquals(holder.Target, duringPlay), "Play must reject reference edits.");
        Check((bool)typeof(MainWindow).GetMethod("StopPlay", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(editor, [])!, "Stop must succeed.");
        Dispatcher.UIThread.RunJobs();

        Console.WriteLine("PASS: reference select/clear, Stuffs row drag resolution, field-row drops, Missing display, dirty, and Play guard.");
    }

    public sealed class TestRefHolder
    {
        [Inspector] public PureEngine.Core.Button? Target { get; set; }
        [Inspector] public SceneObject? Owner { get; set; }
        [Inspector] public ReferenceConfig? Config { get; set; }
        [Inspector] public PureEngine.Core.Button?[] Buttons { get; set; } = [];
    }

    public sealed class ReferenceConfig
    {
        [Inspector] public List<PureEngine.Core.Button?> Buttons { get; set; } = [];
        [Inspector] public Dictionary<string, PureEngine.Core.Button?> Map { get; set; } = [];
    }
}
