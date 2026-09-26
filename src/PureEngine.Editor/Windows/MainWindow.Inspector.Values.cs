using System.Collections;
using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using PureEngine.Core;

namespace PureEngine.Editor;

public partial class MainWindow
{
    private const int InspectorPageSize = 32;

    private static Avalonia.Controls.Button BuildValueCommandButton(string content, string name)
    {
        var button = new Avalonia.Controls.Button
        {
            Content = content,
            FontSize = 11,
            Padding = new Avalonia.Thickness(8, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.SetValue(AutomationProperties.NameProperty, name);
        return button;
    }

    private static bool IsValueContainer(Type type) => type.IsArray || type.IsGenericType
        && (type.GetGenericTypeDefinition() == typeof(List<>) || type.GetGenericTypeDefinition() == typeof(Dictionary<,>));

    private static bool IsCompositeValue(Type type) => IsValueContainer(type)
        || InspectorValueTypes.IsCustomInspectorObject(Nullable.GetUnderlyingType(type) ?? type);

    private void CommitBoundValue(InspectorValueBinding binding, object? value)
    {
        if (IsPlaying) return;
        binding.Write(value);
        MarkEdited(binding.Owner);
    }

    private void ClearBoundReferences(Guid? ownerId, string path)
    {
        if (ownerId is { } id) Documents.Current.Current.References.RemovePathsForMember(id, path);
    }

    private Control BuildBoundValueEditor(InspectorValueBinding binding, string name, Guid? ownerId, string path, bool element = false)
    {
        var type = binding.ValueType;
        if (ShouldShowReferenceEditorFor(type, binding.Owner))
        {
            if (ownerId is not { } id) return UnsupportedBadge(type);
            return BuildSingleReferenceEditor(binding.Read, value =>
            {
                CommitBoundValue(binding, value);
                ClearBoundReferences(id, path);
            }, type, id, path, name);
        }
        if (IsValueContainer(type))
        {
            if (binding.Read() is Array array
                && Enumerable.Range(0, array.Rank).Any(dimension => array.GetLowerBound(dimension) != 0))
                return new TextBlock { Text = "Non-zero array bounds are unsupported.", Classes = { "memberType" } };
            // New nested containers start folded; expanding creates only one bounded page.
            if (element) ViewModel.Inspector.CollapsedMembers.TryAdd(name, true);
            return BuildBoundContainer(binding, name, ownerId, path);
        }
        var underlying = Nullable.GetUnderlyingType(type);
        if (InspectorValueTypes.IsCustomInspectorObject(underlying ?? type)
            || SceneReferenceTypes.ContainsReference(underlying ?? type, Components.Registry))
            return BuildBoundObject(binding, name, ownerId, path, element);
        if (element && type == typeof(Color))
            return BuildBoundColor(binding, name);
        return BuildMemberEditor(binding, binding.ValueMember, name);
    }

    private Grid BuildBoundColor(InspectorValueBinding binding, string name)
    {
        string[] channels = ["R", "G", "B", "A"];
        var boxes = channels.Select(channel =>
        {
            var box = new TextBox { Text = FormatColorChannel(binding.Read(), channel) };
            box.Classes.Add("inspectorField");
            box.SetValue(AutomationProperties.NameProperty, $"{name}.{channel}");
            box.TextChanged += (_, _) =>
            {
                if (IsPlaying) return;
                if (!float.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !float.IsFinite(value))
                    MarkInvalid(box, "Enter a number");
                else
                {
                    CommitBoundValue(binding, WithColorChannel(binding.Read(), channel, value));
                    MarkInvalid(box, null);
                }
            };
            box.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape) return;
                box.Text = FormatColorChannel(binding.Read(), channel);
                e.Handled = true;
            };
            return box;
        }).ToList();
        return BuildAxisGrid(channels, boxes);
    }

