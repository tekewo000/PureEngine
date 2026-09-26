using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace PureEngine.Editor;

public partial class MainWindow
{
    private static Vector2 WithVector2Axis(Vector2 vector, string axis, float value) => axis switch
    {
        "X" => vector with { X = value },
        "Y" => vector with { Y = value },
        _ => vector,
    };

    private static Vector3 WithVector3Axis(Vector3 vector, string axis, float value) => axis switch
    {
        "X" => vector with { X = value },
        "Y" => vector with { Y = value },
        "Z" => vector with { Z = value },
        _ => vector,
    };

    private static Vector4 WithVector4Axis(Vector4 vector, string axis, float value) => axis switch
    {
        "X" => vector with { X = value },
        "Y" => vector with { Y = value },
        "Z" => vector with { Z = value },
        "W" => vector with { W = value },
        _ => vector,
    };

    private static Quaternion WithQuaternionAxis(Quaternion quaternion, string axis, float value) => axis switch
    {
        "X" => quaternion with { X = value },
        "Y" => quaternion with { Y = value },
        "Z" => quaternion with { Z = value },
        "W" => quaternion with { W = value },
        _ => quaternion,
    };

    private TextBox BuildSequenceStringBox(object component, MemberInfo member, int index, string automationName)
    {
        var box = new TextBox { Text = SequenceStringValue(component, member, index), MinWidth = 40, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        box.Classes.Add("inspectorField");
        box.SetValue(AutomationProperties.NameProperty, automationName);
        box.TextChanged += (_, _) =>
        {
            if (IsPlaying) return;
            SetSequenceElement(component, member, index, box.Text ?? "");
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            box.Text = SequenceStringValue(component, member, index);
            e.Handled = true;
        };
        return box;
    }

    private TextBox BuildSequenceIntBox(object component, MemberInfo member, int index, string automationName)
    {
        const string hint = "Enter an integer — Press Esc to revert";
        var box = new TextBox { Text = SequenceScalarText(component, member, index), MinWidth = 40, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        box.Classes.Add("inspectorField");
        box.Classes.Add("numericField");
        box.SetValue(AutomationProperties.NameProperty, automationName);
        ToolTip.SetTip(box, hint);
        box.TextChanged += (_, _) =>
        {
            if (IsPlaying) return;
            if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                SetSequenceElement(component, member, index, value);
                MarkInvalid(box, null, hint);
            }
            else
            {
                MarkInvalid(box, "Enter an integer");
            }
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            box.Text = SequenceScalarText(component, member, index);
            e.Handled = true;
        };
        return box;
    }

    private TextBox BuildSequenceFloatBox(object component, MemberInfo member, int index, string automationName, bool isDouble)
    {
        const string hint = "Enter a number — Press Esc to revert";
        var box = new TextBox { Text = SequenceScalarText(component, member, index), MinWidth = 40, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        box.Classes.Add("inspectorField");
        box.Classes.Add("numericField");
        box.SetValue(AutomationProperties.NameProperty, automationName);
        ToolTip.SetTip(box, hint);
        box.TextChanged += (_, _) =>
        {
            if (IsPlaying) return;
            if (isDouble)
            {
                if (double.TryParse(box.Text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value))
                {
                    SetSequenceElement(component, member, index, value);
                    MarkInvalid(box, null, hint);
                }
                else
                {
                    MarkInvalid(box, "Enter a number");
                }
                return;
            }
            if (float.TryParse(box.Text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var single) && float.IsFinite(single))
            {
                SetSequenceElement(component, member, index, single);
                MarkInvalid(box, null, hint);
            }
            else
            {
                MarkInvalid(box, "Enter a number");
            }
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            box.Text = SequenceScalarText(component, member, index);
            e.Handled = true;
        };
        return box;
    }

    private CheckBox BuildSequenceBoolBox(object component, MemberInfo member, int index, string automationName)
    {
        var initial = SequenceElement(component, member, index) is true;
        var check = new CheckBox { IsChecked = initial, Content = initial ? "True" : "False", VerticalAlignment = VerticalAlignment.Center };
        check.Classes.Add("inspectorCheck");
        check.SetValue(AutomationProperties.NameProperty, automationName);
        check.IsCheckedChanged += (_, _) =>
        {
            if (IsPlaying) return;
            var value = check.IsChecked == true;
            check.Content = value ? "True" : "False";
            SetSequenceElement(component, member, index, value);
        };
        return check;
    }

    private Grid BuildSequenceVectorRow(object component, MemberInfo member, Type elementType, int index, string automationName)
    {
        var grid = new Grid { ColumnSpacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        string[] axes = elementType == typeof(Vector2) ? ["X", "Y"] : elementType == typeof(Vector3) ? ["X", "Y", "Z"] : ["X", "Y", "Z", "W"];
        foreach (var _ in axes)
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        var column = 0;
        foreach (var axis in axes)
        {
            var box = new TextBox { Text = SequenceVectorAxisText(component, member, index, axis), MinWidth = 40, FontSize = 12, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
            box.Classes.Add("inspectorField");
            box.SetValue(AutomationProperties.NameProperty, $"{automationName}.{axis}");
            const string hint = "Enter a number — Press Esc to revert";
            ToolTip.SetTip(box, hint);
            var captured = axis;
            box.TextChanged += (_, _) =>
            {
                if (IsPlaying) return;
                if (!float.TryParse(box.Text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value) || !float.IsFinite(value))
                {
                    MarkInvalid(box, "Enter a number");
                    return;
                }
                var current = SequenceElement(component, member, index);
                var updated = WithVectorAxis(current, captured, value);
                if (updated is null)
                    MarkInvalid(box, "Enter a number");
                else
                {
                    SetSequenceElement(component, member, index, updated);
                    MarkInvalid(box, null, hint);
                }
            };
            box.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape) return;
                box.Text = SequenceVectorAxisText(component, member, index, captured);
                e.Handled = true;
            };
            Grid.SetColumn(box, column++);
            grid.Children.Add(box);
        }
        return grid;
    }

    private Grid BuildSequenceColorRow(object component, MemberInfo member, int index, string automationName)
    {
        var grid = new Grid { ColumnSpacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        string[] channels = ["R", "G", "B", "A"];
        foreach (var _ in channels)
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        var column = 0;
        foreach (var channel in channels)
        {
            var box = new TextBox { Text = SequenceColorChannelText(component, member, index, channel), MinWidth = 40, FontSize = 12, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
            box.Classes.Add("inspectorField");
            box.SetValue(AutomationProperties.NameProperty, $"{automationName}.{channel}");
            const string hint = "Enter a number — Press Esc to revert";
            ToolTip.SetTip(box, hint);
            var captured = channel;
            box.TextChanged += (_, _) =>
            {
                if (IsPlaying) return;
                if (!float.TryParse(box.Text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value) || !float.IsFinite(value))
                {
                    MarkInvalid(box, "Enter a number");
                    return;
                }
                var current = SequenceElement(component, member, index);
                var updated = WithColorChannel(current, captured, value);
                if (updated is null)
                    MarkInvalid(box, "Enter a number");
                else
                {
                    SetSequenceElement(component, member, index, updated);
                    MarkInvalid(box, null, hint);
                }
            };
            box.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape) return;
                box.Text = SequenceColorChannelText(component, member, index, captured);
                e.Handled = true;
            };
            Grid.SetColumn(box, column++);
            grid.Children.Add(box);
        }
        return grid;
    }

    private TextBox BuildSequenceNullableBox(object component, MemberInfo member, Type _, Type underlying, int index, string automationName)
    {
        const string hint = "Empty = null — Press Esc to revert";
        var box = new TextBox { Text = SequenceScalarText(component, member, index), MinWidth = 40, PlaceholderText = "Null", HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        box.Classes.Add("inspectorField");
        if (underlying == typeof(int) || underlying == typeof(float) || underlying == typeof(double))
            box.Classes.Add("numericField");
        box.SetValue(AutomationProperties.NameProperty, automationName);
        ToolTip.SetTip(box, hint);
        box.TextChanged += (_, _) =>
        {
            if (IsPlaying) return;
            var text = (box.Text ?? "").Trim();
            if (text.Length == 0)
            {
                SetSequenceElement(component, member, index, null);
                MarkInvalid(box, null, hint);
                return;
            }
            var parsed = ParseNullableScalar(text, underlying);
            if (parsed is null && underlying != typeof(string))
                MarkInvalid(box, underlying == typeof(int) ? "Enter an integer" : "Enter a number");
            else
            {
                SetSequenceElement(component, member, index, parsed ?? (object?)text);
                MarkInvalid(box, null, hint);
            }
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            box.Text = SequenceScalarText(component, member, index);
            e.Handled = true;
        };
        return box;
    }

    private TextBox BuildDictionaryNumericBox(object component, MemberInfo member, string key, string automationName, bool isDouble, bool isInt)
    {
        const string hint = "Enter a number — Press Esc to revert";
        var intHint = "Enter an integer — Press Esc to revert";
        var box = new TextBox { Text = DictionaryScalarText(component, member, key), MinWidth = 40, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        box.Classes.Add("inspectorField");
        box.Classes.Add("numericField");
        box.SetValue(AutomationProperties.NameProperty, automationName);
        ToolTip.SetTip(box, isInt ? intHint : hint);
        box.TextChanged += (_, _) =>
        {
            if (IsPlaying) return;
            if (GetMemberValue(component, member) is not IDictionary dictionary || !dictionary.Contains(key)) return;
            if (isInt)
            {
                if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                {
                    dictionary[key] = integer;
                    MarkEdited(component);
                    MarkInvalid(box, null, intHint);
                }
                else
                {
                    MarkInvalid(box, "Enter an integer");
                }
                return;
            }
            if (isDouble)
            {
                if (double.TryParse(box.Text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var real) && double.IsFinite(real))
                {
                    dictionary[key] = real;
                    MarkEdited(component);
                    MarkInvalid(box, null, hint);
                }
                else
                {
                    MarkInvalid(box, "Enter a number");
                }
                return;
            }
            if (float.TryParse(box.Text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var single) && float.IsFinite(single))
            {
                dictionary[key] = single;
                MarkEdited(component);
                MarkInvalid(box, null, hint);
            }
            else
            {
                MarkInvalid(box, "Enter a number");
            }
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            box.Text = DictionaryScalarText(component, member, key);
            e.Handled = true;
        };
        return box;
    }

    private Grid BuildDictionaryVectorRow(object component, MemberInfo member, Type valueType, string key, string automationName)
    {
        var grid = new Grid { ColumnSpacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        string[] axes = valueType == typeof(Vector2) ? ["X", "Y"] : valueType == typeof(Vector3) ? ["X", "Y", "Z"] : ["X", "Y", "Z", "W"];
        foreach (var _ in axes)
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        var column = 0;
        foreach (var axis in axes)
        {
            var box = new TextBox { Text = DictionaryVectorAxisText(component, member, key, axis), MinWidth = 40, FontSize = 12, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
            box.Classes.Add("inspectorField");
            box.Classes.Add("numericField");
            box.SetValue(AutomationProperties.NameProperty, $"{automationName}.{axis}");
            const string hint = "Enter a number — Press Esc to revert";
            ToolTip.SetTip(box, hint);
            var captured = axis;
            box.TextChanged += (_, _) =>
            {
                if (IsPlaying) return;
                if (!float.TryParse(box.Text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value) || !float.IsFinite(value))
                {
                    MarkInvalid(box, "Enter a number");
                    return;
                }
                if (GetMemberValue(component, member) is not IDictionary dictionary || !dictionary.Contains(key)) return;
                var updated = WithVectorAxis(dictionary[key], captured, value);
                if (updated is null)
                    MarkInvalid(box, "Enter a number");
                else
                {
                    dictionary[key] = updated;
                    MarkEdited(component);
                    MarkInvalid(box, null, hint);
                }
            };
            box.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape) return;
                box.Text = DictionaryVectorAxisText(component, member, key, captured);
                e.Handled = true;
            };
            Grid.SetColumn(box, column++);
            grid.Children.Add(box);
        }
        return grid;
    }

    private Grid BuildDictionaryColorRow(object component, MemberInfo member, string key, string automationName)
    {
        var grid = new Grid { ColumnSpacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        string[] channels = ["R", "G", "B", "A"];
        foreach (var _ in channels)
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        var column = 0;
        foreach (var channel in channels)
        {
            var box = new TextBox { Text = DictionaryColorChannelText(component, member, key, channel), MinWidth = 40, FontSize = 12, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
            box.Classes.Add("inspectorField");
            box.Classes.Add("numericField");
            box.SetValue(AutomationProperties.NameProperty, $"{automationName}.{channel}");
            const string hint = "Enter a number — Press Esc to revert";
            ToolTip.SetTip(box, hint);
            var captured = channel;
            box.TextChanged += (_, _) =>
            {
                if (IsPlaying) return;
                if (!float.TryParse(box.Text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value) || !float.IsFinite(value))
                {
                    MarkInvalid(box, "Enter a number");
                    return;
                }
                if (GetMemberValue(component, member) is not IDictionary dictionary || !dictionary.Contains(key)) return;
                var updated = WithColorChannel(dictionary[key], captured, value);
                if (updated is null)
                    MarkInvalid(box, "Enter a number");
                else
                {
                    dictionary[key] = updated;
                    MarkEdited(component);
                    MarkInvalid(box, null, hint);
                }
            };
            box.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape) return;
                box.Text = DictionaryColorChannelText(component, member, key, captured);
                e.Handled = true;
            };
            Grid.SetColumn(box, column++);
            grid.Children.Add(box);
        }
        return grid;
    }

    private TextBox BuildDictionaryNullableBox(object component, MemberInfo member, Type _, Type underlying, string key, string automationName)
    {
        const string hint = "Empty = null — Press Esc to revert";
        var box = new TextBox { Text = DictionaryScalarText(component, member, key), MinWidth = 40, PlaceholderText = "Null", HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        box.Classes.Add("inspectorField");
        if (underlying == typeof(int) || underlying == typeof(float) || underlying == typeof(double))
            box.Classes.Add("numericField");
        box.SetValue(AutomationProperties.NameProperty, automationName);
        ToolTip.SetTip(box, hint);
        box.TextChanged += (_, _) =>
        {
            if (IsPlaying) return;
            if (GetMemberValue(component, member) is not IDictionary dictionary || !dictionary.Contains(key)) return;
            var text = (box.Text ?? "").Trim();
            if (text.Length == 0)
            {
                dictionary[key] = null;
                MarkEdited(component);
                MarkInvalid(box, null, hint);
                return;
            }
            var parsed = ParseNullableScalar(text, underlying);
            if (parsed is null)
                MarkInvalid(box, underlying == typeof(int) ? "Enter an integer" : "Enter a number");
            else
            {
                dictionary[key] = parsed;
                MarkEdited(component);
                MarkInvalid(box, null, hint);
            }
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            box.Text = DictionaryScalarText(component, member, key);
            e.Handled = true;
        };
        return box;
    }

    private static object? ParseNullableScalar(string text, Type underlying)
    {
        if (underlying == typeof(int) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            return integer;
        if (underlying == typeof(float) && float.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var single) && float.IsFinite(single))
            return single;
        if (underlying == typeof(double) && double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var real) && double.IsFinite(real))
            return real;
        if (underlying == typeof(bool) && bool.TryParse(text, out var boolean))
            return boolean;
        return null;
    }

    private static object? SequenceElement(object component, MemberInfo member, int index)
    {
        var value = GetMemberValue(component, member);
        if (value is Array array && index >= 0 && index < array.Length)
            return array.GetValue(index);
        if (value is IList list && index >= 0 && index < list.Count)
            return list[index];
        return null;
    }

    private void SetSequenceElement(object component, MemberInfo member, int index, object? element)
    {
        if (IsPlaying) return;
        var value = GetMemberValue(component, member);
        if (value is Array array)
        {
            if (index < 0 || index >= array.Length) return;
            array.SetValue(element, index);
            MarkEdited(component);
        }
        else if (value is IList list)
        {
            if (index < 0 || index >= list.Count) return;
            list[index] = element;
            MarkEdited(component);
        }
    }

    private static string SequenceStringValue(object component, MemberInfo member, int index) =>
        SequenceElement(component, member, index) as string ?? "";

    private static string SequenceScalarText(object component, MemberInfo member, int index)
    {
        var element = SequenceElement(component, member, index);
        if (element is null) return "";
        if (element is float single) return single.ToString(CultureInfo.InvariantCulture);
        if (element is double real) return real.ToString(CultureInfo.InvariantCulture);
        return element.ToString() ?? "";
    }

    private static string SequenceColorChannelText(object component, MemberInfo member, int index, string channel) =>
        FormatColorChannel(SequenceElement(component, member, index), channel);

    private static string DictionaryColorChannelText(object component, MemberInfo member, string key, string channel)
    {
        if (GetMemberValue(component, member) is not IDictionary dictionary || !dictionary.Contains(key))
            return "0";
        return FormatColorChannel(dictionary[key], channel);
    }

    private static string SequenceVectorAxisText(object component, MemberInfo member, int index, string axis) =>
        FormatVectorAxis(SequenceElement(component, member, index), axis);

    private static string DictionaryScalarText(object component, MemberInfo member, string key)
    {
        if (GetMemberValue(component, member) is not IDictionary dictionary || !dictionary.Contains(key))
            return "";
        var value = dictionary[key];
        if (value is null) return "";
        if (value is float single) return single.ToString(CultureInfo.InvariantCulture);
        if (value is double real) return real.ToString(CultureInfo.InvariantCulture);
        return value.ToString() ?? "";
    }

    private static string DictionaryVectorAxisText(object component, MemberInfo member, string key, string axis)
    {
        if (GetMemberValue(component, member) is not IDictionary dictionary || !dictionary.Contains(key))
            return "0";
        return FormatVectorAxis(dictionary[key], axis);
    }

    private Control BuildSequenceEnumBox(object component, MemberInfo member, Type elementType, int index, string automationName)
    {
        if (elementType.IsDefined(typeof(FlagsAttribute), inherit: false))
            return BuildFlagsEditor(component, member, automationName, elementType,
                getCurrent: () => SequenceElement(component, member, index) ?? Enum.ToObject(elementType, 0),
                setCurrent: value => SetSequenceElement(component, member, index, value));
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        combo.Classes.Add("inspectorCombo");
        combo.SetValue(AutomationProperties.NameProperty, automationName);
        var items = Enum.GetValues(elementType).Cast<object>().ToList();
        var current = SequenceElement(component, member, index);
        if (current is not null && !items.Contains(current))
            items.Add(current);
        combo.ItemsSource = items;
        combo.SelectedItem = current;
        combo.SelectionChanged += (_, _) =>
        {
            if (IsPlaying) return;
            if (combo.SelectedItem is null) return;
            SetSequenceElement(component, member, index, combo.SelectedItem);
        };
        return combo;
    }

    private Control BuildSequenceNullableEnumBox(object component, MemberInfo member, Type _, Type underlying, int index, string automationName)
    {
        if (underlying.IsDefined(typeof(FlagsAttribute), inherit: false))
        {
            var root = new StackPanel { Spacing = 4 };
            var nullRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            nullRow.Children.Add(new TextBlock { Classes = { "memberType" }, Text = "Null", VerticalAlignment = VerticalAlignment.Center });
            var create = BuildHeaderIconButton("Icon.Compose", $"{automationName}.Create");
            ToolTip.SetTip(create, "Create a flags value.");
            nullRow.Children.Add(create);
            var body = new StackPanel { Spacing = 4 };
            var clear = BuildHeaderIconButton("Icon.Dismiss", $"{automationName}.Null");
            ToolTip.SetTip(clear, "Set to null.");
            root.Children.Add(nullRow);
            root.Children.Add(body);
            root.Children.Add(clear);
            void refresh()
            {
                var value = SequenceElement(component, member, index);
                nullRow.IsVisible = value is null;
                body.IsVisible = value is not null;
                clear.IsVisible = value is not null;
                body.Children.Clear();
                if (value is not null)
                    body.Children.Add(BuildFlagsEditor(component, member, automationName, underlying,
                        getCurrent: () => SequenceElement(component, member, index) ?? Enum.ToObject(underlying, 0),
                        setCurrent: v => SetSequenceElement(component, member, index, v)));
            }
            create.Click += (_, _) =>
            {
                if (IsPlaying) return;
                SetSequenceElement(component, member, index, Enum.ToObject(underlying, 0));
                refresh();
            };
            clear.Click += (_, _) =>
            {
                if (IsPlaying) return;
                SetSequenceElement(component, member, index, null);
                refresh();
            };
            refresh();
            return root;
        }
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        combo.Classes.Add("inspectorCombo");
        combo.SetValue(AutomationProperties.NameProperty, automationName);
        var names = Enum.GetNames(underlying).ToList();
        var current = SequenceElement(component, member, index)?.ToString();
        var items = new List<string> { "Null" };
        items.AddRange(names);
        if (current is not null && !items.Contains(current))
            items.Add(current);
        combo.ItemsSource = items;
        combo.SelectedItem = current ?? "Null";
        combo.SelectionChanged += (_, _) =>
        {
            if (IsPlaying) return;
            if (combo.SelectedItem is not string name) return;
            if (name == "Null")
                SetSequenceElement(component, member, index, null);
            else if (Enum.TryParse(underlying, name, ignoreCase: true, out var parsed))
                SetSequenceElement(component, member, index, parsed);
        };
        return combo;
    }

    private Control BuildDictionaryEnumBox(object component, MemberInfo member, Type valueType, string key, string automationName)
    {
        if (valueType.IsDefined(typeof(FlagsAttribute), inherit: false))
            return BuildFlagsEditor(component, member, automationName, valueType,
                getCurrent: () => DictionaryEnumValue(component, member, key) ?? Enum.ToObject(valueType, 0),
                setCurrent: value =>
                {
                    if (IsPlaying) return;
                    if (GetMemberValue(component, member) is not IDictionary dictionary || !dictionary.Contains(key)) return;
                    dictionary[key] = value;
                    MarkEdited(component);
                });
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        combo.Classes.Add("inspectorCombo");
        combo.SetValue(AutomationProperties.NameProperty, automationName);
        var items = Enum.GetValues(valueType).Cast<object>().ToList();
        var current = DictionaryEnumValue(component, member, key);
        if (current is not null && !items.Contains(current))
            items.Add(current);
        combo.ItemsSource = items;
        combo.SelectedItem = current;
        combo.SelectionChanged += (_, _) =>
        {
            if (IsPlaying) return;
            if (combo.SelectedItem is null) return;
            if (GetMemberValue(component, member) is not IDictionary dictionary || !dictionary.Contains(key)) return;
            dictionary[key] = combo.SelectedItem;
            MarkEdited(component);
        };
        return combo;
    }

    private Control BuildDictionaryNullableEnumBox(object component, MemberInfo member, Type _, Type underlying, string key, string automationName)
    {
        if (underlying.IsDefined(typeof(FlagsAttribute), inherit: false))
        {
            var root = new StackPanel { Spacing = 4 };
            var nullRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            nullRow.Children.Add(new TextBlock { Classes = { "memberType" }, Text = "Null", VerticalAlignment = VerticalAlignment.Center });
            var create = BuildHeaderIconButton("Icon.Compose", $"{automationName}.Create");
            ToolTip.SetTip(create, "Create a flags value.");
            nullRow.Children.Add(create);
            var body = new StackPanel { Spacing = 4 };
            var clear = BuildHeaderIconButton("Icon.Dismiss", $"{automationName}.Null");
            ToolTip.SetTip(clear, "Set to null.");
            root.Children.Add(nullRow);
            root.Children.Add(body);
            root.Children.Add(clear);
            void refresh()
            {
                var value = DictionaryEnumValue(component, member, key);
                nullRow.IsVisible = value is null;
                body.IsVisible = value is not null;
                clear.IsVisible = value is not null;
                body.Children.Clear();
                if (value is not null)
                    body.Children.Add(BuildFlagsEditor(component, member, automationName, underlying,
                        getCurrent: () => DictionaryEnumValue(component, member, key) ?? Enum.ToObject(underlying, 0),
                        setCurrent: v =>
                        {
                            if (IsPlaying) return;
                            if (GetMemberValue(component, member) is not IDictionary dictionary || !dictionary.Contains(key)) return;
                            dictionary[key] = v;
                            MarkEdited(component);
                        }));
            }
            create.Click += (_, _) =>
            {
                if (IsPlaying) return;
                if (GetMemberValue(component, member) is not IDictionary dictionary || !dictionary.Contains(key)) return;
                dictionary[key] = Enum.ToObject(underlying, 0);
                MarkEdited(component);
                refresh();
            };
            clear.Click += (_, _) =>
            {
                if (IsPlaying) return;
                if (GetMemberValue(component, member) is not IDictionary dictionary || !dictionary.Contains(key)) return;
                dictionary[key] = null;
                MarkEdited(component);
                refresh();
            };
            refresh();
            return root;
        }
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        combo.Classes.Add("inspectorCombo");
        combo.SetValue(AutomationProperties.NameProperty, automationName);
        var names = Enum.GetNames(underlying).ToList();
        var current = DictionaryEnumValue(component, member, key)?.ToString();
        var items = new List<string> { "Null" };
        items.AddRange(names);
        if (current is not null && !items.Contains(current))
            items.Add(current);
        combo.ItemsSource = items;
        combo.SelectedItem = current ?? "Null";
        combo.SelectionChanged += (_, _) =>
        {
            if (IsPlaying) return;
            if (combo.SelectedItem is not string name) return;
            if (GetMemberValue(component, member) is not IDictionary dictionary || !dictionary.Contains(key)) return;
            if (name == "Null")
                dictionary[key] = null;
            else if (Enum.TryParse(underlying, name, ignoreCase: true, out var parsed))
                dictionary[key] = parsed;
            else
                return;
            MarkEdited(component);
        };
        return combo;
    }

    private static object? DictionaryEnumValue(object component, MemberInfo member, string key)
    {
        if (GetMemberValue(component, member) is IDictionary dictionary && dictionary.Contains(key))
            return dictionary[key];
        return null;
    }
}
