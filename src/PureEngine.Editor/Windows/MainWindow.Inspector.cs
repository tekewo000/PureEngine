using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using PureEngine.Core;

namespace PureEngine.Editor;

public partial class MainWindow
{
    private void InitInspectorModel()
    {
        ViewModel.Inspector.DocumentEdited += RefreshEditedDocument;
        ViewModel.Inspector.PropertyChanged += OnInspectorModelChanged;
    }

    private void OnInspectorModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(InspectorViewModel.HasInputErrors)) QueuePendingUserCodeReload();
    }

    private void RefreshEditedDocument(EditedDocumentKind kind)
    {
        if (kind == EditedDocumentKind.Table) UpdateDataAssetTableChrome();
        else if (kind == EditedDocumentKind.DataAsset) ViewModel.DataAsset.Refresh();
        else
        {
            UpdateSceneTitle();
            UpdatePrefabEditorChrome();
        }
    }

    private void SetInputError(TextBox box, string message)
    {
        if (!_inputIds.TryGetValue(box, out var id)) _inputIds.Add(box, id = Guid.NewGuid());
        ViewModel.Inspector.SetError(id, message);
    }

    private void ClearInputError(TextBox box)
    {
        if (_inputIds.Remove(box, out var id)) ViewModel.Inspector.SetError(id, null);
    }

    private bool IsInvalidInput(TextBox box) => _inputIds.TryGetValue(box, out var id) && ViewModel.Inspector.IsInvalid(id);

    private TextBox BuildDoubleEditor(object component, MemberInfo member, string automationName)
    {
        const string hint = "Enter a number — Press Esc to revert";
        var box = new TextBox { Text = FormatDoubleMember(component, member), PlaceholderText = "0.0" };
        box.Classes.Add("inspectorField");
        box.Classes.Add("numericField");
        box.SetValue(AutomationProperties.NameProperty, automationName);
        ToolTip.SetTip(box, hint);
        box.TextChanged += (_, _) =>
            MarkInvalid(box, ViewModel.Inspector.SetNumericText(component, member, box.Text), hint);
        AttachEscapeRevert(box, component, member);
        return box;
    }

    private Grid BuildVector2Editor(object component, MemberInfo member, string automationName) =>
        BuildVectorEditor(component, member, automationName, 2);

    private Grid BuildVector3Editor(object component, MemberInfo member, string automationName) =>
        BuildVectorEditor(component, member, automationName, 3);

    private Grid BuildVector4Editor(object component, MemberInfo member, string automationName, bool isQuaternion)
    {
        var panel = BuildVectorEditor(component, member, automationName, 4);
        if (isQuaternion)
            ToolTip.SetTip(panel, "Quaternion (x, y, z, w) — Press Esc in a field to revert");
        return panel;
    }

    /// <summary>Member-level Color editor: the preview swatch opens the picker. Dense element rows keep numeric boxes.</summary>
    private StackPanel BuildColorEditor(object component, MemberInfo member, string automationName)
    {
        var preview = BuildColorPreview(GetMemberValue(component, member), $"{automationName}.Preview");
        var root = new StackPanel { Spacing = 6 };
        root.Children.Add(preview);
        AttachColorPicker(preview, root, () => GetMemberValue(component, member), picked => SetMemberValue(component, member, picked), automationName);
        return root;
    }

    private Grid BuildVectorEditor(object component, MemberInfo member, string automationName, int dimensions)
    {
        string[] labels = dimensions == 2 ? ["X", "Y"] : dimensions == 3 ? ["X", "Y", "Z"] : ["X", "Y", "Z", "W"];
        var boxes = labels.Select(label => BuildVectorComponentBox(component, member, automationName, label)).ToList();
        var panel = BuildAxisGrid(labels, boxes);
        ToolTip.SetTip(panel, "Enter numbers — Press Esc in a field to revert");
        return panel;
    }

    private TextBox BuildVectorComponentBox(object component, MemberInfo member, string automationName, string axis)
    {
        var box = new TextBox
        {
            Text = FormatVectorAxis(GetMemberValue(component, member), axis),
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        box.Classes.Add("inspectorField");
        box.SetValue(AutomationProperties.NameProperty, $"{automationName}.{axis}");
        const string hint = "Enter a number — Press Esc to revert";
        ToolTip.SetTip(box, hint);
        box.TextChanged += (_, _) =>
        {
            if (IsPlaying) return;
            if (IsSyncingInspectorForSceneView()) return;
            if (!float.TryParse(box.Text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value)
                || !float.IsFinite(value))
            {
                MarkInvalid(box, "Enter a number");
                return;
            }
            var current = GetMemberValue(component, member);
            var updated = WithVectorAxis(current, axis, value);
            if (updated is null)
            {
                MarkInvalid(box, "Enter a number");
                return;
            }
            SetMemberValue(component, member, updated);
            MarkInvalid(box, null, hint);
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            box.Text = FormatVectorAxis(GetMemberValue(component, member), axis);
            e.Handled = true;
        };
        return box;
    }

    private Control BuildNullableEditor(object component, MemberInfo member, string automationName)
    {
        var underlying = Nullable.GetUnderlyingType(GetMemberType(member))!;
        if (underlying == typeof(int))
            return BuildNullableNumericBox(component, member, automationName, underlying, "Enter an integer — Press Esc to revert (empty = null)");
        if (underlying == typeof(float) || underlying == typeof(double))
            return BuildNullableNumericBox(component, member, automationName, underlying, "Enter a number — Press Esc to revert (empty = null)");
        if (underlying == typeof(bool))
            return BuildNullableBoolEditor(component, member, automationName);
        if (underlying == typeof(Vector2))
            return BuildNullableVectorEditor(component, member, automationName, 2);
        if (underlying == typeof(Vector3))
            return BuildNullableVectorEditor(component, member, automationName, 3);
        if (underlying == typeof(Vector4) || underlying == typeof(Quaternion))
            return BuildNullableVectorEditor(component, member, automationName, 4);
        if (underlying == typeof(PureEngine.Core.Color))
            return BuildNullableColorEditor(component, member, automationName);
        if (underlying.IsEnum)
            return BuildNullableEnumEditor(component, member, automationName);
        return UnsupportedBadge(GetMemberType(member));
    }

    private TextBox BuildNullableNumericBox(object component, MemberInfo member, string automationName, Type underlying, string hint)
    {
        var box = new TextBox { Text = FormatNullableMember(component, member), PlaceholderText = "Null" };
        box.Classes.Add("inspectorField");
        box.Classes.Add("numericField");
        box.SetValue(AutomationProperties.NameProperty, automationName);
        ToolTip.SetTip(box, hint);
        box.TextChanged += (_, _) =>
        {
            if (IsPlaying) return;
            var text = (box.Text ?? "").Trim();
            if (text.Length == 0)
            {
                SetMemberValue(component, member, null);
                MarkInvalid(box, null, hint);
                return;
            }
            object? parsed = underlying == typeof(int)
                ? (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) ? integer : null)
                : underlying == typeof(float)
                    ? (float.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var single) && float.IsFinite(single) ? single : null)
                    : (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var real) && double.IsFinite(real) ? real : null);
            if (parsed is null)
                MarkInvalid(box, underlying == typeof(int) ? "Enter an integer" : "Enter a number");
            else
            {
                SetMemberValue(component, member, parsed);
                MarkInvalid(box, null, hint);
            }
        };
        AttachEscapeRevert(box, component, member);
        return box;
    }

    private StackPanel BuildNullableBoolEditor(object component, MemberInfo member, string automationName)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var check = new CheckBox { IsChecked = GetMemberValue(component, member) is true, Content = NullableBoolLabel(GetMemberValue(component, member)) };
        check.Classes.Add("inspectorCheck");
        check.SetValue(AutomationProperties.NameProperty, automationName);
        var clear = BuildHeaderIconButton("Icon.Dismiss", $"{automationName}.Null");
        ToolTip.SetTip(clear, "Set to null.");
        check.IsCheckedChanged += (_, _) =>
        {
            if (IsPlaying) return;
            var value = check.IsChecked == true;
            check.Content = value ? "True" : "False";
            SetMemberValue(component, member, value);
        };
        clear.Click += (_, _) =>
        {
            if (IsPlaying) return;
            SetMemberValue(component, member, null);
            check.IsChecked = false;
            check.Content = "Null";
        };
        panel.Children.Add(check);
        panel.Children.Add(clear);
        return panel;
    }

    private StackPanel BuildNullableVectorEditor(object component, MemberInfo member, string automationName, int dimensions)
    {
        var root = new StackPanel { Spacing = 6 };
        var status = new TextBlock { Classes = { "memberType" }, Text = "Null", VerticalAlignment = VerticalAlignment.Center };
        var create = BuildHeaderIconButton("Icon.AddSquare", $"{automationName}.Create");
        ToolTip.SetTip(create, "Create a new value.");
        var clear = BuildHeaderIconButton("Icon.Dismiss", $"{automationName}.Null");
        ToolTip.SetTip(clear, "Set to null.");
        var header = BuildSplitHeader(status, create, clear);
        var vectorPanel = BuildVectorEditorForNullable(component, member, automationName, dimensions);
        root.Children.Add(header);
        root.Children.Add(vectorPanel);
        void refresh()
        {
            var isNull = GetMemberValue(component, member) is null;
            status.IsVisible = isNull;
            create.IsVisible = isNull;
            clear.IsVisible = !isNull;
            vectorPanel.IsVisible = !isNull;
            if (!isNull)
                RefreshVectorBoxes(vectorPanel, GetMemberValue(component, member));
        }
        create.Click += (_, _) =>
        {
            if (IsPlaying) return;
            SetMemberValue(component, member, dimensions == 2 ? (object)new Vector2() : dimensions == 3 ? new Vector3() : new Vector4());
            refresh();
        };
        clear.Click += (_, _) =>
        {
            if (IsPlaying) return;
            SetMemberValue(component, member, null);
            refresh();
        };
        refresh();
        return root;
    }

    private StackPanel BuildNullableColorEditor(object component, MemberInfo member, string automationName)
    {
        var root = new StackPanel { Spacing = 6 };
        var status = new TextBlock { Classes = { "memberType" }, Text = "Null", VerticalAlignment = VerticalAlignment.Center };
        var create = BuildHeaderIconButton("Icon.AddSquare", $"{automationName}.Create");
        ToolTip.SetTip(create, "Create a new value.");
        var clear = BuildHeaderIconButton("Icon.Dismiss", $"{automationName}.Null");
        ToolTip.SetTip(clear, "Set to null.");
        var header = BuildSplitHeader(status, create, clear);
        var colorPanel = BuildColorEditorForNullable(component, member, automationName);
        root.Children.Add(header);
        root.Children.Add(colorPanel);
        void refresh()
        {
            var isNull = GetMemberValue(component, member) is null;
            status.IsVisible = isNull;
            create.IsVisible = isNull;
            clear.IsVisible = !isNull;
            colorPanel.IsVisible = !isNull;
            if (!isNull)
                RefreshColorBoxes(colorPanel, GetMemberValue(component, member));
        }
        create.Click += (_, _) =>
        {
            if (IsPlaying) return;
            SetMemberValue(component, member, PureEngine.Core.Color.White);
            refresh();
        };
        clear.Click += (_, _) =>
        {
            if (IsPlaying) return;
            SetMemberValue(component, member, null);
            foreach (var box in colorPanel.GetVisualDescendants().OfType<TextBox>())
                MarkInvalid(box, null);
            refresh();
        };
        refresh();
        return root;
    }

    /// <summary>Nullable member-level Color editor: same preview-plus-picker rule as <see cref="BuildColorEditor"/>.</summary>
    private StackPanel BuildColorEditorForNullable(object component, MemberInfo member, string automationName)
    {
        var preview = BuildColorPreview(GetMemberValue(component, member), $"{automationName}.Preview");
        var root = new StackPanel { Spacing = 6 };
        root.Children.Add(preview);
        AttachColorPicker(preview, root, () => GetMemberValue(component, member), picked => SetMemberValue(component, member, picked), automationName);
        return root;
    }

    private Grid BuildVectorEditorForNullable(object component, MemberInfo member, string automationName, int dimensions)
    {
        string[] labels = dimensions == 2 ? ["X", "Y"] : dimensions == 3 ? ["X", "Y", "Z"] : ["X", "Y", "Z", "W"];
        var boxes = new List<TextBox>();
        foreach (var label in labels)
        {
            var box = new TextBox { FontSize = 12, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            box.Classes.Add("inspectorField");
            box.SetValue(AutomationProperties.NameProperty, $"{automationName}.{label}");
            const string hint = "Enter a number — Press Esc to revert";
            ToolTip.SetTip(box, hint);
            var axis = label;
            box.TextChanged += (_, _) =>
            {
                if (IsPlaying) return;
                if (!float.TryParse(box.Text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value) || !float.IsFinite(value))
                {
                    MarkInvalid(box, "Enter a number");
                    return;
                }
                var current = GetMemberValue(component, member);
                if (current is null)
                {
                    MarkInvalid(box, "Enter a number");
                    return;
                }
                var updated = WithVectorAxis(current, axis, value);
                if (updated is null)
                    MarkInvalid(box, "Enter a number");
                else
                {
                    SetMemberValue(component, member, updated);
                    MarkInvalid(box, null, hint);
                }
            };
            box.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape) return;
                box.Text = FormatVectorAxis(GetMemberValue(component, member), axis);
                e.Handled = true;
            };
            boxes.Add(box);
        }
        return BuildAxisGrid(labels, boxes);
    }

    private StackPanel BuildTransformEditor(object component, MemberInfo member, string automationName)
    {
        var root = new StackPanel { Spacing = 6 };
        var status = new TextBlock { Classes = { "memberType" }, Text = "Null", VerticalAlignment = VerticalAlignment.Center };
        var create = BuildHeaderIconButton("Icon.AddSquare", $"{automationName}.Create");
        ToolTip.SetTip(create, "Create a new value.");
        var clear = BuildHeaderIconButton("Icon.Dismiss", $"{automationName}.Null");
        ToolTip.SetTip(clear, "Set to null.");
        var header = BuildSplitHeader(status, create, clear);
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(new TextBlock { Classes = { "memberType" }, Text = "Position" });
        body.Children.Add(BuildTransformAxisRow(component, member, automationName, "LocalPosition"));
        body.Children.Add(new TextBlock { Classes = { "memberType" }, Text = "Rotation (Quaternion)" });
        body.Children.Add(BuildTransformAxisRow(component, member, automationName, "LocalRotation"));
        body.Children.Add(new TextBlock { Classes = { "memberType" }, Text = "Scale" });
        body.Children.Add(BuildTransformAxisRow(component, member, automationName, "LocalScale"));
        root.Children.Add(header);
        root.Children.Add(body);
        void refresh()
        {
            var isNull = GetMemberValue(component, member) is not PureEngine.Core.Transform;
            status.IsVisible = isNull;
            create.IsVisible = isNull;
            clear.IsVisible = !isNull;
            body.IsVisible = !isNull;
            if (!isNull)
                RefreshTransformRows(body, (PureEngine.Core.Transform)GetMemberValue(component, member)!);
        }
        create.Click += (_, _) =>
        {
            if (IsPlaying) return;
            SetMemberValue(component, member, new PureEngine.Core.Transform());
            refresh();
        };
        clear.Click += (_, _) =>
        {
            if (IsPlaying) return;
            SetMemberValue(component, member, null);
            foreach (var box in root.GetVisualDescendants().OfType<TextBox>())
                ClearInputError(box);
            UpdateErrorBadge();
            refresh();
        };
        refresh();
        ToolTip.SetTip(root, "Transform — Press Esc in a field to revert");
        return root;
    }

    /// <summary>Axis badge chip matching the S/U/D badge style. X/R reuse the red tint, Y/G the green tint, Z/B the blue tint; W/A stay neutral.</summary>
    private static Border BuildAxisBadge(string axis)
    {
        var background = axis switch
        {
            "X" or "R" => DestroyBadgeBackground,
            "Y" or "G" => UpdateBadgeBackground,
            "Z" or "B" => StartBadgeBackground,
            _ => NeutralBadgeBackground,
        };
        var foreground = axis switch
        {
            "X" or "R" => DestroyAccent,
            "Y" or "G" => UpdateAccent,
            "Z" or "B" => StartAccent,
            _ => MemberLabelBrush,
        };
        var letter = new TextBlock
        {
            Text = axis,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = foreground,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return new Border
        {
            Background = background,
            CornerRadius = new Avalonia.CornerRadius(4),
            Width = 20,
            Height = 20,
            VerticalAlignment = VerticalAlignment.Center,
            Child = letter,
        };
    }

    /// <summary>Axis badge + value grid. Boxes share the available width so 4-axis rows (Quaternion) never clip.</summary>
    private static Grid BuildAxisGrid(string[] axes, List<TextBox> boxes)
    {
        var grid = new Grid { ColumnSpacing = 6 };
        for (var i = 0; i < boxes.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            var badge = BuildAxisBadge(axes[i]);
            Grid.SetColumn(badge, i * 2);
            boxes[i].Classes.Add("numericField");
            boxes[i].MinWidth = 40;
            boxes[i].HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetColumn(boxes[i], i * 2 + 1);
            grid.Children.Add(badge);
            grid.Children.Add(boxes[i]);
        }
        return grid;
    }

    /// <summary>Header action (Add/Set Null/Clear/Create). Icon-only borderless button reusing the dismissButton style so only the glyph shows.</summary>
    private static Avalonia.Controls.Button BuildHeaderIconButton(string resourceKey, string automationName)
    {
        var button = new Avalonia.Controls.Button
        {
            Content = new PathIcon
            {
                Data = (StreamGeometry?)Application.Current?.FindResource(resourceKey),
                Width = 12,
                Height = 12,
            },
        };
        button.Classes.Add("dismissButton");
        button.SetValue(AutomationProperties.NameProperty, automationName);
        return button;
    }

    /// <summary>Circular dismiss button. The round shape comes from the dismissButton style; keep chrome out of code so hover stays round.</summary>
    private static Avalonia.Controls.Button BuildRemoveButton(string automationName)
    {
        var remove = new Avalonia.Controls.Button
        {
            Content = new PathIcon
            {
                Data = (StreamGeometry?)Application.Current?.FindResource("Icon.DismissCircle"),
                Width = 14,
                Height = 14,
            },
        };
        remove.Classes.Add("dismissButton");
        remove.SetValue(AutomationProperties.NameProperty, automationName);
        return remove;
    }

    /// <summary>Split header: left status block, right action buttons. Grid keeps actions right-aligned in narrow panes.</summary>
    private static Grid BuildSplitHeader(Control left, params Avalonia.Controls.Button[] actions)
    {
        var header = new Grid { ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
        header.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        header.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        left.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(left, 0);
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        foreach (var action in actions)
            panel.Children.Add(action);
        Grid.SetColumn(panel, 1);
        header.Children.Add(left);
        header.Children.Add(panel);
        return header;
    }

    private Grid BuildTransformAxisRow(object component, MemberInfo member, string automationName, string property)
    {
        string[] axes = property == "LocalPosition" || property == "LocalScale" ? ["X", "Y", "Z"] : ["X", "Y", "Z", "W"];
        var boxes = new List<TextBox>();
        foreach (var axis in axes)
        {
            var box = new TextBox { FontSize = 12, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            box.Classes.Add("inspectorField");
            box.SetValue(AutomationProperties.NameProperty, $"{automationName}.{property}.{axis}");
            const string hint = "Enter a number — Press Esc to revert";
            ToolTip.SetTip(box, hint);
            var capturedAxis = axis;
            box.TextChanged += (_, _) =>
            {
                if (IsPlaying) return;
                if (IsSyncingInspectorForSceneView()) return;
                if (!float.TryParse(box.Text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value) || !float.IsFinite(value))
                {
                    MarkInvalid(box, "Enter a number");
                    return;
                }
                if (GetMemberValue(component, member) is not PureEngine.Core.Transform transform)
                {
                    MarkInvalid(box, "Enter a number");
                    return;
                }
                if (property == "LocalPosition")
                    transform.LocalPosition = WithVector3Axis(transform.LocalPosition, capturedAxis, value);
                else if (property == "LocalScale")
                    transform.LocalScale = WithVector3Axis(transform.LocalScale, capturedAxis, value);
                else
                    transform.LocalRotation = WithQuaternionAxis(transform.LocalRotation, capturedAxis, value);
                MarkEdited(component);
                MarkInvalid(box, null, hint);
            };
            box.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape) return;
                if (GetMemberValue(component, member) is not PureEngine.Core.Transform transform) return;
                box.Text = property == "LocalPosition" ? FormatFloat(transform.LocalPosition.GetAxis(capturedAxis))
                    : property == "LocalScale" ? FormatFloat(transform.LocalScale.GetAxis(capturedAxis))
                    : FormatFloat(transform.LocalRotation.GetAxis(capturedAxis));
                e.Handled = true;
            };
            boxes.Add(box);
        }
        return BuildAxisGrid(axes, boxes);
    }

    private StackPanel BuildSequenceEditor(object component, MemberInfo member, string automationName)
    {
        var memberType = GetMemberType(member);
        var elementType = memberType.IsArray ? memberType.GetElementType()! : memberType.GetGenericArguments()[0];
        var root = new StackPanel { Spacing = 6 };
        var count = new TextBlock { Classes = { "memberType" }, VerticalAlignment = VerticalAlignment.Center };
        var add = BuildHeaderIconButton("Icon.AddSquare", $"{automationName}.Add");
        ToolTip.SetTip(add, "Add a row.");
        var setNull = BuildHeaderIconButton("Icon.Dismiss", $"{automationName}.Null");
        ToolTip.SetTip(setNull, "Set the list itself to null. Removing rows keeps an empty list.");
        var clear = BuildHeaderIconButton("Icon.Delete", $"{automationName}.Clear");
        ToolTip.SetTip(clear, "Remove all rows. The empty list stays.");
        var nullStatus = new TextBlock { Classes = { "memberType" }, Text = "Null", VerticalAlignment = VerticalAlignment.Center };
        var create = BuildHeaderIconButton("Icon.AddSquare", $"{automationName}.Create");
        ToolTip.SetTip(create, "Create an empty list.");
        var elements = new StackPanel { Spacing = 4 };
        var toggle = BuildCollapseToggle($"{automationName}.Collapse", automationName, ViewModel.Inspector.CollapsedMembers,
            nowExpanded => elements.IsVisible = nowExpanded);
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(toggle);
        left.Children.Add(count);
        var header = BuildSplitHeader(left, add, setNull, clear);
        var nullHeader = BuildSplitHeader(nullStatus, create);
        root.Children.Add(header);
        root.Children.Add(nullHeader);
        root.Children.Add(elements);
        void refresh()
        {
            foreach (var box in elements.GetVisualDescendants().OfType<TextBox>())
                ClearInputError(box);
            elements.Children.Clear();
            var value = GetMemberValue(component, member);
            if (value is null)
            {
                nullHeader.IsVisible = true;
                header.IsVisible = false;
                elements.IsVisible = false;
            }
            else
            {
                nullHeader.IsVisible = false;
                header.IsVisible = true;
                elements.IsVisible = !ViewModel.Inspector.CollapsedMembers.TryGetValue(automationName, out var elementsCollapsed) || !elementsCollapsed;
                var items = value is IEnumerable enumerable ? enumerable.Cast<object?>().ToList() : [];
                count.Text = $"{items.Count} items";
                for (var i = 0; i < items.Count; i++)
                    elements.Children.Add(BuildSequenceElementRow(component, member, elementType, i, automationName, refresh));
            }
            UpdateErrorBadge();
            QueuePendingUserCodeReload();
        }
        add.Click += (_, _) =>
        {
            if (IsPlaying) return;
            AddSequenceElement(component, member, elementType);
            refresh();
        };
        setNull.Click += (_, _) =>
        {
            if (IsPlaying) return;
            SetMemberValue(component, member, null);
            refresh();
        };
        clear.Click += (_, _) =>
        {
            if (IsPlaying) return;
            ClearSequence(component, member);
            refresh();
        };
        create.Click += (_, _) =>
        {
            if (IsPlaying) return;
            SetMemberValue(component, member, memberType.IsArray ? Array.CreateInstance(elementType, 0) : Activator.CreateInstance(memberType)!);
            refresh();
        };
        refresh();
        return root;
    }

    /// <summary>Removes every element while keeping the list itself. Row remove buttons cover single rows.</summary>
    private void ClearSequence(object component, MemberInfo member)
    {
        var memberType = GetMemberType(member);
        var value = GetMemberValue(component, member);
        if (value is null) return;
        if (memberType.IsArray)
            SetMemberValue(component, member, Array.CreateInstance(memberType.GetElementType()!, 0));
        else if (value is IList list)
        {
            list.Clear();
            MarkEdited(component);
        }
    }

    private Grid BuildSequenceElementRow(object component, MemberInfo member, Type elementType, int index, string automationName, Action refresh)
    {
        var row = new Grid { ColumnSpacing = 6, VerticalAlignment = VerticalAlignment.Center };
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var label = new TextBlock { Text = $"[{index}]", Classes = { "memberType" }, Width = 36, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 0);
        var editor = BuildSequenceElementEditor(component, member, elementType, index, automationName);
        AttachEditorDropHandlers(row, editor);
        editor.HorizontalAlignment = HorizontalAlignment.Stretch;
        editor.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(editor, 1);
        var remove = BuildRemoveButton($"{automationName}.Remove[{index}]");
        Grid.SetColumn(remove, 2);
        remove.Click += (_, _) =>
        {
            if (IsPlaying) return;
            RemoveSequenceElement(component, member, index);
            refresh();
        };
        row.Children.Add(label);
        row.Children.Add(editor);
        row.Children.Add(remove);
        return row;
    }

    private Control BuildSequenceElementEditor(object component, MemberInfo member, Type elementType, int index, string automationName)
    {
        var elementName = $"{automationName}[{index}]";
        if (SceneReferenceTypes.IsSingleReference(elementType, Components.Registry))
            return BuildSequenceReferenceEditor(component, member, index, elementType, elementName);
        if (elementType == typeof(string))
            return BuildSequenceStringBox(component, member, index, elementName);
        if (elementType == typeof(int))
            return BuildSequenceIntBox(component, member, index, elementName);
        if (elementType == typeof(float))
            return BuildSequenceFloatBox(component, member, index, elementName, isDouble: false);
        if (elementType == typeof(double))
            return BuildSequenceFloatBox(component, member, index, elementName, isDouble: true);
        if (elementType == typeof(bool))
            return BuildSequenceBoolBox(component, member, index, elementName);
        if (elementType == typeof(Vector2) || elementType == typeof(Vector3) || elementType == typeof(Vector4) || elementType == typeof(Quaternion))
            return BuildSequenceVectorRow(component, member, elementType, index, elementName);
        if (elementType == typeof(PureEngine.Core.Color))
            return BuildSequenceColorRow(component, member, index, elementName);
        if (elementType.IsEnum)
            return BuildSequenceEnumBox(component, member, elementType, index, elementName);
        var underlying = Nullable.GetUnderlyingType(elementType);
        if (underlying is not null)
        {
            if (underlying.IsEnum)
                return BuildSequenceNullableEnumBox(component, member, elementType, underlying, index, elementName);
            return BuildSequenceNullableBox(component, member, elementType, underlying, index, elementName);
        }
        if (InspectorValueTypes.IsCustomInspectorObject(elementType))
            return BuildSequenceObjectBox(component, member, elementType, index, elementName);
        return UnsupportedBadge(elementType);
    }

    private StackPanel BuildDictionaryEditor(object component, MemberInfo member, string automationName)
    {
        var memberType = GetMemberType(member);
        var valueType = memberType.GetGenericArguments()[1];
        var root = new StackPanel { Spacing = 6 };
        var count = new TextBlock { Classes = { "memberType" }, VerticalAlignment = VerticalAlignment.Center };
        var add = BuildHeaderIconButton("Icon.AddSquare", $"{automationName}.Add");
        ToolTip.SetTip(add, "Add an entry.");
        var setNull = BuildHeaderIconButton("Icon.Dismiss", $"{automationName}.Null");
        ToolTip.SetTip(setNull, "Set the dictionary itself to null. Removing rows keeps an empty dictionary.");
        var clear = BuildHeaderIconButton("Icon.Delete", $"{automationName}.Clear");
        ToolTip.SetTip(clear, "Remove all entries. The empty dictionary stays.");
        var nullStatus = new TextBlock { Classes = { "memberType" }, Text = "Null", VerticalAlignment = VerticalAlignment.Center };
        var create = BuildHeaderIconButton("Icon.AddSquare", $"{automationName}.Create");
        ToolTip.SetTip(create, "Create an empty dictionary.");
        var rows = new StackPanel { Spacing = 4 };
        var toggle = BuildCollapseToggle($"{automationName}.Collapse", automationName, ViewModel.Inspector.CollapsedMembers,
            nowExpanded => rows.IsVisible = nowExpanded);
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(toggle);
        left.Children.Add(count);
        var header = BuildSplitHeader(left, add, setNull, clear);
        var nullHeader = BuildSplitHeader(nullStatus, create);
        root.Children.Add(header);
        root.Children.Add(nullHeader);
        root.Children.Add(rows);
        void refresh()
        {
            foreach (var box in rows.GetVisualDescendants().OfType<TextBox>())
                ClearInputError(box);
            rows.Children.Clear();
            var value = GetMemberValue(component, member);
            if (value is not IDictionary dictionary)
            {
                nullHeader.IsVisible = true;
                header.IsVisible = false;
                rows.IsVisible = false;
            }
            else
            {
                nullHeader.IsVisible = false;
                header.IsVisible = true;
                rows.IsVisible = !ViewModel.Inspector.CollapsedMembers.TryGetValue(automationName, out var rowsCollapsed) || !rowsCollapsed;
                var keys = dictionary.Keys.Cast<string>().OrderBy(key => key, StringComparer.Ordinal).ToList();
                count.Text = $"{keys.Count} entries";
                for (var i = 0; i < keys.Count; i++)
                    rows.Children.Add(BuildDictionaryRow(component, member, valueType, keys[i], i, automationName, refresh));
            }
            UpdateErrorBadge();
            QueuePendingUserCodeReload();
        }
        add.Click += (_, _) =>
        {
            if (IsPlaying) return;
            if (GetMemberValue(component, member) is not IDictionary dictionary) return;
            var key = UniqueDictionaryKey(dictionary);
            dictionary.Add(key, DefaultElementValue(valueType));
            MarkEdited(component);
            refresh();
        };
        setNull.Click += (_, _) =>
        {
            if (IsPlaying) return;
            SetMemberValue(component, member, null);
            refresh();
        };
        clear.Click += (_, _) =>
        {
            if (IsPlaying) return;
            if (GetMemberValue(component, member) is IDictionary dictionary)
                dictionary.Clear();
            MarkEdited(component);
            refresh();
        };
        create.Click += (_, _) =>
        {
            if (IsPlaying) return;
            SetMemberValue(component, member, Activator.CreateInstance(memberType)!);
            refresh();
        };
        refresh();
        return root;
    }

    private Grid BuildDictionaryRow(object component, MemberInfo member, Type valueType, string key, int rowIndex, string automationName, Action refresh)
    {
        var row = new Grid { ColumnSpacing = 6, VerticalAlignment = VerticalAlignment.Center };
        row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var keyBox = new TextBox { Text = key, MinWidth = 40, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        keyBox.Classes.Add("inspectorField");
        keyBox.SetValue(AutomationProperties.NameProperty, $"{automationName}.Key[{rowIndex}]");
        ToolTip.SetTip(keyBox, "Dictionary key — must be unique and non-empty");
        keyBox.TextChanged += (_, _) =>
        {
            if (IsPlaying) return;
            var next = keyBox.Text ?? "";
            if (GetMemberValue(component, member) is not IDictionary dictionary) return;
            if (next.Length == 0 || (next != key && dictionary.Contains(next)))
            {
                MarkInvalid(keyBox, "Enter a unique non-empty key");
                return;
            }
            if (next == key)
            {
                MarkInvalid(keyBox, null, "Dictionary key — must be unique and non-empty");
                return;
            }
            var preserved = dictionary[key];
            dictionary.Remove(key);
            dictionary.Add(next, preserved);
            MarkEdited(component);
            MarkInvalid(keyBox, null, "Dictionary key — must be unique and non-empty");
            refresh();
        };
        keyBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            keyBox.Text = key;
            e.Handled = true;
        };
        row.Children.Add(keyBox);
        var valueEditor = BuildDictionaryValueEditor(component, member, valueType, key, automationName, rowIndex);
        AttachEditorDropHandlers(row, valueEditor);
        valueEditor.HorizontalAlignment = HorizontalAlignment.Stretch;
        valueEditor.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(keyBox, 0);
        Grid.SetColumn(valueEditor, 1);
        row.Children.Add(valueEditor);
        var remove = BuildRemoveButton($"{automationName}.Remove[{rowIndex}]");
        Grid.SetColumn(remove, 2);
        remove.Click += (_, _) =>
        {
            if (IsPlaying) return;
            if (GetMemberValue(component, member) is IDictionary dictionary)
                dictionary.Remove(key);
            MarkEdited(component);
            refresh();
        };
        row.Children.Add(remove);
        return row;
    }

    private Control BuildDictionaryValueEditor(object component, MemberInfo member, Type valueType, string key, string automationName, int rowIndex)
    {
        var valueName = $"{automationName}.Value[{rowIndex}]";
        if (SceneReferenceTypes.IsSingleReference(valueType, Components.Registry))
            return BuildDictionaryReferenceEditor(component, member, key, valueType, valueName);
        if (valueType == typeof(string))
        {
            var box = new TextBox { Text = DictionaryStringValue(component, member, key), MinWidth = 40, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
            box.Classes.Add("inspectorField");
            box.SetValue(AutomationProperties.NameProperty, valueName);
            box.TextChanged += (_, _) =>
            {
                if (IsPlaying) return;
                if (GetMemberValue(component, member) is not IDictionary dictionary) return;
                if (!dictionary.Contains(key)) return;
                dictionary[key] = box.Text ?? "";
                MarkEdited(component);
            };
            box.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape) return;
                box.Text = DictionaryStringValue(component, member, key);
                e.Handled = true;
            };
            return box;
        }
        if (valueType == typeof(int))
            return BuildDictionaryNumericBox(component, member, key, valueName, isDouble: false, isInt: true);
        if (valueType == typeof(float))
            return BuildDictionaryNumericBox(component, member, key, valueName, isDouble: false, isInt: false);
        if (valueType == typeof(double))
            return BuildDictionaryNumericBox(component, member, key, valueName, isDouble: true, isInt: false);
        if (valueType == typeof(bool))
        {
            var initial = GetMemberValue(component, member) is IDictionary dictionary && dictionary.Contains(key) && dictionary[key] is true;
            var check = new CheckBox { IsChecked = initial, Content = initial ? "True" : "False", VerticalAlignment = VerticalAlignment.Center };
            check.Classes.Add("inspectorCheck");
            check.SetValue(AutomationProperties.NameProperty, valueName);
            check.IsCheckedChanged += (_, _) =>
            {
                if (IsPlaying) return;
                if (GetMemberValue(component, member) is not IDictionary target || !target.Contains(key)) return;
                var value = check.IsChecked == true;
                check.Content = value ? "True" : "False";
                target[key] = value;
                MarkEdited(component);
            };
            return check;
        }
        if (valueType == typeof(Vector2) || valueType == typeof(Vector3) || valueType == typeof(Vector4) || valueType == typeof(Quaternion))
            return BuildDictionaryVectorRow(component, member, valueType, key, valueName);
        if (valueType == typeof(PureEngine.Core.Color))
            return BuildDictionaryColorRow(component, member, key, valueName);
        if (valueType.IsEnum)
            return BuildDictionaryEnumBox(component, member, valueType, key, valueName);
        var underlying = Nullable.GetUnderlyingType(valueType);
        if (underlying is not null)
        {
            if (underlying.IsEnum)
                return BuildDictionaryNullableEnumBox(component, member, valueType, underlying, key, valueName);
            return BuildDictionaryNullableBox(component, member, valueType, underlying, key, valueName);
        }
        if (InspectorValueTypes.IsCustomInspectorObject(valueType))
            return BuildDictionaryObjectBox(component, member, valueType, key, valueName);
        return UnsupportedBadge(valueType);
    }

    private Control BuildEnumEditor(object component, MemberInfo member, string automationName)
    {
        var enumType = GetMemberType(member);
        if (enumType.IsDefined(typeof(FlagsAttribute), inherit: false))
            return BuildFlagsEditor(component, member, automationName, enumType, getCurrent: () => GetMemberValue(component, member), setCurrent: value => SetMemberValue(component, member, value));
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        combo.Classes.Add("inspectorCombo");
        combo.SetValue(AutomationProperties.NameProperty, automationName);
        ToolTip.SetTip(combo, $"{member.Name} : {FriendlyTypeName(enumType)} — Select a value");
        var items = Enum.GetValues(enumType).Cast<object>().ToList();
        var current = GetMemberValue(component, member);
        if (current is not null && !items.Contains(current))
            items.Add(current);
        combo.ItemsSource = items;
        combo.SelectedItem = current;
        combo.SelectionChanged += (_, _) =>
        {
            if (IsPlaying) return;
            if (combo.SelectedItem is null) return;
            SetMemberValue(component, member, combo.SelectedItem);
        };
        combo.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            combo.SelectedItem = GetMemberValue(component, member);
            e.Handled = true;
        };
        return combo;
    }

    private StackPanel BuildFlagsEditor(object _, MemberInfo member, string automationName, Type enumType, Func<object?> getCurrent, Action<object> setCurrent)
    {
        var root = new StackPanel { Spacing = 4 };
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 4 };
        List<(CheckBox Check, Enum Flag)> checks = [];
        var refreshing = false;
        void refresh()
        {
            refreshing = true;
            try
            {
                var current = (Enum)(getCurrent() ?? Enum.ToObject(enumType, 0));
                foreach (var (check, flag) in checks)
                    check.IsChecked = current.HasFlag(flag);
            }
            finally { refreshing = false; }
        }
        foreach (var flag in Enum.GetValues(enumType).Cast<Enum>().Where(flag => EnumBits(flag) != 0))
        {
            var name = flag.ToString()!;
            var check = new CheckBox { Content = name, VerticalAlignment = VerticalAlignment.Center };
            check.Classes.Add("inspectorCheck");
            check.SetValue(AutomationProperties.NameProperty, $"{automationName}.{name}");
            ToolTip.SetTip(check, $"{member.Name} : {FriendlyTypeName(enumType)} flag");
            var captured = flag;
            check.IsCheckedChanged += (_, _) =>
            {
                if (IsPlaying || refreshing) return;
                var next = CombineFlags(enumType, getCurrent(), captured, check.IsChecked == true);
                setCurrent(next);
                refresh();
            };
            checks.Add((check, flag));
            wrap.Children.Add(check);
        }
        var clear = new Avalonia.Controls.Button { Content = "None", FontSize = 11, Padding = new Avalonia.Thickness(8, 2), HorizontalAlignment = HorizontalAlignment.Left };
        clear.SetValue(AutomationProperties.NameProperty, $"{automationName}.Clear");
        clear.Click += (_, _) =>
        {
            if (IsPlaying) return;
            setCurrent(Enum.ToObject(enumType, 0));
            refresh();
        };
        root.Children.Add(wrap);
        root.Children.Add(clear);
        refresh();
        return root;
    }

    private StackPanel BuildNullableEnumEditor(object component, MemberInfo member, string automationName)
    {
        var enumType = Nullable.GetUnderlyingType(GetMemberType(member))!;
        var root = new StackPanel { Spacing = 6 };
        var status = new TextBlock { Classes = { "memberType" }, Text = "Null", VerticalAlignment = VerticalAlignment.Center };
        var create = BuildHeaderIconButton("Icon.AddSquare", $"{automationName}.Create");
        ToolTip.SetTip(create, "Create a new value.");
        var clear = BuildHeaderIconButton("Icon.Dismiss", $"{automationName}.Null");
        ToolTip.SetTip(clear, "Set to null.");
        var header = BuildSplitHeader(status, create, clear);
        var body = new StackPanel { Spacing = 4 };
        root.Children.Add(header);
        root.Children.Add(body);
        void refresh()
        {
            var value = GetMemberValue(component, member);
            var isNull = value is null;
            status.IsVisible = isNull;
            create.IsVisible = isNull;
            clear.IsVisible = !isNull;
            body.IsVisible = !isNull;
            body.Children.Clear();
            if (!isNull)
            {
                if (enumType.IsDefined(typeof(FlagsAttribute), inherit: false))
                    body.Children.Add(BuildFlagsEditor(component, member, automationName, enumType, getCurrent: () => GetMemberValue(component, member) ?? Enum.ToObject(enumType, 0), setCurrent: v => SetMemberValue(component, member, v)));
                else
                {
                    var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
                    combo.Classes.Add("inspectorCombo");
                    combo.SetValue(AutomationProperties.NameProperty, automationName);
                    var items = Enum.GetValues(enumType).Cast<object>().ToList();
                    if (!items.Contains(value!))
                        items.Add(value!);
                    combo.ItemsSource = items;
                    combo.SelectedItem = value;
                    combo.SelectionChanged += (_, _) =>
                    {
                        if (IsPlaying) return;
                        if (combo.SelectedItem is null) return;
                        SetMemberValue(component, member, combo.SelectedItem);
                    };
                    body.Children.Add(combo);
                }
            }
        }
        create.Click += (_, _) =>
        {
            if (IsPlaying) return;
            SetMemberValue(component, member, Enum.ToObject(enumType, 0));
            refresh();
        };
        clear.Click += (_, _) =>
        {
            if (IsPlaying) return;
            SetMemberValue(component, member, null);
            foreach (var box in root.GetVisualDescendants().OfType<TextBox>())
                ClearInputError(box);
            UpdateErrorBadge();
            refresh();
        };
        refresh();
        return root;
    }

    private static object CombineFlags(Type enumType, object? current, object toggled, bool isChecked)
    {
        var bits = EnumBits(current ?? Enum.ToObject(enumType, 0));
        var flag = EnumBits(toggled);
        bits = isChecked ? bits | flag : bits & ~flag;
        return Enum.ToObject(enumType, bits);
    }

    private static ulong EnumBits(object value) => Enum.GetUnderlyingType(value.GetType()) == typeof(ulong)
        ? Convert.ToUInt64(value, CultureInfo.InvariantCulture)
        : unchecked((ulong)Convert.ToInt64(value, CultureInfo.InvariantCulture));

    private static Border UnsupportedBadge(Type memberType) =>
        new()
        {
            Classes = { "kindBadge", "unsupportedBadge" },
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = $"Unsupported: {FriendlyTypeName(memberType)}", FontSize = 11, Foreground = UnsupportedBadgeBrush },
        };

    private static string FormatDoubleMember(object component, MemberInfo member) =>
        Convert.ToString(GetMemberValue(component, member), CultureInfo.InvariantCulture) ?? "0";

    private static string FormatNullableMember(object component, MemberInfo member)
    {
        var value = GetMemberValue(component, member);
        if (value is null) return "";
        var underlying = Nullable.GetUnderlyingType(GetMemberType(member));
        if (underlying == typeof(float) || underlying == typeof(double) || underlying == typeof(int))
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        if (underlying == typeof(bool))
            return value is true ? "True" : "False";
        return value.ToString() ?? "";
    }

    private static string NullableBoolLabel(object? value) => value is null ? "Null" : value is true ? "True" : "False";

    private static string FormatVectorAxis(object? value, string axis)
    {
        if (value is null) return "0";
        return FormatFloat(value.GetAxis(axis));
    }

    private static string FormatColorChannel(object? value, string channel)
    {
        if (value is null) return "0";
        return FormatFloat(value.GetAxis(channel));
    }

    private static string FormatFloat(float value) => value.ToString(CultureInfo.InvariantCulture);

    private static PureEngine.Core.Color WithColorAxis(PureEngine.Core.Color color, string channel, float value) => channel switch
    {
        "R" => color with { R = value },
        "G" => color with { G = value },
        "B" => color with { B = value },
        "A" => color with { A = value },
        _ => color,
    };

    private static PureEngine.Core.Color? WithColorChannel(object? value, string channel, float parsed) =>
        value is PureEngine.Core.Color color ? WithColorAxis(color, channel, parsed) : null;

    private static Border BuildColorPreview(object? value, string automationName)
    {
        var preview = new Border
        {
            Height = 20,
            CornerRadius = new Avalonia.CornerRadius(4),
            BorderThickness = new Avalonia.Thickness(1),
            BorderBrush = UnsupportedBadgeBrush,
            Background = ToPreviewBrush(value),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        preview.SetValue(AutomationProperties.NameProperty, automationName);
        ToolTip.SetTip(preview, "Current color preview");
        return preview;
    }

    private static void RefreshColorPreview(Border preview, object? value) =>
        preview.Background = ToPreviewBrush(value);

    private static SolidColorBrush ToPreviewBrush(object? value)
    {
        if (value is not PureEngine.Core.Color color)
            return new SolidColorBrush(Avalonia.Media.Color.FromArgb(0, 0, 0, 0));
        return new SolidColorBrush(Avalonia.Media.Color.FromArgb(ToPreviewByte(color.A), ToPreviewByte(color.R), ToPreviewByte(color.G), ToPreviewByte(color.B)));
    }

    private static byte ToPreviewByte(float channel)
    {
        if (!float.IsFinite(channel)) return 0;
        return (byte)MathF.Round(Math.Clamp(channel, 0f, 1f) * 255f);
    }

    private static void RefreshColorBoxes(Panel panel, object? value)
    {
        var boxes = panel.GetVisualDescendants().OfType<TextBox>().ToList();
        string[] channels = ["R", "G", "B", "A"];
        var preview = panel.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(border => ((border.GetValue(AutomationProperties.NameProperty) as string) ?? "").EndsWith(".Preview", StringComparison.Ordinal));
        for (var i = 0; i < boxes.Count && i < channels.Length; i++)
            boxes[i].Text = FormatColorChannel(value, channels[i]);
        if (preview is not null)
            RefreshColorPreview(preview, value);
    }

    private static object? WithVectorAxis(object? value, string axis, float parsed) =>
        value switch
        {
            Vector2 vector2 => WithVector2Axis(vector2, axis, parsed),
            Vector3 vector3 => WithVector3Axis(vector3, axis, parsed),
            Vector4 vector4 => WithVector4Axis(vector4, axis, parsed),
            Quaternion quaternion => WithQuaternionAxis(quaternion, axis, parsed),
            _ => null,
        };

    private static void RefreshVectorBoxes(Panel panel, object? value)
    {
        var axes = panel.GetVisualDescendants().OfType<TextBox>().ToList();
        string[] labels = axes.Count == 2 ? ["X", "Y"] : axes.Count == 3 ? ["X", "Y", "Z"] : ["X", "Y", "Z", "W"];
        for (var i = 0; i < axes.Count && i < labels.Length; i++)
            axes[i].Text = FormatVectorAxis(value, labels[i]);
    }

    private static void RefreshTransformRows(Panel body, PureEngine.Core.Transform transform)
    {
        var rows = body.Children.OfType<Grid>().ToList();
        if (rows.Count != 3) return;
        RefreshTransformRow(rows[0], transform.LocalPosition);
        RefreshTransformRow(rows[1], transform.LocalRotation);
        RefreshTransformRow(rows[2], transform.LocalScale);
    }

    private static void RefreshTransformRow(Panel row, object vector)
    {
        // Boxes sit inside badge+box pairs, so collect them in document order instead of direct children.
        var boxes = row.GetVisualDescendants().OfType<TextBox>().ToList();
        string[] axes = boxes.Count == 3 ? ["X", "Y", "Z"] : ["X", "Y", "Z", "W"];
        for (var i = 0; i < boxes.Count && i < axes.Length; i++)
            boxes[i].Text = FormatFloat(vector.GetAxis(axes[i]));
    }

    private object? DefaultElementValue(Type elementType)
    {
        if (SceneReferenceTypes.IsSingleReference(elementType, Components.Registry))
            return null;
        if (elementType == typeof(string)) return "";
        if (elementType == typeof(int)) return 0;
        if (elementType == typeof(float)) return 0f;
        if (elementType == typeof(double)) return 0d;
        if (elementType == typeof(bool)) return false;
        if (elementType == typeof(Vector2)) return Vector2.Zero;
        if (elementType == typeof(Vector3)) return Vector3.Zero;
        if (elementType == typeof(Vector4)) return Vector4.Zero;
        if (elementType == typeof(Quaternion)) return Quaternion.Identity;
        if (elementType == typeof(PureEngine.Core.Color)) return PureEngine.Core.Color.White;
        if (elementType.IsEnum) return Enum.ToObject(elementType, 0);
        if (Nullable.GetUnderlyingType(elementType) is not null) return null;
        if (InspectorValueTypes.IsCustomInspectorObject(elementType))
        {
            try
            {
                return Activator.CreateInstance(elementType);
            }
            catch
            {
                // Pad elements that failed to create with Null so they can be rebuilt from the card Create action.
                return null;
            }
        }
        return null;
    }

    private void AddSequenceElement(object component, MemberInfo member, Type elementType)
    {
        var memberType = GetMemberType(member);
        var value = GetMemberValue(component, member);
        if (value is null) return;
        var defaultValue = DefaultElementValue(elementType);
        if (memberType.IsArray)
        {
            var array = (Array)value;
            var next = Array.CreateInstance(elementType, array.Length + 1);
            Array.Copy(array, next, array.Length);
            next.SetValue(defaultValue, array.Length);
            SetMemberValue(component, member, next);
        }
        else if (value is IList list)
        {
            list.Add(defaultValue);
            MarkEdited(component);
        }
    }

    private void RemoveSequenceElement(object component, MemberInfo member, int index)
    {
        var memberType = GetMemberType(member);
        var value = GetMemberValue(component, member);
        if (value is null) return;
        if (memberType.IsArray)
        {
            var array = (Array)value;
            if (index < 0 || index >= array.Length) return;
            var elementType = memberType.GetElementType()!;
            var next = Array.CreateInstance(elementType, array.Length - 1);
            for (var source = 0; source < array.Length; source++)
            {
                if (source == index) continue;
                var target = source > index ? source - 1 : source;
                next.SetValue(array.GetValue(source), target);
            }
            SetMemberValue(component, member, next);
        }
        else if (value is IList list)
        {
            if (index < 0 || index >= list.Count) return;
            list.RemoveAt(index);
            MarkEdited(component);
        }
    }

    private static string UniqueDictionaryKey(IDictionary dictionary)
    {
        var key = "Key";
        for (var suffix = 1; dictionary.Contains(key); suffix++)
            key = $"Key ({suffix})";
        return key;
    }

    private static string DictionaryStringValue(object component, MemberInfo member, string key)
    {
        if (GetMemberValue(component, member) is IDictionary dictionary && dictionary.Contains(key))
            return dictionary[key] as string ?? "";
        return "";
    }
}