    private StackPanel BuildBoundObject(InspectorValueBinding binding, string name, Guid? ownerId, string path, bool deferContainers)
    {
        var type = Nullable.GetUnderlyingType(binding.ValueType) ?? binding.ValueType;
        var root = new StackPanel { Spacing = 6 };
        var body = new StackPanel { Spacing = 4 };
        var count = new TextBlock { Classes = { "memberType" } };
        var create = BuildHeaderIconButton("Icon.Compose", $"{name}.Create");
        ToolTip.SetTip(create, "Create a new instance.");
        var clear = BuildHeaderIconButton("Icon.Dismiss", $"{name}.Null");
        ToolTip.SetTip(clear, "Set to null.");
        Action refresh = () => { };
        var toggle = BuildCollapseToggle($"{name}.Collapse", name, ViewModel.Inspector.CollapsedMembers, expanded =>
        {
            body.IsVisible = expanded;
            if (expanded && body.Children.Count == 0) refresh();
        });
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        left.Children.Add(toggle);
        left.Children.Add(count);
        root.Children.Add(BuildSplitHeader(left, create, clear));
        root.Children.Add(body);
        refresh = () =>
        {
            ClearBoundBody(body);
            var value = binding.Read();
            var members = ComponentSchema.GetInspectorMembers(type);
            count.Text = value is null ? "Null" : $"{members.Count} fields";
            create.IsVisible = value is null;
            clear.IsVisible = value is not null && (!binding.ValueType.IsValueType || Nullable.GetUnderlyingType(binding.ValueType) is not null);
            toggle.IsVisible = value is not null;
            body.IsVisible = !ViewModel.Inspector.CollapsedMembers.GetValueOrDefault(name);
            if (value is null || ViewModel.Inspector.CollapsedMembers.GetValueOrDefault(name)) return;
            foreach (var member in members)
            {
                var child = binding.Member(member);
                body.Children.Add(BuildLabeledEditorRow(member.Name, $"{member.Name} : {FriendlyTypeName(child.ValueType)}",
                    FriendlyTypeName(child.ValueType), BuildBoundValueEditor(child, $"{name}.{member.Name}", ownerId, $"{path}.{member.Name}", deferContainers)));
            }
        };
        create.Click += (_, _) =>
        {
            if (IsPlaying) return;
            try
            {
                CommitBoundValue(binding, Activator.CreateInstance(type));
            }
            catch (Exception error)
            {
                SetFileStatus(error.ToString(), true);
                return;
            }
            refresh();
        };
        clear.Click += (_, _) =>
        {
            if (IsPlaying) return;
            CommitBoundValue(binding, null);
            ClearBoundReferences(ownerId, path);
            refresh();
        };
        refresh();
        return root;
    }

    private void ClearBoundBody(StackPanel body)
    {
        foreach (var box in body.GetVisualDescendants().OfType<TextBox>()) ClearInputError(box);
        body.Children.Clear();
        UpdateErrorBadge();
    }

