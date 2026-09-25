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
    internal sealed record ReferenceOption(object? Value, string Display, string Detail)
    {
        public override string ToString() => Display;
    }

    private sealed record ReferenceDropRegistration(Type DeclaredType, Action<object?> Assign);

    private sealed record ImageDropRegistration(Action<Sprite> Assign);

    private Guid? GetOwnerComponentId(object component)
    {
        var owner = FindOwner(component);
        if (owner is null)
            return null;
        return owner.TryGetComponentId(component, out var id) ? id : null;
    }

    /// <summary>Asset files own their data: a plain registered type inside asset-owned data edits as a nested
    /// value, never as a scene reference. SceneObject and data asset references keep the reference editor.</summary>
    private bool ShouldShowReferenceEditorFor(Type declaredType, object? owner)
    {
        if (!SceneReferenceTypes.IsSingleReference(declaredType, _components.Registry)) return false;
        if (owner is not null && (_assetOwned.Contains(owner) || _tableOwned.Contains(owner))
            && declaredType != typeof(SceneObject)
            && !DataAssetStore.IsAssetType(declaredType)
            && SceneReferenceTypes.IsComponentReference(declaredType, _components.Registry)) return false;
        return true;
    }

    private List<(SceneObject? Owner, object? Component, Guid Id, string Display, string Detail)> ReferenceCandidates(Type declaredType)
    {
        List<(SceneObject? Owner, object? Component, Guid Id, string Display, string Detail)> found = [];
        if (DataAssetStore.IsAssetType(declaredType))
        {
            RefreshReferenceAssets();
            var assets = _editScene.Current.DataAssets;
            foreach (var id in assets.Ids)
                if (assets.TryGet<object>(id, out var asset) && declaredType.IsInstanceOfType(asset))
                {
                    var display = assets.DisplayName(id);
                    found.Add((null, asset, id, display, $"{display} ({id:D})"));
                }
            return found;
        }
        if (declaredType == typeof(SceneObject))
        {
            foreach (var item in _editScene.Current.Objects)
                found.Add((item, null, item.Id, item.Name, $"{item.Name} ({item.Id:D})"));
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
                var display = $"{item.Name}/{component.GetType().Name}";
                found.Add((item, component, id, display, $"{display} ({id:D})"));
            }
        }
        return found;
    }

    /// <summary>Shortens an ID for single-line option display. The full ID stays in the tooltip.</summary>
    private static string ShortId(Guid id) => id.ToString("D")[..8];

    private bool TryResolveDraggedReference(Guid draggedId, Type declaredType, out object? target, out string? error)
    {
        target = null;
        error = null;
        if (DataAssetStore.IsAssetType(declaredType))
        {
            error = "Drop a project data asset file, not a scene object.";
            return false;
        }
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
        if (draggedObject is not null)
            return TryResolveObjectReference(draggedObject, declaredType, out target, out error);
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

    private static bool TryResolveObjectReference(SceneObject root, Type declaredType, out object? target, out string? error)
    {
        target = null;
        error = null;
        if (declaredType == typeof(SceneObject))
        {
            target = root;
            return true;
        }
        List<object> direct = [];
        foreach (var component in root.Components)
        {
            if (declaredType.IsAssignableFrom(component.GetType()))
                direct.Add(component);
        }
        if (direct.Count == 1)
        {
            target = direct[0];
            return true;
        }
        if (direct.Count > 1)
        {
            error = $"Multiple {declaredType.Name} candidates on the dragged object.";
            return false;
        }
        List<object> descendants = [];
        var pending = new Stack<SceneObject>();
        foreach (var child in root.Children)
            pending.Push(child);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var component in current.Components)
            {
                if (declaredType.IsAssignableFrom(component.GetType()))
                    descendants.Add(component);
            }
            foreach (var child in current.Children)
                pending.Push(child);
        }
        if (descendants.Count == 1)
        {
            target = descendants[0];
            return true;
        }
        error = descendants.Count == 0
            ? $"No {declaredType.Name} in the dragged object subtree."
            : $"Multiple {declaredType.Name} candidates in the dragged object subtree.";
        return false;
    }

    private static bool IsReferenceDrag(DragEventArgs e) =>
        e.DataTransfer.Contains(SceneObjectIdFormat)
        || e.DataTransfer.Contains(DataAssetIdFormat)
        || e.DataTransfer.Contains(PrefabPathFormat);

    private bool CanDropReference(DragEventArgs e, Type declaredType)
    {
        if (e.DataTransfer.Contains(DataAssetIdFormat))
            return DroppedDataAsset(e, declaredType) is not null;
        if (e.DataTransfer.Contains(SceneObjectIdFormat))
        {
            if (GetDraggedId(e) is not { } draggedId)
                return false;
            return TryResolveDraggedReference(draggedId, declaredType, out var target, out var error)
                && (target is not null || error is null);
        }
        if (e.DataTransfer.TryGetValue(PrefabPathFormat) is { } path)
            return TryLoadPrefabReference(path, declaredType, out var document, out var loadError)
                && document is not null && loadError is null;
        return false;
    }

    private static bool IsSourceWithin(Visual? source, Control boundary) =>
        source is not null && source.GetSelfAndVisualAncestors().Contains(boundary);

    private void AttachEditorDropHandlers(Grid row, Control editor)
    {
        if (editor.Tag is ReferenceDropRegistration or ImageDropRegistration)
            row.SetCurrentValue(Panel.BackgroundProperty, Brushes.Transparent);
        switch (editor.Tag)
        {
            case ReferenceDropRegistration reference:
                AttachReferenceDropHandlers(row, reference.DeclaredType, reference.Assign, editor);
                break;
            case ImageDropRegistration image:
                AttachImageDropHandlers(row, image.Assign, editor);
                break;
        }
    }

    private void AttachReferenceDropHandlers(
        Control target,
        Type declaredType,
        Action<object?> assign,
        Control? handledBoundary = null)
    {
        target.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (handledBoundary is not null && (e.Handled || IsSourceWithin(e.Source as Visual, handledBoundary))) return;
            if (!IsReferenceDrag(e)) return;
            e.Handled = true;
            e.DragEffects = !IsPlaying && CanDropReference(e, declaredType)
                ? DragDropEffects.Copy : DragDropEffects.None;
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        target.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (handledBoundary is not null && (e.Handled || IsSourceWithin(e.Source as Visual, handledBoundary))) return;
            if (!IsReferenceDrag(e)) return;
            e.Handled = true;
            e.DragEffects = DragDropEffects.None;
            if (RejectWhenPlaying("Assign")) return;
            if (e.DataTransfer.Contains(DataAssetIdFormat))
            {
                if (DroppedDataAsset(e, declaredType) is not { } asset)
                {
                    SetFileStatus("Drop a matching project data asset file.", true);
                    return;
                }
                assign(asset);
            }
            else if (e.DataTransfer.Contains(SceneObjectIdFormat))
            {
                if (GetDraggedId(e) is not { } draggedId)
                {
                    SetFileStatus("The dragged Stuffs row has no valid ID.", true);
                    return;
                }
                if (!TryResolveDraggedReference(draggedId, declaredType, out var value, out var error))
                {
                    SetFileStatus(error ?? "Cannot assign reference.", true);
                    return;
                }
                assign(value);
            }
            else if (e.DataTransfer.TryGetValue(PrefabPathFormat) is { } path)
            {
                if (!TryAssignPrefabReference(path, declaredType, assign, out var error))
                {
                    SetFileStatus(error ?? "Cannot assign prefab reference.", true);
                    return;
                }
            }
            else
            {
                return;
            }
            e.DragEffects = DragDropEffects.Copy;
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void AttachImageDropHandlers(
        Control target,
        Action<Sprite> assign,
        Control? handledBoundary = null)
    {
        target.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (handledBoundary is not null && (e.Handled || IsSourceWithin(e.Source as Visual, handledBoundary))) return;
            if (!e.DataTransfer.Contains(ImageIdFormat)) return;
            e.Handled = true;
            e.DragEffects = !IsPlaying && DroppedImageId(e) is not null
                ? DragDropEffects.Copy : DragDropEffects.None;
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        target.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (handledBoundary is not null && (e.Handled || IsSourceWithin(e.Source as Visual, handledBoundary))) return;
            if (!e.DataTransfer.Contains(ImageIdFormat)) return;
            e.Handled = true;
            e.DragEffects = DragDropEffects.None;
            if (RejectWhenPlaying("Assign")) return;
            if (DroppedImageId(e) is not { } id)
            {
                SetFileStatus("Drop a registered project image.", true);
                return;
            }
            assign(new Sprite(id));
            e.DragEffects = DragDropEffects.Copy;
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private bool TryLoadPrefabReference(string path, Type declaredType, out PrefabDocument? document, out string? error)
    {
        document = null;
        error = null;
        if (_project is null)
        {
            error = "Open a project first.";
            return false;
        }
        if (DataAssetStore.IsAssetType(declaredType))
        {
            error = "Drop a scene object or prefab, not a data asset field.";
            return false;
        }
        try
        {
            _project.ValidatePrefabPath(path);
            document = PrefabFile.Load(path);
            if (declaredType == typeof(SceneObject))
                return true;
            if (!SceneReferenceTypes.IsComponentReference(declaredType, _components.Registry))
            {
                error = $"Prefab cannot be assigned to {declaredType.Name}.";
                return false;
            }
            var count = document.Objects!
                .SelectMany(item => item.Components ?? [])
                .Count(data => declaredType.IsAssignableFrom(_components.Registry.GetType(data.TypeId!)));
            if (count == 1)
                return true;
            error = count == 0
                ? $"Prefab has no {declaredType.Name} component."
                : $"Prefab has multiple {declaredType.Name} components.";
            return false;
        }
        catch (Exception exception)
        {
            document = null;
            error = exception.GetBaseException().Message;
            return false;
        }
    }

    private bool TryAssignPrefabReference(string path, Type declaredType, Action<object?> assign, out string? error)
    {
        error = null;
        if (!TryLoadPrefabReference(path, declaredType, out var document, out error) || document is null)
            return false;
        try
        {
            assign(_editScene.Current.Prefabs.Assign(document, declaredType, _components.Registry, EditSession.Factory, _editScene.Current.DataAssets));
        }
        catch (Exception exception)
        {
            error = exception.GetBaseException().Message;
            return false;
        }
        SetFileStatus($"Assigned prefab: {Path.GetFileName(path)}");
        return true;
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

    /// <summary>
    /// Single-line reference field: name-only ComboBox with an inline clear button.
    /// Full names and IDs live in the tooltip; the second line shows only Missing or legacy states.
    /// Collection rows pass <paramref name="showClear"/> as false since the row remove button owns removal.
    /// </summary>
    private StackPanel BuildSingleReferenceEditor(
        Func<object?> getter,
        Action<object?> setter,
        Type declaredType,
        Guid ownerId,
        string storePath,
        string automationName,
        bool showClear = true)
    {
        var root = new StackPanel { Spacing = 4 };
        var row = new Grid { ColumnSpacing = 6, VerticalAlignment = VerticalAlignment.Center };
        row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        combo.Classes.Add("inspectorCombo");
        combo.SetValue(AutomationProperties.NameProperty, automationName);
        Grid.SetColumn(combo, 0);
        row.Children.Add(combo);
        var clear = BuildRemoveButton($"{automationName}.Clear");
        ToolTip.SetTip(clear, "Clear");
        Grid.SetColumn(clear, 1);
        if (showClear)
            row.Children.Add(clear);
        var info = new TextBlock { Classes = { "memberType" }, TextWrapping = TextWrapping.Wrap };
        info.SetValue(AutomationProperties.NameProperty, $"{automationName}.Info");
        root.Children.Add(row);
        root.Children.Add(info);
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
            List<ReferenceOption> options = [new ReferenceOption(null, "None", "None")];
            foreach (var candidate in candidates)
            {
                var value = declaredType == typeof(SceneObject) ? candidate.Owner : candidate.Component;
                options.Add(new ReferenceOption(value, candidate.Display, candidate.Detail));
            }
            var selected = options[0];
            string? note = null;
            if (current is not null)
            {
                var match = options.FirstOrDefault(option => ReferenceEquals(option.Value, current));
                if (match is not null)
                {
                    selected = match;
                }
                else if (PrefabReferenceStore.TryGetIdentity(current, out var prefab))
                {
                    var display = $"Prefab: {BuildPrefabCatalog().DisplayName(prefab!.PrefabId)}";
                    selected = new ReferenceOption(current, display, $"{display} ({prefab.TargetId:D})");
                    options.Add(selected);
                }
                else if (DataAssetStore.IsAssetType(declaredType) && _editScene.Current.DataAssets.TryGetId(current, out var assetId))
                {
                    selected = new ReferenceOption(current, $"Missing: {ShortId(assetId)}", $"Missing: {assetId:D}");
                    options.Add(selected);
                    note = $"Missing {assetId:D}. ID is kept.";
                }
                else
                {
                    selected = new ReferenceOption(current, $"Detached: {current.GetType().Name}", $"Detached: {current.GetType().FullName}");
                    options.Add(selected);
                }
            }
            else if (_editScene.Current.References.TryGetMissing(ownerId, storePath, out var missing))
            {
                selected = new ReferenceOption(null, $"Missing: {ShortId(missing)}", $"Missing: {missing:D}");
                options.Add(selected);
                note = $"Missing {missing:D}. ID is kept.";
            }
            else if (_editScene.Current.References.TryGetLegacy(ownerId, storePath, out _))
            {
                note = "Old inline value is kept. Reassign or clear to save.";
            }
            info.Text = note ?? "";
            info.IsVisible = note is not null;
            refreshing = true;
            try
            {
                combo.ItemsSource = options;
                combo.SelectedItem = selected;
            }
            finally { refreshing = false; }
            var source = DataAssetStore.IsAssetType(declaredType)
                ? "a project asset file" : "a Stuffs row or matching prefab";
            ToolTip.SetTip(combo, $"{storePath} : {FriendlyTypeName(declaredType)} — {selected.Detail}. Select, clear, or drop {source}");
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
        root.Tag = new ReferenceDropRegistration(declaredType, assign);
        AttachReferenceDropHandlers(root, declaredType, assign);
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
            elementType, ownerId.Value, storePath, automationName, showClear: false);
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
            valueType, ownerId.Value, storePath, automationName, showClear: false);
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
            void action(string label, string suffix, Action edit, string? tip = null)
            {
                var button = BuildHeaderButton(label, $"{automationName}.{suffix}");
                if (tip is not null)
                    ToolTip.SetTip(button, tip);
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
            action("Set Null", "Null", () =>
            {
                assign(null);
                _editScene.Current.References.RemovePathsForMember(ownerId, path);
            });
            action("\U0001F5D1", "Clear", () =>
            {
                assign(type.IsArray ? Array.CreateInstance(elementType, 0) : Activator.CreateInstance(type));
                _editScene.Current.References.RemovePathsForMember(ownerId, path);
            }, "Remove all rows. The empty collection stays.");
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
                    ? BuildSingleReferenceEditor(get, set, elementType, ownerId, slot, name, showClear: false)
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
