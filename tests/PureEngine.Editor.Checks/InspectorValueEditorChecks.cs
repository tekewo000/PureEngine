using System.Numerics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PureEngine.Core;
using PureEngine.Editor;
using Button = Avalonia.Controls.Button;

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
        static ColorView PickerFor(MainWindow window, string previewName)
        {
            var swatch = window.GetVisualDescendants().OfType<Border>()
                .Single(border => Equals(border.GetValue(AutomationProperties.NameProperty) as string, previewName));
            if (FlyoutBase.GetAttachedFlyout(swatch) is not Flyout attached || attached.Content is not ColorView picker)
                throw new Exception($"Color picker must be attached to {previewName}.");
            return picker;
        }

        var sceneObjects = Control<TreeView>(editor, "SceneObjects");
        _ = sceneObjects;
        static void Select(MainWindow window, SceneObject item) =>
            typeof(MainWindow).GetMethod("SelectSceneObjectForTest",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!
                .Invoke(window, [item]);
        var editStore = (EditSceneStore)typeof(MainWindow).GetProperty("EditSceneStore", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(editor)!;
        var scene = editStore.Current;

        var valueObject = scene.AddEmpty();
        valueObject.Rename("Values");
        var probe = new InspectorValueProbe();
        valueObject.Attach(probe);
        var nestedObject = scene.AddEmpty();
        nestedObject.Rename("Nested");
        var nested = new InspectorNestedProbe();
        nestedObject.Attach(nested);
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

        // Member colors edit through the Spectrum/Palette/Sliders picker; the preview is the entry point.
        var preview = editor.GetVisualDescendants().OfType<Border>().Single(border =>
            Equals(border.GetValue(AutomationProperties.NameProperty), $"{nameof(InspectorValueProbe)}.Tint.Preview"));
        Check((ToolTip.GetTip(preview) as string)?.Contains("Spectrum") == true,
            "Color preview must advertise the Spectrum/Palette/Sliders picker.");
        var picker = PickerFor(editor, $"{nameof(InspectorValueProbe)}.Tint.Preview");
        Check(Equals(picker.GetValue(AutomationProperties.NameProperty), $"{nameof(InspectorValueProbe)}.Tint.Picker"), "Color picker must carry its automation name.");
        Check(picker.IsColorSpectrumVisible && picker.IsColorPaletteVisible && picker.IsColorComponentsVisible,
            "Color picker must show Spectrum, Palette, and Sliders.");
        Check(picker.IsHexInputVisible && picker.IsAlphaEnabled,
            "Color picker must offer hex input and alpha.");
        picker.Color = Avalonia.Media.Color.FromArgb(255, 64, 128, 64);
        Dispatcher.UIThread.RunJobs();
        Check(MathF.Abs(probe.Tint.R - 64f / 255f) < 1e-6f && MathF.Abs(probe.Tint.G - 128f / 255f) < 1e-6f
            && MathF.Abs(probe.Tint.B - 64f / 255f) < 1e-6f && MathF.Abs(probe.Tint.A - 1f) < 1e-6f,
            $"Picker selection did not reach the scene, got {probe.Tint}.");
        Check((preview.Background as Avalonia.Media.SolidColorBrush)?.Color == Avalonia.Media.Color.FromArgb(255, 64, 128, 64),
            "Color preview must reflect the picked color.");
        // Pane tab styling must not leak into the picker tabs: icon headers with the 48px strip.
        var inspectorPane = Control<TabControl>(editor, "InspectorPane");
        var styleProbe = new ColorView
        {
            IsAlphaEnabled = true,
            IsAlphaVisible = true,
            IsColorSpectrumVisible = true,
            IsColorPaletteVisible = true,
            IsColorComponentsVisible = true,
            IsHexInputVisible = true,
        };
        var styleProbeTab = new TabItem { Header = "StyleProbe", Content = styleProbe };
        var previousPaneIndex = inspectorPane.SelectedIndex;
        inspectorPane.Items.Add(styleProbeTab);
        inspectorPane.SelectedItem = styleProbeTab;
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
        try
        {
            var innerTabs = styleProbe.GetVisualDescendants().OfType<TabItem>().ToList();
            Check(innerTabs.Count == 3, $"Color picker must keep its three tabs, got {innerTabs.Count}.");
            Check(innerTabs.All(tab => !tab.GetVisualDescendants().OfType<Control>().Any(inner => inner.Name is "TabFill" or "TabOutline")),
                "Pane tab styling must not replace the picker tab headers.");
            Check(innerTabs.All(tab => tab.GetVisualDescendants().OfType<Border>().Any(border => border.Name == "PART_LayoutRoot")),
                "Picker tabs must keep the ColorView tab theme.");
            Check(!styleProbe.GetVisualDescendants().OfType<TextBlock>().Any(block => (block.Text ?? "").Contains("Avalonia", StringComparison.Ordinal)),
                "Picker tab headers must show icons, not type names.");
        }
        finally
        {
            inspectorPane.Items.Remove(styleProbeTab);
            inspectorPane.SelectedIndex = previousPaneIndex;
            Dispatcher.UIThread.RunJobs();
        }
        Click(ButtonByName(editor, $"{nameof(InspectorValueProbe)}.MaybeTint.Null"));
        Dispatcher.UIThread.RunJobs();
        Check(probe.MaybeTint is null, "Nullable Color Set Null must clear the member.");
        Click(ButtonByName(editor, $"{nameof(InspectorValueProbe)}.MaybeTint.Create"));
        Dispatcher.UIThread.RunJobs();
        Check(probe.MaybeTint == Color.White, "Nullable Color Create must assign white.");
        var maybePicker = PickerFor(editor, $"{nameof(InspectorValueProbe)}.MaybeTint.Preview");
        maybePicker.Color = Avalonia.Media.Color.FromArgb(255, 32, 64, 96);
        Dispatcher.UIThread.RunJobs();
        Check(probe.MaybeTint == new Color(32f / 255f, 64f / 255f, 96f / 255f, 1f),
            $"Nullable picker selection did not reach the scene, got {probe.MaybeTint}.");
        var swatchR = Box(editor, $"{nameof(InspectorValueProbe)}.Swatches[0].R");
        swatchR.Text = "0";
        Dispatcher.UIThread.RunJobs();
        Check(probe.Swatches[0].R == 0f, "Color list element edit did not reach the scene.");
        var swatchesAdd = ButtonByName(editor, $"{nameof(InspectorValueProbe)}.Swatches.Add");
        Check(swatchesAdd.Content is PathIcon swatchesAddIcon && ReferenceEquals(swatchesAddIcon.Data, Application.Current?.FindResource("Icon.AddSquare")),
            "Collection Add must use the shared AddSquare icon.");
        Check(swatchesAdd.Classes.Contains("dismissButton"), "Header actions must be borderless icon buttons.");
        var swatchesToggle = ButtonByName(editor, $"{nameof(InspectorValueProbe)}.Swatches.Collapse");
        Check(swatchesToggle.Content is PathIcon swatchesToggleIcon && ReferenceEquals(swatchesToggleIcon.Data, Application.Current?.FindResource("Icon.TriangleDown")),
            "Expanded collections must show the TriangleDown toggle.");
        Click(swatchesAdd);
        Check(probe.Swatches[1] == Color.White, "New color elements must start white.");
        var swatchesClear = ButtonByName(editor, $"{nameof(InspectorValueProbe)}.Swatches.Clear");
        Check(swatchesClear.Content is PathIcon swatchesClearIcon && ReferenceEquals(swatchesClearIcon.Data, Application.Current?.FindResource("Icon.Delete")),
            "Bulk Clear must use the shared Delete icon.");
        Check(swatchesClear.Classes.Contains("dismissButton"), "Header actions must be borderless icon buttons.");
        var swatchesNull = ButtonByName(editor, $"{nameof(InspectorValueProbe)}.Swatches.Null");
        Check(swatchesNull.Content is PathIcon swatchesNullIcon && ReferenceEquals(swatchesNullIcon.Data, Application.Current?.FindResource("Icon.Dismiss")),
            "Set Null must use the shared Dismiss icon.");
        Check(swatchesNull.Classes.Contains("dismissButton"), "Header actions must be borderless icon buttons.");
        var swatchesCreate = ButtonByName(editor, $"{nameof(InspectorValueProbe)}.Swatches.Create");
        Check(swatchesCreate.Content is PathIcon swatchesCreateIcon && ReferenceEquals(swatchesCreateIcon.Data, Application.Current?.FindResource("Icon.Compose")),
            "List Create must use the shared Compose icon.");
        Check(swatchesCreate.Classes.Contains("dismissButton"), "Header actions must be borderless icon buttons.");
        var countsCreate = ButtonByName(editor, $"{nameof(InspectorValueProbe)}.Counts.Create");
        Check(countsCreate.Content is PathIcon countsCreateIcon && ReferenceEquals(countsCreateIcon.Data, Application.Current?.FindResource("Icon.Compose")),
            "Dictionary Create must use the shared Compose icon.");
        var maybeTintCreate = ButtonByName(editor, $"{nameof(InspectorValueProbe)}.MaybeTint.Create");
        Check(maybeTintCreate.Content is PathIcon maybeTintCreateIcon && ReferenceEquals(maybeTintCreateIcon.Data, Application.Current?.FindResource("Icon.Compose")),
            "Nullable Create must use the shared Compose icon.");
        Click(swatchesClear);
        Check(probe.Swatches.Count == 0, "List Clear must remove all rows and keep the list.");
        Box(editor, $"{nameof(InspectorValueProbe)}.Palette.Value[0].A").Text = "0.25";
        Box(editor, $"{nameof(InspectorValueProbe)}.Colors[0].B").Text = "0.5";
        Dispatcher.UIThread.RunJobs();
        Check(probe.Palette["accent"].A == 0.25f && probe.Colors[0].B == 0.5f,
            "Dictionary and array colors must be editable.");

        // Double editing works with invariant formatting.
        var ratio = Box(editor, $"{nameof(InspectorValueProbe)}.Ratio");
        ratio.Text = "2.5";
        Dispatcher.UIThread.RunJobs();
        Check(probe.Ratio == 2.5, "Double edit did not reach the scene.");

        // List Add/Remove updates the scene.
        var addScores = ButtonByName(editor, $"{nameof(InspectorValueProbe)}.Scores.Add");
        var scoresNull = ButtonByName(editor, $"{nameof(InspectorValueProbe)}.Scores.Null");
        Check(scoresNull.Content is PathIcon scoresNullIcon && ReferenceEquals(scoresNullIcon.Data, Application.Current?.FindResource("Icon.Dismiss")),
            "Set Null must use the shared Dismiss icon.");
        Check(scoresNull.Classes.Contains("dismissButton"), "Header actions must be borderless icon buttons.");
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
        var countsClear = ButtonByName(editor, $"{nameof(InspectorValueProbe)}.Counts.Clear");
        Check(countsClear.Content is PathIcon countsClearIcon && ReferenceEquals(countsClearIcon.Data, Application.Current?.FindResource("Icon.Delete")),
            "Bulk Clear must use the shared Delete icon.");
        Click(countsClear);
        Check(probe.Counts.Count == 0, "Dictionary Clear must remove all entries and keep the dictionary.");

        // Nested lists fix the index and remove columns at each header and stay editable.
        Click(ButtonByName(editor, $"{nameof(InspectorValueProbe)}.Matrix[0].Collapse"));
        Dispatcher.UIThread.RunJobs();
        var matrixCell = Box(editor, $"{nameof(InspectorValueProbe)}.Matrix[0][0]");
        Check(matrixCell.Text == "1", $"Nested list cell must start at 1, got '{matrixCell.Text}'.");
        var outerRemove = ButtonByName(editor, $"{nameof(InspectorValueProbe)}.Matrix.Remove[0]");
        var innerRemove = ButtonByName(editor, $"{nameof(InspectorValueProbe)}.Matrix[0].Remove[0]");
        Check(Position(outerRemove, editor).X + outerRemove.Bounds.Width == Position(innerRemove, editor).X + innerRemove.Bounds.Width,
            "Nested remove buttons must share the header column.");
        matrixCell.Text = "42";
        Dispatcher.UIThread.RunJobs();
        Check(probe.Matrix[0][0] == 42, "Nested list edit did not reach the scene.");
        Click(innerRemove);
        Check(probe.Matrix[0].Count == 1, "Nested inner remove must shrink the inner list.");
        Click(ButtonByName(editor, $"{nameof(InspectorValueProbe)}.Matrix.Remove[1]"));
        Check(probe.Matrix.Count == 1, "Nested outer remove must shrink the outer list.");

        // Transform member is a reference slot: None -> select scene Transform -> Clear.
        var targetCombo = Combo(editor, $"{nameof(InspectorValueProbe)}.Target");
        Check(targetCombo.SelectedItem?.ToString() == "None", "Transform reference must start as None.");
        var moverOption = ((System.Collections.IEnumerable)targetCombo.ItemsSource!).Cast<object>()
            .FirstOrDefault(option => option.ToString()!.Contains(moverObject.Name, StringComparison.Ordinal));
        Check(moverOption is not null, "Transform reference must list the scene Transform.");
        targetCombo.SelectedItem = moverOption;
        Dispatcher.UIThread.RunJobs();
        Check(ReferenceEquals(probe.Target, mover), "Transform reference selection must connect the scene instance.");
        Check(editor.Title!.StartsWith("* "), "Transform reference edit must mark the scene dirty.");
        Click(ButtonByName(editor, $"{nameof(InspectorValueProbe)}.Target.Clear"));
        Check(probe.Target is null, "Transform Clear must clear the member.");

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

        // Custom classes render nested editors, not Unsupported badges.
        Select(editor, nestedObject);
        Dispatcher.UIThread.RunJobs();
        var nestedBadges = editor.GetVisualDescendants().OfType<TextBlock>()
            .Where(block => (block.Text ?? "").StartsWith("Unsupported:", StringComparison.Ordinal)).ToList();
        Check(nestedBadges.Count == 0, $"Custom class members must not show Unsupported badges, got {nestedBadges.Count}.");

        // Custom member null -> Create -> nested edit -> Set Null.
        Click(ButtonByName(editor, $"{nameof(InspectorNestedProbe)}.Boss.Create"));
        Check(nested.Boss is not null, "Custom Create must assign a new instance.");
        var bossHp = Box(editor, $"{nameof(InspectorNestedProbe)}.Boss.Hp");
        Check(bossHp.Text == "10", $"Custom nested int must start from the initializer, got '{bossHp.Text}'.");
        bossHp.Text = "42";
        Dispatcher.UIThread.RunJobs();
        Check(nested.Boss!.Hp == 42, "Custom nested edit did not reach the scene.");
        Check(editor.Title!.StartsWith("* "), "Custom nested edit must mark the scene dirty.");
        bossHp.Text = "abc";
        Dispatcher.UIThread.RunJobs();
        Check(errorBadge.IsVisible, "Invalid custom nested input must show an error badge.");
        Check(nested.Boss.Hp == 42, "Invalid custom nested input must not change the scene.");
        bossHp.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Dispatcher.UIThread.RunJobs();
        Check(bossHp.Text == "42" && !errorBadge.IsVisible, "Esc must restore the custom nested value and clear the error.");
        var bossName = Box(editor, $"{nameof(InspectorNestedProbe)}.Boss.Name");
        bossName.Text = "Rex";
        Dispatcher.UIThread.RunJobs();
        Check(nested.Boss.Name == "Rex", "Custom nested string edit did not reach the scene.");
        Click(ButtonByName(editor, $"{nameof(InspectorNestedProbe)}.Boss.Null"));
        Check(nested.Boss is null, "Custom Set Null must clear the member.");

        // Doubly nested custom classes stay editable and collapsible.
        Click(ButtonByName(editor, $"{nameof(InspectorNestedProbe)}.Loadout.Create"));
        var weapon = Box(editor, $"{nameof(InspectorNestedProbe)}.Loadout.Weapon");
        weapon.Text = "Bow";
        Dispatcher.UIThread.RunJobs();
        Check(nested.Loadout!.Weapon == "Bow", "Doubly nested edit did not reach the scene.");
        var kitHp = Box(editor, $"{nameof(InspectorNestedProbe)}.Loadout.Stats.Hp");
        kitHp.Text = "5";
        Dispatcher.UIThread.RunJobs();
        Check(nested.Loadout.Stats.Hp == 5, "Second-level nested edit did not reach the scene.");
        var loadoutToggle = ButtonByName(editor, $"{nameof(InspectorNestedProbe)}.Loadout.Collapse");
        Click(loadoutToggle);
        Dispatcher.UIThread.RunJobs();
        Check(!Box(editor, $"{nameof(InspectorNestedProbe)}.Loadout.Weapon").IsEffectivelyVisible,
            "Collapsed custom object must hide nested editors.");
        Click(loadoutToggle);
        Dispatcher.UIThread.RunJobs();
        Check(Box(editor, $"{nameof(InspectorNestedProbe)}.Loadout.Weapon").IsEffectivelyVisible,
            "Expanded custom object must show nested editors again.");

        // Lists of custom classes grow and edit through nested rows.
        Click(ButtonByName(editor, $"{nameof(InspectorNestedProbe)}.Party.Add"));
        Dispatcher.UIThread.RunJobs();
        Check(nested.Party.Count == 1 && nested.Party[0] is not null, "Custom list Add must append a new instance.");
        var partyHp = Box(editor, $"{nameof(InspectorNestedProbe)}.Party[0].Hp");
        partyHp.Text = "5";
        Dispatcher.UIThread.RunJobs();
        Check(nested.Party[0].Hp == 5, "Custom list element edit did not reach the scene.");
        Click(ButtonByName(editor, $"{nameof(InspectorNestedProbe)}.Party.Remove[0]"));
        Check(nested.Party.Count == 0, "Custom list Remove must shrink the scene list.");

        // Dictionaries of custom classes add entries and edit their values.
        Click(ButtonByName(editor, $"{nameof(InspectorNestedProbe)}.Lookup.Add"));
        Dispatcher.UIThread.RunJobs();
        Check(nested.Lookup.Count == 1, "Custom dictionary Add must grow the scene dictionary.");
        var onlyKey = nested.Lookup.Keys.Single();
        Check(nested.Lookup[onlyKey] is not null, "Custom dictionary Add must assign a new instance.");
        var entryHp = Box(editor, $"{nameof(InspectorNestedProbe)}.Lookup.Value[0].Hp");
        entryHp.Text = "77";
        Dispatcher.UIThread.RunJobs();
        Check(nested.Lookup[onlyKey]!.Hp == 77, "Custom dictionary value edit did not reach the scene.");

        Console.WriteLine("PASS: extended Inspector display, vector/double/enum/list/dictionary/Transform/custom-class edit, validation, Esc revert, and dirty.");
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
        [Inspector] public Color Tint = new(1f, 0.5f, 0.25f, 1f);
        [Inspector] public Color? MaybeTint { get; set; } = new(0f, 1f, 0f, 1f);
        [Inspector] public List<Color> Swatches { get; set; } = [new(1f, 0f, 0f, 1f)];
        [Inspector] public Color[] Colors { get; set; } = [Color.White];
        [Inspector] public Dictionary<string, Color> Palette { get; set; } = new() { ["accent"] = Color.White };
        [Inspector] public double Ratio = 1.5;
        [Inspector] public List<int> Scores { get; set; } = [1, 2];
        [Inspector] public List<List<int>> Matrix { get; set; } = [[1, 2], [3]];
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

    public sealed class SkillStats
    {
        [Inspector] public int Hp { get; set; } = 10;
        [Inspector] public string Name = "fresh";
    }

    public sealed class SkillLoadout
    {
        [Inspector] public string Weapon = "sword";
        [Inspector] public SkillStats Stats { get; set; } = new();
    }

    public sealed class InspectorNestedProbe
    {
        [Inspector] public SkillStats? Boss { get; set; }
        [Inspector] public SkillLoadout? Loadout { get; set; }
        [Inspector] public List<SkillStats> Party { get; set; } = [];
        [Inspector] public Dictionary<string, SkillStats> Lookup { get; set; } = [];
    }
}
