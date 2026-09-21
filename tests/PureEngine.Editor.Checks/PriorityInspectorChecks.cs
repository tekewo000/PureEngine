using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PureEngine.Core;
using PureEngine.Core.Attributes;
using PureEngine.Editor;

static class PriorityInspectorChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static T Control<T>(Window window, string name) where T : Control => window.FindControl<T>(name)!;

    private static List<TextBox> PriorityBoxes(MainWindow editor, string typeName)
    {
        return editor.GetVisualDescendants().OfType<TextBox>()
            .Where(box => (box.GetValue(AutomationProperties.NameProperty) as string)?.StartsWith(typeName + ".", StringComparison.Ordinal) == true
                && (box.GetValue(AutomationProperties.NameProperty) as string)?.EndsWith("Priority", StringComparison.Ordinal) == true)
            .ToList();
    }

    private static TextBox FindPriorityBox(MainWindow editor, string automationName)
    {
        return editor.GetVisualDescendants().OfType<TextBox>()
            .Single(box => Equals(box.GetValue(AutomationProperties.NameProperty) as string, automationName));
    }

    public static void Run(MainWindow editor)
    {
        var sceneObjects = Control<ListBox>(editor, "SceneObjects");
        Check(sceneObjects.Items.Count == 0, "Inspector checks require an empty scene.");

        // Attach a full-lifecycle component and a data-only component to separate objects.
        var editorSceneField = typeof(MainWindow).GetField("_scene", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var scene = (Scene)editorSceneField.GetValue(editor)!;

        var fullObject = scene.AddEmpty();
        fullObject.Rename("Full");
        var full = new InspectorFullProbe();
        fullObject.Attach(full);
        var partialObject = scene.AddEmpty();
        partialObject.Rename("Partial");
        var partial = new InspectorStartOnlyProbe();
        partialObject.Attach(partial);
        var dataObject = scene.AddEmpty();
        dataObject.Rename("Data");
        var data = new InspectorDataOnly();
        dataObject.Attach(data);

        Dispatcher.UIThread.RunJobs();

        // Full shows three compact fields beside the component name, in lifecycle order.
        sceneObjects.SelectedItem = fullObject;
        Dispatcher.UIThread.RunJobs();
        var fullBoxes = PriorityBoxes(editor, nameof(InspectorFullProbe));
        Check(fullBoxes.Count == 3, $"Full must show 3 priorities, got {fullBoxes.Count}.");
        Check(fullBoxes.Select(box => ToolTip.GetTip(box) as string)
            .SequenceEqual(new[] { "Start Priority", "Update Priority", "Destroy Priority" }),
            "Priority tooltips must identify each lifecycle in order.");
        var priorityPanel = fullBoxes[0].Parent as StackPanel;
        Check(priorityPanel?.Orientation == Avalonia.Layout.Orientation.Horizontal
            && fullBoxes.All(box => box.Parent == priorityPanel && box.Width == 36 && box.FontSize == 10)
            && priorityPanel.Parent is Grid header
            && header.Children.OfType<TextBlock>().Single().Text == nameof(InspectorFullProbe),
            "Compact priority fields must share the component title header.");

        // Partial shows only Start.
        sceneObjects.SelectedItem = partialObject;
        Dispatcher.UIThread.RunJobs();
        var partialBoxes = PriorityBoxes(editor, nameof(InspectorStartOnlyProbe));
        Check(partialBoxes.Count == 1, $"Start-only must show 1 priority, got {partialBoxes.Count}.");
        Check(FindPriorityBox(editor, $"{nameof(InspectorStartOnlyProbe)}.StartPriority").Text == "0",
            "Initial priority must be 0.");

        // Data-only shows none.
        sceneObjects.SelectedItem = dataObject;
        Dispatcher.UIThread.RunJobs();
        Check(PriorityBoxes(editor, nameof(InspectorDataOnly)).Count == 0, "Data-only must show no priorities.");

        // Edit a valid value including negatives; unsaved state appears.
        sceneObjects.SelectedItem = fullObject;
        Dispatcher.UIThread.RunJobs();
        var startBox = FindPriorityBox(editor, $"{nameof(InspectorFullProbe)}.StartPriority");
        startBox.Text = "-12";
        Dispatcher.UIThread.RunJobs();
        Check(fullObject.GetStartPriority(full) == -12, "Valid priority edit did not reach the scene.");
        Check(editor.Title!.StartsWith("* "), "Priority edit must mark the scene dirty.");
        var errorBadge = Control<TextBlock>(editor, "ComponentsError");
        Check(!errorBadge.IsVisible, "Valid edit must not show an error badge.");

        // Invalid input shows an error and blocks saving.
        startBox.Text = "abc";
        Dispatcher.UIThread.RunJobs();
        Check(errorBadge.IsVisible, "Invalid priority must show an error badge.");
        Check(fullObject.GetStartPriority(full) == -12, "Invalid input must not change the scene.");
        var invalidFieldsValue = typeof(MainWindow)
            .GetField("_invalidFields", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(editor)!;
        var invalidCount = (int)invalidFieldsValue.GetType().GetProperty("Count")!.GetValue(invalidFieldsValue)!;
        Check(invalidCount == 1, "Invalid priority must join the shared save guard.");
        var fileStatus = Control<TextBlock>(editor, "FileStatus");
        var saveMethod = typeof(MainWindow).GetMethod("SaveSceneAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var saveTask = (Task<bool>)saveMethod.Invoke(editor, [false])!;
        saveTask.GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
        Check(fileStatus.Text?.Contains("保存できません") == true, "Save must be refused during priority input errors.");

        // Esc restores the last valid value and clears the error.
        startBox.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Escape,
        });
        Dispatcher.UIThread.RunJobs();
        Check(startBox.Text == "-12", $"Esc must restore last valid priority, got '{startBox.Text}'.");
        invalidCount = (int)invalidFieldsValue.GetType().GetProperty("Count")!.GetValue(invalidFieldsValue)!;
        Check(!errorBadge.IsVisible && invalidCount == 0, "Esc must clear the priority error.");

        // Update/Destroy stay independent.
        var updateBox = FindPriorityBox(editor, $"{nameof(InspectorFullProbe)}.UpdatePriority");
        var destroyBox = FindPriorityBox(editor, $"{nameof(InspectorFullProbe)}.DestroyPriority");
        updateBox.Text = "7";
        destroyBox.Text = "-3";
        Dispatcher.UIThread.RunJobs();
        Check(fullObject.GetUpdatePriority(full) == 7 && fullObject.GetDestroyPriority(full) == -3
            && fullObject.GetStartPriority(full) == -12, "Priorities must stay independent.");

        Console.WriteLine("PASS: priority Inspector display, edit, validation, Esc revert, dirty, and save guard.");
    }

    public sealed class InspectorFullProbe
    {
        [Start] private void Begin() { }
        [Update] private void Tick() { }
        [Destroy] private void End() { }
    }

    public sealed class InspectorStartOnlyProbe
    {
        [Start] private void Begin() { }
    }

    public sealed class InspectorDataOnly
    {
        [Inspector] public int Value = 0;
    }
}
