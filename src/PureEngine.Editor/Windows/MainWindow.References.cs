using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using PureEngine.Core;

namespace PureEngine.Editor;

public partial class MainWindow
{
    internal sealed record ReferenceOption(object? Value, string Display)
    {
        public override string ToString() => Display;
    }

    private Guid? GetOwnerComponentId(object component)
    {
        var owner = FindOwner(component);
        if (owner is null)
            return null;
        return owner.TryGetComponentId(component, out var id) ? id : null;
    }

    private bool ShouldShowReferenceEditor(Type declaredType) =>
        SceneReferenceTypes.IsSingleReference(declaredType, _components.Registry);

    private List<(SceneObject Owner, object? Component, Guid Id, string Display)> ReferenceCandidates(Type declaredType)
    {
        List<(SceneObject Owner, object? Component, Guid Id, string Display)> found = [];
        if (declaredType == typeof(SceneObject))
        {
            foreach (var item in _editScene.Current.Objects)
                found.Add((item, null, item.Id, $"{item.Name} ({item.Id:D})"));
            return found;
        }
        foreach (var item in _editScene.Current.Objects)
        {
            foreach (var component in item.Components)
            {
                if (!declaredType.IsAssignableFrom(component.GetType()))
                    continue;
                if (!item.TryGetComponentId(component, out var id))
                    continue;
                found.Add((item, component, id, $"{item.Name}/{component.GetType().Name} ({id:D})"));
            }
        }
        return found;
    }

    private bool TryResolveDraggedReference(Guid draggedId, Type declaredType, out object? target, out string? error)
    {
        target = null;
        error = null;
        var scene = _editScene.Current;
        SceneObject? draggedObject = null;
        foreach (var item in scene.Objects)
        {
            if (item.Id == draggedId)
            {
                draggedObject = item;
                break;
            }
        }
        if (declaredType == typeof(SceneObject))
        {
            if (draggedObject is null)
            {
                error = "Target is not in the current scene.";
                return false;
            }
            target = draggedObject;
            return true;
        }
        if (draggedObject is not null)
        {
            List<object> matches = [];
            foreach (var component in draggedObject.Components)
            {
                if (declaredType.IsAssignableFrom(component.GetType()))
                    matches.Add(component);
            }
            if (matches.Count == 1)
            {
                target = matches[0];
                return true;
            }
            if (matches.Count == 0)
            {
                error = $"No {declaredType.Name} on the dragged object.";
                return false;
            }
            error = $"Multiple {declaredType.Name} candidates on the dragged object.";
            return false;
        }
        foreach (var item in scene.Objects)
        {
            foreach (var component in item.Components)
            {
                if (!item.TryGetComponentId(component, out var id) || id != draggedId)
                    continue;
                if (!declaredType.IsAssignableFrom(component.GetType()))
                {
                    error = $"Type mismatch: expected {declaredType.Name}.";
                    return false;
                }
                target = component;
                return true;
            }
        }
        error = "Target is not in the current scene.";
        return false;
    }

    private void SetSingleReference(
        object ownerComponent,
        MemberInfo member,
        string storePath,
        object? newValue,
        Action refresh)
    {
        if (IsPlaying)
            return;
        var ownerId = GetOwnerComponentId(ownerComponent);
        if (ownerId is null)
            return;
        switch (member)
        {
            case FieldInfo field: field.SetValue(ownerComponent, newValue); break;
            case PropertyInfo property: property.SetValue(ownerComponent, newValue); break;
            default: throw new NotSupportedException($"Unsupported member: {member.Name}");
        }
        _editScene.Current.References.ClearMissing(ownerId.Value, storePath);
        _editScene.Current.References.ClearLegacy(ownerId.Value, storePath);
        MarkSceneChanged();
        refresh();
        QueuePendingUserCodeReload();
    }

    private void SetElementReference(
        Guid ownerId,
        string storePath,
        Action<object?> assign,
        object? newValue,
        Action refresh)
    {
        if (IsPlaying)
            return;
        assign(newValue);
        _editScene.Current.References.ClearMissing(ownerId, storePath);
        _editScene.Current.References.ClearLegacy(ownerId, storePath);
        MarkSceneChanged();
        refresh();
        QueuePendingUserCodeReload();
    }

