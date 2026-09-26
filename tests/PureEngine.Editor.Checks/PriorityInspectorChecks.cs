using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PureEngine.Core;
using PureEngine.Editor;
using Button = Avalonia.Controls.Button;

static class PriorityInspectorChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static T Control<T>(Window window, string name) where T : Control => window.FindControl<T>(name)!;

    private static List<TextBox> PriorityBoxes(MainWindow editor, string typeName) => [.. editor.GetVisualDescendants().OfType<TextBox>()
            .Where(box => (box.GetValue(AutomationProperties.NameProperty) as string)?.StartsWith(typeName + ".", StringComparison.Ordinal) == true
                && (box.GetValue(AutomationProperties.NameProperty) as string)?.EndsWith("Priority", StringComparison.Ordinal) == true)];

    private static TextBox FindPriorityBox(MainWindow editor, string automationName) => editor.GetVisualDescendants().OfType<TextBox>()
        .Single(box => Equals(box.GetValue(AutomationProperties.NameProperty) as string, automationName));

    private static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>S/U/D letter inside its badge chip: the field panel holds [badge Border, box].</summary>
    private static TextBlock? PriorityLabel(TextBox box) => (box.Parent as StackPanel)?.Children.OfType<Border>().SingleOrDefault()?.Child as TextBlock;

    private static readonly string[] PriorityTooltips = ["Start Priority", "Update Priority", "Destroy Priority"];
    private static readonly string[] PriorityLabels = ["S", "U", "D"];
    private static readonly string[] PriorityColors = ["#8AB4F8", "#81C995", "#F28B82"];
    private static readonly string[] PriorityBadgeBackgrounds = ["#2A3A57", "#24402E", "#472D2D"];

    public static void Run(MainWindow editor)
    {
        var sceneObjects = Control<TreeView>(editor, "SceneObjects");
        _ = sceneObjects;
        static void Select(MainWindow window, SceneObject item) =>
            typeof(MainWindow).GetMethod("SelectSceneObjectForTest",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!
                .Invoke(window, [item]);
        Check(sceneObjects.Items.Count == 0, "Inspector checks require an empty scene.");

        // Attach a full-lifecycle component and a data-only component to separate objects.
        var editStore = (EditSceneStore)typeof(MainWindow).GetProperty("EditSceneStore", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(editor)!;
        var scene = editStore.Current;

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
        typeof(MainWindow).GetMethod("SyncHierarchyForTest",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!
            .Invoke(editor, []);

        Dispatcher.UIThread.RunJobs();

        // Full shows three compact fields beside the component name, in lifecycle order.
        Select(editor, fullObject);
        Dispatcher.UIThread.RunJobs();
        var fullBoxes = PriorityBoxes(editor, nameof(InspectorFullProbe));
        Check(fullBoxes.Count == 3, $"Full must show 3 priorities, got {fullBoxes.Count}.");
        Check(fullBoxes.Select(box => ToolTip.GetTip(box) as string)
            .SequenceEqual(PriorityTooltips),
            "Priority tooltips must identify each lifecycle in order.");
        Check(fullBoxes.Select(box => PriorityLabel(box)?.Text)
            .SequenceEqual(PriorityLabels),
            "Priority fields must show S/U/D labels in lifecycle order.");
        Check(fullBoxes.Select(box => (PriorityLabel(box)?.Foreground as Avalonia.Media.SolidColorBrush)?.Color.ToString())
            .SequenceEqual(PriorityColors.Select(hex => Avalonia.Media.Color.Parse(hex).ToString())),
            "Priority S/U/D labels must use lifecycle accent colors.");
        Check(fullBoxes.Select(PriorityLabel)
            .All(label => label?.FontWeight == Avalonia.Media.FontWeight.SemiBold),
            "Priority S/U/D labels must be semibold for scannability.");
        Check(fullBoxes.Select(box => ((PriorityLabel(box)?.Parent as Border)?.Background as Avalonia.Media.SolidColorBrush)?.Color.ToString())
            .SequenceEqual(PriorityBadgeBackgrounds.Select(hex => Avalonia.Media.Color.Parse(hex).ToString())),
            "Priority badges must use per-lifecycle fills so S/U/D read apart.");
        Check(fullBoxes.All(box => box.Width == 24 && box.FontSize == 10
            && box.TextAlignment == Avalonia.Media.TextAlignment.Center),
            "Priority boxes must stay compact and centered.");
        var fields = fullBoxes.Select(box => box.Parent as StackPanel).ToList();
        Check(fields.All(field => field is not null
            && field.Orientation == Avalonia.Layout.Orientation.Horizontal
            && field.VerticalAlignment == Avalonia.Layout.VerticalAlignment.Center
            && field.Children.Count == 2),
            "Each priority must pair its S/U/D label with its box.");
        var priorityPanel = fields[0]?.Parent as StackPanel;
        Check(priorityPanel?.Orientation == Avalonia.Layout.Orientation.Horizontal
            && priorityPanel.HorizontalAlignment == Avalonia.Layout.HorizontalAlignment.Right
            && fields.All(field => field!.Parent == priorityPanel)
            && priorityPanel.Parent is Grid header
            && Grid.GetColumn(priorityPanel) == 1
            && header.Children.OfType<TextBlock>().Single() is TextBlock title
            && title.Text == nameof(InspectorFullProbe)
            && title.HorizontalAlignment == Avalonia.Layout.HorizontalAlignment.Left,
            "Compact priority fields must share the component title header and dock right.");

        // Partial shows only Start.
        Select(editor, partialObject);
        Dispatcher.UIThread.RunJobs();
        var partialBoxes = PriorityBoxes(editor, nameof(InspectorStartOnlyProbe));
        Check(partialBoxes.Count == 1, $"Start-only must show 1 priority, got {partialBoxes.Count}.");
        Check(FindPriorityBox(editor, $"{nameof(InspectorStartOnlyProbe)}.StartPriority").Text == "0",
            "Initial priority must be 0.");

        // Data-only shows none.
        Select(editor, dataObject);
        Dispatcher.UIThread.RunJobs();
        Check(PriorityBoxes(editor, nameof(InspectorDataOnly)).Count == 0, "Data-only must show no priorities.");

        // Edit a valid value including negatives; unsaved state appears.
        Select(editor, fullObject);
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
        var invalidCount = editor.ViewModel.Inspector.InvalidCount;
        Check(invalidCount == 1, "Invalid priority must join the shared save guard.");
        var fileStatus = Control<TextBlock>(editor, "FileStatus");
        var saveMethod = typeof(MainWindow).GetMethod("SaveSceneAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var saveTask = (Task<bool>)saveMethod.Invoke(editor, [false])!;
        saveTask.GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
        Check(fileStatus.Text?.Contains("Cannot save") == true, "Save must be refused during priority input errors.");

        // Esc restores the last valid value and clears the error.
        startBox.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Escape,
        });
        Dispatcher.UIThread.RunJobs();
        Check(startBox.Text == "-12", $"Esc must restore last valid priority, got '{startBox.Text}'.");
        invalidCount = editor.ViewModel.Inspector.InvalidCount;
        Check(!errorBadge.IsVisible && invalidCount == 0, "Esc must clear the priority error.");

        // Update/Destroy stay independent.
        var updateBox = FindPriorityBox(editor, $"{nameof(InspectorFullProbe)}.UpdatePriority");
        var destroyBox = FindPriorityBox(editor, $"{nameof(InspectorFullProbe)}.DestroyPriority");
        updateBox.Text = "7";
        destroyBox.Text = "-3";
        Dispatcher.UIThread.RunJobs();
        Check(fullObject.GetUpdatePriority(full) == 7 && fullObject.GetDestroyPriority(full) == -3
            && fullObject.GetStartPriority(full) == -12, "Priorities must stay independent.");

        // Collapse hides member editors but keeps the header; state survives reselection.
        Select(editor, dataObject);
        Dispatcher.UIThread.RunJobs();
        static TextBox MemberBox(MainWindow window, string automationName) => window.GetVisualDescendants().OfType<TextBox>()
            .Single(box => Equals(box.GetValue(AutomationProperties.NameProperty) as string, automationName));
        static Button CollapseToggle(MainWindow window, string automationName) => window.GetVisualDescendants().OfType<Button>()
            .Single(toggle => Equals(toggle.GetValue(AutomationProperties.NameProperty) as string, automationName));
        Check(MemberBox(editor, $"{nameof(InspectorDataOnly)}.Value").IsEffectivelyVisible, "Member editor must start visible.");
        var dataCollapse = CollapseToggle(editor, $"{nameof(InspectorDataOnly)}.Collapse");
        Check((dataCollapse.Foreground as Avalonia.Media.SolidColorBrush)?.Color.ToString()
            == Avalonia.Media.Color.Parse("#8B7CF6").ToString(), "Collapse toggle must carry accent color.");
        Click(dataCollapse);
        Dispatcher.UIThread.RunJobs();
        Check(((dataCollapse.Background as Avalonia.Media.SolidColorBrush)?.Color.ToString())
            == Avalonia.Media.Color.Parse("#2E2A4A").ToString(), "Collapsed toggle must show its wash fill.");
        Check(!MemberBox(editor, $"{nameof(InspectorDataOnly)}.Value").IsEffectivelyVisible, "Collapsed card must hide member editors.");
        Select(editor, fullObject);
        Dispatcher.UIThread.RunJobs();
        Select(editor, dataObject);
        Dispatcher.UIThread.RunJobs();
        Check(!MemberBox(editor, $"{nameof(InspectorDataOnly)}.Value").IsEffectivelyVisible, "Collapse state must survive reselection.");
        Click(CollapseToggle(editor, $"{nameof(InspectorDataOnly)}.Collapse"));
        Dispatcher.UIThread.RunJobs();
        Check(MemberBox(editor, $"{nameof(InspectorDataOnly)}.Value").IsEffectivelyVisible, "Expanded card must show member editors again.");

        Console.WriteLine("PASS: priority Inspector display, edit, validation, Esc revert, dirty, and save guard.");
    }

    public sealed class InspectorFullProbe
    {
#pragma warning disable CA1822 // Reflection tests require these lifecycle/Inspector members to remain instance members.
        [Start] private void Begin() { }
        [Update] private void Tick() { }
        [Destroy] private void End() { }
    }

    public sealed class InspectorStartOnlyProbe
    {
        [Start] private void Begin() { }
#pragma warning restore CA1822
    }

    public sealed class InspectorDataOnly
    {
        [Inspector] public int Value = 0;
    }
}
