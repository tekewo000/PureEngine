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

    /// <summary>
    /// Reference-holding collection inside a custom class. Mirrors the top-level sequence and
    /// dictionary editors: one collapsible header (count plus Add/Set Null/Clear) with each row
    /// keeping its editor and fixed-width remove button on a single Grid line.
    /// </summary>
    private StackPanel BuildNestedCollectionEditor(object owner, MemberInfo member, Guid ownerId, string path, string automationName)
    {
        var type = GetMemberType(member);
        var mapping = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>);
        var elementType = type.IsArray ? type.GetElementType()! : type.GetGenericArguments()[mapping ? 1 : 0];
        var root = new StackPanel { Spacing = 6 };
        var count = new TextBlock { Classes = { "memberType" }, VerticalAlignment = VerticalAlignment.Center };
        var add = BuildHeaderIconButton("Icon.AddSquare", $"{automationName}.Add");
        ToolTip.SetTip(add, mapping ? "Add an entry." : "Add a row.");
        var setNull = BuildHeaderButton("Set Null", $"{automationName}.Null");
        ToolTip.SetTip(setNull, mapping
            ? "Set the dictionary itself to null. Removing rows keeps an empty dictionary."
            : "Set the list itself to null. Removing rows keeps an empty list.");
        var clear = BuildHeaderIconButton("Icon.Delete", $"{automationName}.Clear");
        ToolTip.SetTip(clear, mapping
            ? "Remove all entries. The empty dictionary stays."
            : "Remove all rows. The empty list stays.");
        var nullStatus = new TextBlock { Classes = { "memberType" }, Text = "Null", VerticalAlignment = VerticalAlignment.Center };
        var create = BuildHeaderButton("Create", $"{automationName}.Create");
        var body = new StackPanel { Spacing = 4 };
        var toggle = BuildCollapseToggle($"{automationName}.Collapse", automationName, _collapsedMembers,
            nowExpanded => body.IsVisible = nowExpanded);
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(toggle);
        left.Children.Add(count);
        var header = BuildSplitHeader(left, add, setNull, clear);
        var nullHeader = BuildSplitHeader(nullStatus, create);
        root.Children.Add(header);
        root.Children.Add(nullHeader);
        root.Children.Add(body);
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
            foreach (var box in body.GetVisualDescendants().OfType<TextBox>())
                _invalidFields.Remove(box);
            body.Children.Clear();
            var value = GetMemberValue(owner, member);
            if (value is null)
            {
                nullHeader.IsVisible = true;
                header.IsVisible = false;
                body.IsVisible = false;
            }
            else if (mapping && value is not System.Collections.IDictionary)
            {
                nullHeader.IsVisible = true;
                header.IsVisible = false;
                body.IsVisible = false;
            }
            else if (!mapping && value is not System.Collections.IList)
            {
                nullHeader.IsVisible = true;
                header.IsVisible = false;
                body.IsVisible = false;
            }
            else
            {
                nullHeader.IsVisible = false;
                header.IsVisible = true;
                body.IsVisible = !_collapsedMembers.TryGetValue(automationName, out var collapsed) || !collapsed;
                if (mapping)
                {
                    var dictionary = (System.Collections.IDictionary)value;
                    var keys = dictionary.Keys.Cast<string>().OrderBy(key => key, StringComparer.Ordinal).ToList();
                    count.Text = $"{keys.Count} entries";
                    for (var i = 0; i < keys.Count; i++)
                        body.Children.Add(BuildNestedDictionaryRow(owner, member, ownerId, path, automationName, elementType, keys[i], i, changed));
                }
                else
                {
                    var list = (System.Collections.IList)value;
                    count.Text = $"{list.Count} items";
                    for (var i = 0; i < list.Count; i++)
                        body.Children.Add(BuildNestedSequenceRow(owner, member, ownerId, path, automationName, elementType, i, changed));
                }
            }
            UpdateErrorBadge();
            QueuePendingUserCodeReload();
        }
        add.Click += (_, _) =>
        {
            if (IsPlaying) return;
            var value = GetMemberValue(owner, member);
            if (value is null) return;
            if (mapping && value is System.Collections.IDictionary dictionary)
                dictionary.Add(UniqueDictionaryKey(dictionary), DefaultElementValue(elementType));
            else if (value is Array array)
            {
                var next = Array.CreateInstance(elementType, array.Length + 1);
                Array.Copy(array, next, array.Length);
                next.SetValue(DefaultElementValue(elementType), array.Length);
                assign(next);
            }
            else if (value is System.Collections.IList list)
                list.Add(DefaultElementValue(elementType));
            changed();
        };
        setNull.Click += (_, _) =>
        {
            if (IsPlaying) return;
            assign(null);
            _editScene.Current.References.RemovePathsForMember(ownerId, path);
            changed();
        };
        clear.Click += (_, _) =>
        {
            if (IsPlaying) return;
            var value = GetMemberValue(owner, member);
            if (value is null) return;
            if (value is Array)
                assign(Array.CreateInstance(elementType, 0));
            else if (value is System.Collections.IDictionary dictionary)
                dictionary.Clear();
            else if (value is System.Collections.IList list)
                list.Clear();
            _editScene.Current.References.RemovePathsForMember(ownerId, path);
            changed();
        };
        create.Click += (_, _) =>
        {
            if (IsPlaying) return;
            assign(type.IsArray ? Array.CreateInstance(elementType, 0) : Activator.CreateInstance(type)!);
            changed();
        };
        refresh();
        return root;
    }

    /// <summary>Single nested list row: index label, stretched editor, and remove button on one line.</summary>
    private Grid BuildNestedSequenceRow(object owner, MemberInfo member, Guid ownerId, string path, string automationName, Type elementType, int index, Action refresh)
    {
        var row = new Grid { ColumnSpacing = 6, VerticalAlignment = VerticalAlignment.Center };
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var label = new TextBlock { Text = $"[{index}]", Classes = { "memberType" }, Width = 36, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 0);
        var slot = $"{path}[{index}]";
        var name = $"{automationName}[{index}]";
        object? get() => GetMemberValue(owner, member) is System.Collections.IList list && index >= 0 && index < list.Count ? list[index] : null;
        void set(object? element)
        {
            if (IsPlaying) return;
            if (GetMemberValue(owner, member) is not System.Collections.IList list || index < 0 || index >= list.Count) return;
            list[index] = element;
            _editScene.Current.References.RemovePathsForMember(ownerId, slot);
            refresh();
        }
        var editor = SceneReferenceTypes.IsSingleReference(elementType, _components.Registry)
            ? BuildSingleReferenceEditor(get, set, elementType, ownerId, slot, name, showClear: false)
            : BuildObjectBox(get, set, elementType, name, ownerId, slot);
        AttachEditorDropHandlers(row, editor);
        editor.HorizontalAlignment = HorizontalAlignment.Stretch;
        editor.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(editor, 1);
        var remove = BuildRemoveButton($"{automationName}.Remove[{index}]");
        Grid.SetColumn(remove, 2);
        remove.Click += (_, _) =>
        {
            if (IsPlaying) return;
            var value = GetMemberValue(owner, member);
            if (value is not System.Collections.IList list || index < 0 || index >= list.Count) return;
            var total = list.Count;
            _editScene.Current.References.RemovePathsForMember(ownerId, slot);
            for (var j = index + 1; j < total; j++)
                _editScene.Current.References.MovePath(ownerId, $"{path}[{j}]", $"{path}[{j - 1}]");
            if (value is Array array)
            {
                var next = Array.CreateInstance(elementType, total - 1);
                Array.Copy(array, 0, next, 0, index);
                Array.Copy(array, index + 1, next, index, total - index - 1);
                if (member is FieldInfo field) field.SetValue(owner, next);
                else ((PropertyInfo)member).SetValue(owner, next);
            }
            else
                list.RemoveAt(index);
            refresh();
        };
        row.Children.Add(label);
        row.Children.Add(editor);
        row.Children.Add(remove);
        return row;
    }

    /// <summary>Single nested dictionary row: key box, stretched value editor, and remove button on one line.</summary>
    private Grid BuildNestedDictionaryRow(object owner, MemberInfo member, Guid ownerId, string path, string automationName, Type elementType, string key, int rowIndex, Action refresh)
    {
        var row = new Grid { ColumnSpacing = 6, VerticalAlignment = VerticalAlignment.Center };
        row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var slot = SceneReferenceStore.DictionaryPath(path, key);
        var name = $"{automationName}.Value[{rowIndex}]";
        var keyBox = new TextBox { Text = key, MinWidth = 40, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        keyBox.Classes.Add("inspectorField");
        keyBox.SetValue(AutomationProperties.NameProperty, $"{automationName}.Key[{rowIndex}]");
        ToolTip.SetTip(keyBox, "Dictionary key — must be unique and non-empty");
        keyBox.TextChanged += (_, _) =>
        {
            if (IsPlaying) return;
            var next = keyBox.Text ?? "";
            if (GetMemberValue(owner, member) is not System.Collections.IDictionary current) return;
            if (next.Length == 0 || (next != key && current.Contains(next)))
            {
                MarkInvalid(keyBox, "Enter a unique non-empty key");
                return;
            }
            if (next == key)
            {
                MarkInvalid(keyBox, null, "Dictionary key — must be unique and non-empty");
                return;
            }
            var preserved = current[key];
            current.Remove(key);
            current.Add(next, preserved);
            _editScene.Current.References.MovePath(ownerId, slot, SceneReferenceStore.DictionaryPath(path, next));
            MarkInvalid(keyBox, null, "Dictionary key — must be unique and non-empty");
            refresh();
        };
        keyBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            keyBox.Text = key;
            e.Handled = true;
        };
        Grid.SetColumn(keyBox, 0);
        row.Children.Add(keyBox);
        object? get() => GetMemberValue(owner, member) is System.Collections.IDictionary current && current.Contains(key) ? current[key] : null;
        void set(object? element)
        {
            if (IsPlaying) return;
            if (GetMemberValue(owner, member) is not System.Collections.IDictionary current || !current.Contains(key)) return;
            current[key] = element;
            _editScene.Current.References.RemovePathsForMember(ownerId, slot);
            refresh();
        }
        var valueEditor = SceneReferenceTypes.IsSingleReference(elementType, _components.Registry)
            ? BuildSingleReferenceEditor(get, set, elementType, ownerId, slot, name, showClear: false)
            : BuildObjectBox(get, set, elementType, name, ownerId, slot);
        AttachEditorDropHandlers(row, valueEditor);
        valueEditor.HorizontalAlignment = HorizontalAlignment.Stretch;
        valueEditor.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(valueEditor, 1);
        row.Children.Add(valueEditor);
        var remove = BuildRemoveButton($"{automationName}.Remove[{rowIndex}]");
        Grid.SetColumn(remove, 2);
        remove.Click += (_, _) =>
        {
            if (IsPlaying) return;
            if (GetMemberValue(owner, member) is System.Collections.IDictionary current)
                current.Remove(key);
            _editScene.Current.References.RemovePathsForMember(ownerId, slot);
            refresh();
        };
        row.Children.Add(remove);
        return row;
    }
}