    private StackPanel BuildSingleReferenceEditor(
        Func<object?> getter,
        Action<object?> setter,
        Type declaredType,
        Guid ownerId,
        string storePath,
        string automationName)
    {
        var root = new StackPanel { Spacing = 4 };
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        combo.Classes.Add("inspectorCombo");
        combo.SetValue(AutomationProperties.NameProperty, automationName);
        var info = new TextBlock { Classes = { "memberType" }, TextWrapping = TextWrapping.Wrap };
        info.SetValue(AutomationProperties.NameProperty, $"{automationName}.Info");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var clear = new Avalonia.Controls.Button { Content = "Clear", FontSize = 11, Padding = new Avalonia.Thickness(8, 2) };
        clear.SetValue(AutomationProperties.NameProperty, $"{automationName}.Clear");
        buttons.Children.Add(clear);
        root.Children.Add(combo);
        root.Children.Add(info);
        root.Children.Add(buttons);
        var refreshing = false;
        void assign(object? value)
        {
            if (IsPlaying || (ReferenceEquals(getter(), value)
                && !_editScene.Current.References.TryGetMissing(ownerId, storePath, out _)
                && !_editScene.Current.References.TryGetLegacy(ownerId, storePath, out _)))
                return;
            setter(value);
            refreshOptions();
        }
        void refreshOptions()
        {
            var current = getter();
            var candidates = ReferenceCandidates(declaredType);
            List<ReferenceOption> options = [new ReferenceOption(null, "None")];
            foreach (var candidate in candidates)
            {
                var value = declaredType == typeof(SceneObject) ? (object)candidate.Owner : candidate.Component;
                options.Add(new ReferenceOption(value, candidate.Display));
            }
            var selected = options[0];
            if (current is not null)
            {
                var match = options.FirstOrDefault(option => ReferenceEquals(option.Value, current));
                if (match is not null)
                {
                    selected = match;
                    info.Text = selected.Display;
                }
                else
                {
                    selected = new ReferenceOption(current, $"Detached: {current.GetType().Name}");
                    options.Add(selected);
                    info.Text = selected.Display;
                }
            }
            else if (_editScene.Current.References.TryGetMissing(ownerId, storePath, out var missing))
            {
                selected = new ReferenceOption(null, $"Missing: {missing:D}");
                options.Add(selected);
                info.Text = $"Missing {missing:D}. ID is kept.";
            }
            else if (_editScene.Current.References.TryGetLegacy(ownerId, storePath, out _))
            {
                info.Text = "Old inline value is kept. Reassign or Clear to save.";
            }
            else
            {
                info.Text = "None.";
            }
            refreshing = true;
            try
            {
                combo.ItemsSource = options;
                combo.SelectedItem = selected;
            }
            finally { refreshing = false; }
            ToolTip.SetTip(combo, $"{storePath} : {FriendlyTypeName(declaredType)} — Select, Clear, or drop a Stuffs row");
        }
        combo.SelectionChanged += (_, _) =>
        {
            if (IsPlaying || refreshing)
                return;
            if (combo.SelectedItem is not ReferenceOption option)
                return;
            if (option.Display.StartsWith("Missing:", StringComparison.Ordinal))
                return;
            assign(option.Value);
        };
        clear.Click += (_, _) =>
        {
            if (IsPlaying)
                return;
            assign(null);
        };
        combo.AddHandler(DragDrop.DragOverEvent, (sender, e) =>
        {
            if (!e.DataTransfer.Contains(SceneObjectIdFormat))
                return;
            if (IsPlaying || GetDraggedId(e) is not { } draggedId)
            {
                e.DragEffects = DragDropEffects.None;
                e.Handled = true;
                return;
            }
            if (!TryResolveDraggedReference(draggedId, declaredType, out _, out _))
            {
                e.DragEffects = DragDropEffects.None;
                e.Handled = true;
                return;
            }
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        combo.AddHandler(DragDrop.DropEvent, (sender, e) =>
        {
            if (!e.DataTransfer.Contains(SceneObjectIdFormat))
                return;
            e.Handled = true;
            if (RejectWhenPlaying("Assign"))
                return;
            if (GetDraggedId(e) is not { } draggedId)
                return;
            if (!TryResolveDraggedReference(draggedId, declaredType, out var target, out var error))
            {
                SetFileStatus(error ?? "Cannot assign reference.", true);
                return;
            }
            assign(target);
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        refreshOptions();
        return root;
    }

    private Control BuildMemberReferenceEditor(object component, MemberInfo member, string automationName)
    {
        var declaredType = GetMemberType(member);
        var ownerId = GetOwnerComponentId(component);
        if (ownerId is null)
            return UnsupportedBadge(declaredType);
        var storePath = member.Name;
        return BuildSingleReferenceEditor(
            () => GetMemberValue(component, member),
            value => SetSingleReference(component, member, storePath, value, RefreshComponents),
            declaredType, ownerId.Value, storePath, automationName);
    }

    private StackPanel BuildNestedReferenceEditor(object container, MemberInfo member, Guid ownerId, string storePath, string automationName, Action refresh)
    {
        var declaredType = GetMemberType(member);
        return BuildSingleReferenceEditor(
            () => member is FieldInfo field ? field.GetValue(container) : ((PropertyInfo)member).GetValue(container),
            value =>
            {
                if (IsPlaying)
                    return;
                switch (member)
                {
                    case FieldInfo field: field.SetValue(container, value); break;
                    case PropertyInfo property: property.SetValue(container, value); break;
                    default: throw new NotSupportedException($"Unsupported member: {member.Name}");
                }
                _editScene.Current.References.ClearMissing(ownerId, storePath);
                _editScene.Current.References.ClearLegacy(ownerId, storePath);
                MarkSceneChanged();
                refresh();
                QueuePendingUserCodeReload();
            },
            declaredType, ownerId, storePath, automationName);
    }

    private Control BuildSequenceReferenceEditor(object component, MemberInfo member, int index, Type elementType, string automationName)
    {
        var ownerId = GetOwnerComponentId(component);
        if (ownerId is null)
            return UnsupportedBadge(elementType);
        var storePath = $"{member.Name}[{index}]";
        return BuildSingleReferenceEditor(
            () => SequenceElement(component, member, index),
            value => SetElementReference(ownerId.Value, storePath, v => SetSequenceElement(component, member, index, v), value, RefreshComponents),
            elementType, ownerId.Value, storePath, automationName);
    }

    private Control BuildDictionaryReferenceEditor(object component, MemberInfo member, string key, Type valueType, string automationName)
    {
        var ownerId = GetOwnerComponentId(component);
        if (ownerId is null)
            return UnsupportedBadge(valueType);
        var storePath = SceneReferenceStore.DictionaryPath(member.Name, key);
        return BuildSingleReferenceEditor(
            () =>
            {
                if (GetMemberValue(component, member) is System.Collections.IDictionary dictionary && dictionary.Contains(key))
                    return dictionary[key];
                return null;
            },
            value =>
            {
                if (IsPlaying)
                    return;
                if (GetMemberValue(component, member) is not System.Collections.IDictionary dictionary || !dictionary.Contains(key))
                    return;
                dictionary[key] = value;
                _editScene.Current.References.ClearMissing(ownerId.Value, storePath);
                _editScene.Current.References.ClearLegacy(ownerId.Value, storePath);
                MarkSceneChanged();
                RefreshComponents();
                QueuePendingUserCodeReload();
            },
            valueType, ownerId.Value, storePath, automationName);
    }

    private StackPanel BuildNestedCollectionEditor(object owner, MemberInfo member, Guid ownerId, string path, string automationName)
    {
        var type = GetMemberType(member);
        var mapping = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>);
        var elementType = type.IsArray ? type.GetElementType()! : type.GetGenericArguments()[mapping ? 1 : 0];
        var root = new StackPanel { Spacing = 4 };
        void assign(object? value)
        {
            if (member is FieldInfo field) field.SetValue(owner, value);
            else ((PropertyInfo)member).SetValue(owner, value);
        }
        void changed()
        {
            MarkSceneChanged();
            refresh();
            QueuePendingUserCodeReload();
        }
        void refresh()
        {
            root.Children.Clear();
            var value = GetMemberValue(owner, member);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            root.Children.Add(actions);
            void action(string label, string suffix, Action edit)
            {
                var button = BuildHeaderButton(label, $"{automationName}.{suffix}");
                button.Click += (_, _) => { if (!IsPlaying) { edit(); changed(); } };
                actions.Children.Add(button);
            }
            if (value is null)
            {
                action("Create", "Create", () => assign(type.IsArray ? Array.CreateInstance(elementType, 0) : Activator.CreateInstance(type)));
                return;
            }
            action("Add", "Add", () =>
            {
                if (value is System.Collections.IDictionary dictionary)
                    dictionary.Add(UniqueDictionaryKey(dictionary), DefaultElementValue(elementType));
                else if (value is Array array)
                {
                    var next = Array.CreateInstance(elementType, array.Length + 1);
                    Array.Copy(array, next, array.Length);
                    next.SetValue(DefaultElementValue(elementType), array.Length);
                    assign(next);
                }
                else ((System.Collections.IList)value).Add(DefaultElementValue(elementType));
            });
            action("Clear", "Clear", () =>
            {
                assign(type.IsArray ? Array.CreateInstance(elementType, 0) : Activator.CreateInstance(type));
                _editScene.Current.References.RemovePathsForMember(ownerId, path);
            });
            action("Set Null", "Null", () =>
            {
                assign(null);
                _editScene.Current.References.RemovePathsForMember(ownerId, path);
            });
            var keys = mapping ? ((System.Collections.IDictionary)value).Keys.Cast<string>().ToArray() : [];
            var count = mapping ? keys.Length : ((System.Collections.IList)value).Count;
            for (var i = 0; i < count; i++)
            {
                var index = i;
                var key = mapping ? keys[index] : index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var slot = SceneReferenceStore.DictionaryPath(path, key);
                var name = mapping ? $"{automationName}.Value[{index}]" : $"{automationName}[{index}]";
                object? get() => mapping ? ((System.Collections.IDictionary)value)[key] : ((System.Collections.IList)value)[index];
                void set(object? element)
                {
                    if (IsPlaying) return;
                    if (mapping) ((System.Collections.IDictionary)value)[key] = element;
                    else ((System.Collections.IList)value)[index] = element;
                    _editScene.Current.References.RemovePathsForMember(ownerId, slot);
                    changed();
                }
                if (mapping)
                {
                    var keyBox = new TextBox { Text = key };
                    keyBox.SetValue(AutomationProperties.NameProperty, $"{automationName}.Key[{index}]");
                    keyBox.TextChanged += (_, _) =>
                    {
                        if (IsPlaying || keyBox.Text == key) return;
                        var dictionary = (System.Collections.IDictionary)value;
                        if (string.IsNullOrEmpty(keyBox.Text) || dictionary.Contains(keyBox.Text))
                        {
                            MarkInvalid(keyBox, "Enter a unique non-empty key");
                            return;
                        }
                        var nextKey = keyBox.Text;
                        var preserved = dictionary[key];
                        dictionary.Remove(key);
                        dictionary.Add(nextKey, preserved);
                        _editScene.Current.References.MovePath(ownerId, slot, SceneReferenceStore.DictionaryPath(path, nextKey));
                        MarkInvalid(keyBox, null);
                        changed();
                    };
                    root.Children.Add(keyBox);
                }
                root.Children.Add(SceneReferenceTypes.IsSingleReference(elementType, _components.Registry)
                    ? BuildSingleReferenceEditor(get, set, elementType, ownerId, slot, name)
                    : BuildObjectBox(get, set, elementType, name, ownerId, slot));
                var remove = BuildRemoveButton($"{automationName}.Remove[{index}]");
                remove.Click += (_, _) =>
                {
                    if (IsPlaying) return;
                    _editScene.Current.References.RemovePathsForMember(ownerId, slot);
                    if (mapping) ((System.Collections.IDictionary)value).Remove(key);
                    else
                    {
                        for (var j = index + 1; j < count; j++)
                            _editScene.Current.References.MovePath(ownerId, $"{path}[{j}]", $"{path}[{j - 1}]");
                        if (value is Array array)
                        {
                            var next = Array.CreateInstance(elementType, count - 1);
                            Array.Copy(array, 0, next, 0, index);
                            Array.Copy(array, index + 1, next, index, count - index - 1);
                            assign(next);
                        }
                        else ((System.Collections.IList)value).RemoveAt(index);
                    }
                    changed();
                };
                root.Children.Add(remove);
            }
        }
        refresh();
        return root;
    }
}
