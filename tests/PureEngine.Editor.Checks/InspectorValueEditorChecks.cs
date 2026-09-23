using System.Numerics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PureEngine.Core;
using PureEngine.Editor;

static class InspectorValueEditorChecks
{
    public static void Run(MainWindow editor)
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
        static T Control<T>(Window window, string name) where T : Control => window.FindControl<T>(name)!;
        static TextBox Box(MainWindow window, string automationName) => window.GetVisualDescendants().OfType<TextBox>()
            .Single(box => Equals(box.GetValue(AutomationProperties.NameProperty) as string, automationName));
        static Button ButtonByName(MainWindow window, string automationName) => window.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.GetValue(AutomationProperties.NameProperty) as string, automationName));
        static ComboBox Combo(MainWindow window, string automationName) => window.GetVisualDescendants().OfType<ComboBox>()
            .Single(combo => Equals(combo.GetValue(AutomationProperties.NameProperty) as string, automationName));
        static CheckBox Flag(MainWindow window, string automationName) => window.GetVisualDescendants().OfType<CheckBox>()
            .Single(check => Equals(check.GetValue(AutomationProperties.NameProperty) as string, automationName));
        static TextBlock? AxisBadge(TextBox box)
        {
            if (box.Parent is not Panel parent) return null;
            var index = parent.Children.IndexOf(box);
            return parent.Children.Take(index).OfType<Border>().LastOrDefault()?.Child as TextBlock;
        }
        static void Click(Button button)
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        }

        var sceneObjects = Control<TreeView>(editor, "SceneObjects");
        _ = sceneObjects;
        static void Select(MainWindow window, SceneObject item) =>
            typeof(MainWindow).GetMethod("SelectSceneObjectForTest",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!
                .Invoke(window, [item]);
        var editStore = (EditSceneStore)typeof(MainWindow).GetField("_editScene", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(editor)!;
        var scene = editStore.Current;

        var valueObject = scene.AddEmpty();
        valueObject.Rename("Values");
        var probe = new InspectorValueProbe();
        valueObject.Attach(probe);
        var moverObject = scene.AddEmpty();
        moverObject.Rename("Mover");
        var mover = new Transform { LocalPosition = new Vector3(1, 2, 3) };
        moverObject.Attach(mover);
        typeof(MainWindow).GetMethod("SyncHierarchyForTest",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!
            .Invoke(editor, []);
        Dispatcher.UIThread.RunJobs();

        Select(editor, valueObject);
        Dispatcher.UIThread.RunJobs();

        // New types must show editors, not Unsupported badges.
        var badges = editor.GetVisualDescendants().OfType<TextBlock>()
            .Where(block => (block.Text ?? "").StartsWith("Unsupported:", StringComparison.Ordinal)).ToList();
        Check(badges.Count == 0, $"Extended members must not show Unsupported badges, got {badges.Count}.");
        Check(probe.PartialAccess == Permissions.Read && probe.WideAccess == WidePermissions.High,
            "Rendering Flags must preserve partial composites and unsigned high bits.");

        // Vector3 editing reaches the scene and marks dirty.
        var positionX = Box(editor, $"{nameof(InspectorValueProbe)}.Position.X");
        Check(positionX.Text == "1", $"Initial Position.X must be 1, got '{positionX.Text}'.");
        Check(AxisBadge(positionX)?.Text == "X", "Vector axis value must carry its axis badge.");
        positionX.Text = "10";
        Dispatcher.UIThread.RunJobs();
        Check(probe.Position.X == 10f, "Vector edit did not reach the scene.");
        Check(editor.Title!.StartsWith("* "), "Vector edit must mark the scene dirty.");

        // Invalid vector input shows an error and blocks saving.
        var errorBadge = Control<TextBlock>(editor, "ComponentsError");
        Check(!errorBadge.IsVisible, "Valid edit must not show an error badge.");
        var positionY = Box(editor, $"{nameof(InspectorValueProbe)}.Position.Y");
        positionY.Text = "abc";
        Dispatcher.UIThread.RunJobs();
        Check(errorBadge.IsVisible, "Invalid vector input must show an error badge.");
        Check(probe.Position.Y == 2f, "Invalid input must not change the scene.");

        // Esc restores the last valid value and clears the error.
        positionY.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Dispatcher.UIThread.RunJobs();
        Check(positionY.Text == "2", $"Esc must restore last valid vector component, got '{positionY.Text}'.");
        Check(!errorBadge.IsVisible, "Esc must clear the vector error.");

        // Double editing works with invariant formatting.
        var ratio = Box(editor, $"{nameof(InspectorValueProbe)}.Ratio");
        ratio.Text = "2.5";
        Dispatcher.UIThread.RunJobs();
        Check(probe.Ratio == 2.5, "Double edit did not reach the scene.");

        // List Add/Remove updates the scene.
        var addScores = ButtonByName(editor, $"{nameof(InspectorValueProbe)}.Scores.Add");
        Click(addScores);
        Check(probe.Scores.Count == 3, $"List Add must grow the scene list, got {probe.Scores.Count}.");
        var added = Box(editor, $"{nameof(InspectorValueProbe)}.Scores[2]");
        added.Text = "99";
        Dispatcher.UIThread.RunJobs();
        Check(probe.Scores[2] == 99, "List element edit did not reach the scene.");
        Click(ButtonByName(editor, $"{nameof(InspectorValueProbe)}.Scores.Remove[2]"));
        Check(probe.Scores.Count == 2, "List Remove must shrink the scene list.");

        // Dictionary Add/Value edit works.
        Click(ButtonByName(editor, $"{nameof(InspectorValueProbe)}.Counts.Add"));
        Dispatcher.UIThread.RunJobs();
        Check(probe.Counts.Count == 2, "Dictionary Add must grow the scene dictionary.");
        var freshKey = probe.Counts.Keys.Single(key => key != "alice");
        var valueIndex = probe.Counts.Keys.OrderBy(key => key, StringComparer.Ordinal).ToList().IndexOf(freshKey);
        var valueBox = Box(editor, $"{nameof(InspectorValueProbe)}.Counts.Value[{valueIndex}]");
        valueBox.Text = "7";
        Dispatcher.UIThread.RunJobs();
        Check(probe.Counts[freshKey] == 7, "Dictionary value edit did not reach the scene.");

        // Sequence and dictionary element lists collapse; headers stay visible.
        static Point Position(Visual visual, Visual relativeTo) =>
            visual.TranslatePoint(new Point(0, 0), relativeTo) ?? new Point(-1, -1);
        Check(Box(editor, $"{nameof(InspectorValueProbe)}.Scores[0]").IsEffectivelyVisible, "Sequence elements must start visible.");
        var scoresToggle = ButtonByName(editor, $"{nameof(InspectorValueProbe)}.Scores.Collapse");
        var scoresAdd = ButtonByName(editor, $"{nameof(InspectorValueProbe)}.Scores.Add");
        var toggleWidth = scoresToggle.Bounds.Width;
        var addPosition = Position(scoresAdd, editor);
        Check(toggleWidth > 0 && addPosition.X > 0, "Header layout must be measurable.");
        Click(scoresToggle);
        Dispatcher.UIThread.RunJobs();
        Check(scoresToggle.Bounds.Width == toggleWidth, "Collapse toggle size must not change between states.");
        Check(Position(scoresAdd, editor) == addPosition, "Header buttons must not shift when collapsing.");
        Check(!Box(editor, $"{nameof(InspectorValueProbe)}.Scores[0]").IsEffectivelyVisible, "Collapsed sequence must hide elements.");
        Check(ButtonByName(editor, $"{nameof(InspectorValueProbe)}.Scores.Add").IsEffectivelyVisible, "Collapsed sequence must keep its header.");
        Click(scoresToggle);
        Dispatcher.UIThread.RunJobs();
        Check(Box(editor, $"{nameof(InspectorValueProbe)}.Scores[0]").IsEffectivelyVisible, "Expanded sequence must show elements again.");
        var countsToggle = ButtonByName(editor, $"{nameof(InspectorValueProbe)}.Counts.Collapse");
        Click(countsToggle);
        Dispatcher.UIThread.RunJobs();
        Check(!valueBox.IsEffectivelyVisible, "Collapsed dictionary must hide entries.");
        Click(countsToggle);
        Dispatcher.UIThread.RunJobs();
        Check(valueBox.IsEffectivelyVisible, "Expanded dictionary must show entries again.");

        // Transform member null -> Create -> edit -> Set Null.
        var createTarget = ButtonByName(editor, $"{nameof(InspectorValueProbe)}.Target.Create");
        Click(createTarget);
        Check(probe.Target is not null, "Transform Create must assign a new instance.");
        var targetX = Box(editor, $"{nameof(InspectorValueProbe)}.Target.LocalPosition.X");
        targetX.Text = "5";
        Dispatcher.UIThread.RunJobs();
        Check(probe.Target!.LocalPosition.X == 5f, "Transform nested edit did not reach the scene.");
        var targetXBadge = AxisBadge(targetX);
        Check(targetXBadge?.Text == "X", "Transform axis value must carry its axis badge.");
        var targetW = Box(editor, $"{nameof(InspectorValueProbe)}.Target.LocalRotation.W");
        var targetWBadge = AxisBadge(targetW);
        Check(targetWBadge?.Text == "W", "Quaternion rotation must show X/Y/Z/W badges.");
        var rotationGrid = targetW.Parent as Grid;
        Check(rotationGrid is not null && targetW.Bounds.Right <= rotationGrid.Bounds.Width + 1,
            "Quaternion row must fit without clipping the W box.");
        Click(ButtonByName(editor, $"{nameof(InspectorValueProbe)}.Target.Null"));
        Check(probe.Target is null, "Transform Set Null must clear the member.");

        // Transform component itself shows Local editors.
        Select(editor, moverObject);
        Dispatcher.UIThread.RunJobs();
        var moverX = Box(editor, $"{nameof(Transform)}.LocalPosition.X");
        moverX.Text = "9";
        Dispatcher.UIThread.RunJobs();
        Check(mover.LocalPosition.X == 9f, "Transform component edit did not reach the scene.");

        // Enum ComboBox selection reaches the scene.
        Select(editor, valueObject);
        Dispatcher.UIThread.RunJobs();
        var level = Combo(editor, $"{nameof(InspectorValueProbe)}.Level");
        level.SelectedItem = Difficulty.Hard;
        Dispatcher.UIThread.RunJobs();
        Check(probe.Level == Difficulty.Hard, "Enum selection did not reach the scene.");
        Check(editor.Title!.StartsWith("* "), "Enum edit must mark the scene dirty.");

        // Flags CheckBoxes combine into the scene value.
        var writeFlag = Flag(editor, $"{nameof(InspectorValueProbe)}.Access.Write");
        Check(writeFlag.IsChecked == true, "Flags initial state must reflect Read|Write.");
        writeFlag.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Check(probe.Access == Permissions.Read, $"Flags uncheck did not reach the scene, got {probe.Access}.");
        writeFlag.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Check(probe.Access == (Permissions.Read | Permissions.Write), "Flags recheck did not reach the scene.");
        Click(ButtonByName(editor, $"{nameof(InspectorValueProbe)}.Access.Clear"));
        Check(probe.Access == Permissions.None, "Flags None must clear all bits.");
        Flag(editor, $"{nameof(InspectorValueProbe)}.Access.ReadWrite").IsChecked = true;
        Check(probe.Access == Permissions.ReadWrite && writeFlag.IsChecked == true,
            "Composite selection must update individual checks without additional writes.");

        Flag(editor, $"{nameof(InspectorValueProbe)}.WideAccess.Low").IsChecked = true;
        Check(probe.WideAccess == (WidePermissions.High | WidePermissions.Low), "Setting a low bit must preserve the ulong high bit.");
        Flag(editor, $"{nameof(InspectorValueProbe)}.WideAccess.High").IsChecked = false;
        Check(probe.WideAccess == WidePermissions.Low, "Clearing the ulong high bit must preserve other bits.");
        Flag(editor, $"{nameof(InspectorValueProbe)}.SignedAccess.All").IsChecked = true;
        Check(probe.SignedAccess == SignedPermissions.All, "Signed negative flags must remain representable.");
        Flag(editor, $"{nameof(InspectorValueProbe)}.SignedAccess.Read").IsChecked = false;
        Check(probe.SignedAccess == (SignedPermissions)(-2), "Clearing a signed flag must preserve other bits.");

        Flag(editor, $"{nameof(InspectorValueProbe)}.MaybeAccess.Write").IsChecked = false;
        Flag(editor, $"{nameof(InspectorValueProbe)}.AccessList[0].Write").IsChecked = false;
        Flag(editor, $"{nameof(InspectorValueProbe)}.AccessMap.Value[0].Write").IsChecked = false;
        Check(probe.MaybeAccess == Permissions.Read && probe.AccessList[0] == Permissions.Read
            && probe.AccessMap["key"] == Permissions.Read, "Nullable and collection Flags must preserve remaining bits.");

        // Nullable enum Create/Set Null round-trips.
        Click(ButtonByName(editor, $"{nameof(InspectorValueProbe)}.MaybeLevel.Create"));
        Check(probe.MaybeLevel is not null, "Nullable enum Create must assign a value.");
        Click(ButtonByName(editor, $"{nameof(InspectorValueProbe)}.MaybeLevel.Null"));
        Check(probe.MaybeLevel is null, "Nullable enum Set Null must clear the member.");

        Console.WriteLine("PASS: extended Inspector display, vector/double/enum/list/dictionary/Transform edit, validation, Esc revert, and dirty.");
    }

    public enum Difficulty
    {
        Easy,
        Normal,
        Hard,
    }

    [Flags]
    public enum Permissions
    {
        None = 0,
        Read = 1,
        Write = 2,
        ReadWrite = Read | Write,
        Execute = 4,
    }

    [Flags]
    public enum WidePermissions : ulong
    {
        None = 0,
        Low = 1,
        High = 1UL << 63,
    }

    [Flags]
    public enum SignedPermissions : sbyte
    {
        None = 0,
        Read = 1,
        All = -1,
    }

    public sealed class InspectorValueProbe
    {
        [Inspector] public Vector3 Position = new(1, 2, 3);
        [Inspector] public Quaternion Rotation = Quaternion.Identity;
        [Inspector] public double Ratio = 1.5;
        [Inspector] public List<int> Scores { get; set; } = [1, 2];
        [Inspector] public Dictionary<string, int> Counts { get; set; } = new() { ["alice"] = 3 };
        [Inspector] public Transform? Target { get; set; }
        [Inspector] public Difficulty Level = Difficulty.Normal;
        [Inspector] public Permissions Access = Permissions.Read | Permissions.Write;
        [Inspector] public Permissions PartialAccess = Permissions.Read;
        [Inspector] public WidePermissions WideAccess = WidePermissions.High;
        [Inspector] public SignedPermissions SignedAccess = SignedPermissions.Read;
        [Inspector] public Permissions? MaybeAccess = Permissions.ReadWrite;
        [Inspector] public List<Permissions> AccessList = [Permissions.ReadWrite];
        [Inspector] public Dictionary<string, Permissions> AccessMap = new() { ["key"] = Permissions.ReadWrite };
        [Inspector] public Difficulty? MaybeLevel { get; set; }
    }
}