    private StackPanel BuildBoundContainer(InspectorValueBinding binding, string name, Guid? ownerId, string path)
    {
        var type = binding.ValueType;
        var dictionaryType = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>);
        var elementType = type.IsArray ? type.GetElementType()! : type.GetGenericArguments()[dictionaryType ? 1 : 0];
        var multidimensional = type.IsArray && type.GetArrayRank() > 1;
        var root = new StackPanel { Spacing = 6 };
        var body = new StackPanel { Spacing = 4 };
        var count = new TextBlock { Classes = { "memberType" } };
        var add = BuildHeaderIconButton("Icon.AddSquare", $"{name}.Add");
        ToolTip.SetTip(add, "Add an entry.");
        var clear = BuildHeaderIconButton("Icon.Delete", $"{name}.Clear");
        ToolTip.SetTip(clear, "Remove all entries; keep an empty collection.");
        var setNull = BuildHeaderIconButton("Icon.Dismiss", $"{name}.Null");
        ToolTip.SetTip(setNull, "Set the collection to null.");
        var create = BuildHeaderIconButton("Icon.Compose", $"{name}.Create");
        ToolTip.SetTip(create, "Create an empty collection.");
        var page = 0;
        Action refresh = () => { };
        var toggle = BuildCollapseToggle($"{name}.Collapse", name, ViewModel.Inspector.CollapsedMembers, expanded =>
        {
            body.IsVisible = expanded;
            if (expanded && body.Children.Count == 0) refresh();
        });
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        left.Children.Add(toggle);
        left.Children.Add(count);
        root.Children.Add(BuildSplitHeader(left, add, create, setNull, clear));
        root.Children.Add(body);
        refresh = () =>
        {
            ClearBoundBody(body);
            var current = binding.Read();
            create.IsVisible = current is null;
            add.IsVisible = current is not null && !multidimensional;
            clear.IsVisible = setNull.IsVisible = toggle.IsVisible = current is not null;
            var size = current is ICollection collection ? collection.Count : 0;
            count.Text = current is null ? "Null" : multidimensional && current is Array shaped
                ? $"{string.Join(" × ", Enumerable.Range(0, shaped.Rank).Select(shaped.GetLength))} ({size} items)"
                : $"{size} {(dictionaryType ? "entries" : "items")}";
            body.IsVisible = !ViewModel.Inspector.CollapsedMembers.GetValueOrDefault(name);
            if (current is null || ViewModel.Inspector.CollapsedMembers.GetValueOrDefault(name)) return;
            if (multidimensional) body.Children.Add(BuildBoundArrayShape(binding, name, ownerId, path, refresh));
            page = Math.Clamp(page, 0, Math.Max(0, (size - 1) / InspectorPageSize));
            var start = page * InspectorPageSize;
            if (current is IDictionary dictionary)
            {
                var keys = dictionary.Keys.Cast<string>().Order(StringComparer.Ordinal).ToList();
                for (var index = start; index < Math.Min(size, start + InspectorPageSize); index++)
                    body.Children.Add(BuildBoundDictionaryRow(binding, elementType, keys[index], index, name, ownerId, path, refresh));
            }
            else
            {
                for (var index = start; index < Math.Min(size, start + InspectorPageSize); index++)
                {
                    var slot = index;
                    var indices = current is Array array ? InspectorArrayShape.GetIndices(array, index) : [index];
                    var suffix = $"[{string.Join(",", indices)}]";
                    var editor = BuildBoundValueEditor(binding.Element(elementType, indices), $"{name}{suffix}", ownerId, $"{path}{suffix}", element: true);
                    var row = new Grid { ColumnDefinitions = [with("Auto,*,Auto")], ColumnSpacing = 6 };
                    row.Children.Add(new TextBlock { Text = suffix, Classes = { "memberType" } });
                    Grid.SetColumn(editor, 1);
                    row.Children.Add(editor);
                    AttachEditorDropHandlers(row, editor);
                    if (!multidimensional)
                    {
                        var remove = BuildRemoveButton($"{name}.Remove[{index}]");
                        Grid.SetColumn(remove, 2);
                        row.Children.Add(remove);
                        remove.Click += (_, _) =>
                        {
                            if (IsPlaying) return;
                            RemoveBoundSequenceElement(binding, elementType, slot);
                            if (ownerId is { } id) Documents.Current.Current.References.RemoveSequenceElement(id, path, slot);
                            refresh();
                        };
                    }
                    body.Children.Add(row);
                }
            }
            if (size > InspectorPageSize)
            {
                var previous = BuildValueCommandButton("Previous", $"{name}.Previous");
                var next = BuildValueCommandButton("Next", $"{name}.Next");
                previous.IsEnabled = page > 0;
                next.IsEnabled = start + InspectorPageSize < size;
                previous.Click += (_, _) => { page--; refresh(); };
                next.Click += (_, _) => { page++; refresh(); };
                body.Children.Add(BuildSplitHeader(new TextBlock { Text = $"{start + 1}–{Math.Min(size, start + InspectorPageSize)} of {size}" }, previous, next));
            }
        };
        object empty() => type.IsArray ? Array.CreateInstance(elementType, new int[type.GetArrayRank()]) : Activator.CreateInstance(type)!;
        create.Click += (_, _) => { if (IsPlaying) return; CommitBoundValue(binding, empty()); refresh(); };
        setNull.Click += (_, _) => { if (IsPlaying) return; CommitBoundValue(binding, null); ClearBoundReferences(ownerId, path); refresh(); };
        clear.Click += (_, _) => { if (IsPlaying) return; CommitBoundValue(binding, empty()); ClearBoundReferences(ownerId, path); refresh(); };
        add.Click += (_, _) =>
        {
            if (IsPlaying) return;
            var current = binding.Read();
            object? defaultValue;
            try
            {
                defaultValue = DefaultElementValue(elementType);
            }
            catch (Exception error)
            {
                SetFileStatus(error.ToString(), true);
                return;
            }
            if (current is IDictionary dictionary) dictionary.Add(UniqueDictionaryKey(dictionary), defaultValue);
            else if (current is Array array)
            {
                if (array.Length >= 1_048_576) return;
                var replacement = Array.CreateInstance(elementType, array.Length + 1);
                Array.Copy(array, replacement, array.Length);
                replacement.SetValue(defaultValue, array.Length);
                current = replacement;
            }
            else if (current is IList list) list.Add(defaultValue);
            CommitBoundValue(binding, current);
            refresh();
        };
        refresh();
        return root;
    }

    private void RemoveBoundSequenceElement(InspectorValueBinding binding, Type elementType, int index)
    {
        var current = binding.Read();
        if (current is Array array)
        {
            var replacement = Array.CreateInstance(elementType, array.Length - 1);
            Array.Copy(array, 0, replacement, 0, index);
            Array.Copy(array, index + 1, replacement, index, array.Length - index - 1);
            current = replacement;
        }
        else if (current is IList list) list.RemoveAt(index);
        CommitBoundValue(binding, current);
    }

    private Grid BuildBoundDictionaryRow(InspectorValueBinding binding, Type type, string key, int index, string name, Guid? ownerId, string path, Action refresh)
    {
        var row = new Grid { ColumnDefinitions = [with("*,*,Auto")], ColumnSpacing = 6 };
        var box = new TextBox { Text = key, Classes = { "inspectorField" } };
        box.SetValue(AutomationProperties.NameProperty, $"{name}.Key[{index}]");
        ToolTip.SetTip(box, "Unique non-empty key — Enter or leave the field to apply; Esc to revert");
        var active = true;
        box.TextChanged += (_, _) =>
        {
            if (IsPlaying || !active || binding.Read() is not IDictionary dictionary) return;
            var next = box.Text ?? "";
            if (next == key) { MarkInvalid(box, null); return; }
            MarkInvalid(box, next.Length == 0 || dictionary.Contains(next)
                ? "Enter a unique non-empty key" : "Press Enter or leave the field to apply the key");
        };
        void commitKey()
        {
            if (IsPlaying || !active || TopLevel.GetTopLevel(box) is null
                || binding.Read() is not IDictionary dictionary || !dictionary.Contains(key)) return;
            var next = box.Text ?? "";
            if (next == key) { MarkInvalid(box, null); return; }
            if (next.Length == 0 || dictionary.Contains(next))
            {
                MarkInvalid(box, "Enter a unique non-empty key");
                return;
            }
            // Disable the old row before rebuilding; focus loss and queued input must not rename it again.
            active = false;
            var value = dictionary[key];
            dictionary.Remove(key);
            dictionary.Add(next, value);
            if (ownerId is { } id) Documents.Current.Current.References.RenameDictionaryKey(id, path, key, next);
            CommitBoundValue(binding, dictionary);
            MarkInvalid(box, null);
            refresh();
        }
        box.LostFocus += (_, _) => commitKey();
        box.KeyDown += (_, e) =>
        {
            if (!active) return;
            if (e.Key == Key.Enter) { commitKey(); e.Handled = true; }
            else if (e.Key == Key.Escape) { box.Text = key; e.Handled = true; }
        };
        row.Children.Add(box);
        var childPath = SceneReferenceStore.DictionaryPath(path, key);
        var editor = BuildBoundValueEditor(binding.Entry(type, key), $"{name}.Value[{index}]", ownerId, childPath, element: true);
        Grid.SetColumn(editor, 1);
        row.Children.Add(editor);
        AttachEditorDropHandlers(row, editor);
        var remove = BuildRemoveButton($"{name}.Remove[{index}]");
        Grid.SetColumn(remove, 2);
        row.Children.Add(remove);
        remove.Click += (_, _) =>
        {
            if (IsPlaying || binding.Read() is not IDictionary dictionary) return;
            dictionary.Remove(key);
            ClearBoundReferences(ownerId, childPath);
            CommitBoundValue(binding, dictionary);
            refresh();
        };
        return row;
    }

    private StackPanel BuildBoundArrayShape(InspectorValueBinding binding, string name, Guid? ownerId, string path, Action refresh)
    {
        var array = (Array)binding.Read()!;
        var panel = new StackPanel { Spacing = 4 };
        var boxes = Enumerable.Range(0, array.Rank).Select(dimension =>
        {
            var box = new TextBox { Text = array.GetLength(dimension).ToString(CultureInfo.InvariantCulture), Classes = { "inspectorField" } };
            box.SetValue(AutomationProperties.NameProperty, $"{name}.Dimension[{dimension}]");
            ToolTip.SetTip(box, $"Dimension {dimension} length (zero or greater)");
            return box;
        }).ToList();
        panel.Children.Add(BuildAxisGrid([.. Enumerable.Range(0, array.Rank).Select(dimension => dimension.ToString(CultureInfo.InvariantCulture))], boxes));
        var resize = BuildValueCommandButton("Resize", $"{name}.Resize");
        panel.Children.Add(resize);
        resize.Click += (_, _) =>
        {
            if (IsPlaying) return;
            var current = (Array)binding.Read()!;
            var lengths = new int[array.Rank];
            for (var dimension = 0; dimension < lengths.Length; dimension++)
            {
                if (!int.TryParse(boxes[dimension].Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out lengths[dimension])
                    || lengths[dimension] < 0 || lengths[dimension] > Array.MaxLength)
                {
                    MarkInvalid(boxes[dimension], "Enter a valid non-negative array length");
                    return;
                }
                MarkInvalid(boxes[dimension], null);
            }
            Array replacement;
            try
            {
                var total = InspectorArrayShape.ValidateDimensions(lengths, path);
                if (total > 1_048_576 && total > current.Length)
                {
                    MarkInvalid(boxes[0], "Inspector growth is limited to 1048576 items; existing larger arrays can be shrunk or edited.");
                    return;
                }
                replacement = InspectorArrayShape.Create(binding.ValueType.GetElementType()!, lengths, path);
            }
            catch (InvalidDataException error)
            {
                MarkInvalid(boxes[0], error.Message);
                return;
            }
            foreach (var indices in InspectorArrayShape.Indices(current))
            {
                if (indices.Where((coordinate, dimension) => coordinate >= lengths[dimension]).Any())
                    ClearBoundReferences(ownerId, $"{path}[{string.Join(",", indices)}]");
                else replacement.SetValue(current.GetValue(indices), indices);
            }
            CommitBoundValue(binding, replacement);
            refresh();
        };
        return panel;
    }
}
