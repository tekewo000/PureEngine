using System.Globalization;
using System.Numerics;
using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PureEngine.Core;
using Color = Avalonia.Media.Color;
using PureEngine.Runtime;

namespace PureEngine.Editor;

public partial class MainWindow : Window
{
    /// <summary>Owner of the edit scene, path, and dirty state. Centralizes edit-state changes across partials.</summary>
    private readonly EditSceneStore _editScene = new(new Scene());
    /// <summary>Owner of code-reload preparation, adoption, cleanup, and pending state. Verifiable without a UI.</summary>
    private readonly UserCodeReloadCoordinator _reloadCoordinator = new();
    /// <summary>Type owner for this window (project). Serializer, attach, Play, and reload all use it explicitly.</summary>
    internal ProjectComponents _components;
    private static readonly DataFormat<Type> ComponentFormat =
        DataFormat.CreateInProcessFormat<Type>("PureEngine.ComponentType");
    private static readonly DataFormat<IReadOnlyList<Type>> ComponentTypesFormat =
        DataFormat.CreateInProcessFormat<IReadOnlyList<Type>>("PureEngine.ComponentTypes");
    private PointerPressedEventArgs? _assetPress;
    private Point _assetPressPosition;
    private IReadOnlyList<Type>? _dragTypes;
    private readonly HashSet<TextBox> _invalidFields = [];
    /// <summary>Collapsed state of Component cards. Kept per type and preserved across selection changes.</summary>
    private readonly Dictionary<string, bool> _collapsedCards = [with(StringComparer.Ordinal)];
    /// <summary>Collapsed state of collection-member (List/Dictionary) element lists. Kept per member.</summary>
    private readonly Dictionary<string, bool> _collapsedMembers = [with(StringComparer.Ordinal)];
    private bool _viewportFitted;

    /// <summary>Whether UI-driven input errors exist. Passed as a value to gate decisions.</summary>
    internal bool HasInputErrors => _invalidFields.Count > 0 || (NameError?.IsVisible == true);

    internal EditSceneStore EditSceneStore => _editScene;

    internal UserCodeReloadCoordinator ReloadCoordinator => _reloadCoordinator;

    internal GameSession EditSession => _editScene.Services;

    public MainWindow(ProjectSession session) : this()
    {
        ArgumentNullException.ThrowIfNull(session);
        _editScene.ReplaceServices(session.EditServices).Dispose();
        // Transfers Session ownership (Components and Scene) to this window. The transferred Session is not disposed.
        var placeholder = _components;
        _components = session.Components;
        session.TransferOwnership();
        try { placeholder.Dispose(); } catch { }
        try
        {
            _sceneSerializer = new SceneSerializer(_components.Registry);
            _project = session.Project;
            // Adopt the scene before asset I/O so constructor failure cleanup owns its components.
            SetCurrentScene(session.Scene, session.Project.StartupScenePath);
            RefreshProjectAssets();
            // Rebuild before showing the window; the initial Inspector used the placeholder asset index.
            RefreshComponents();
            if (session.SceneNeedsSave) MarkSceneChanged();
            ProjectTab.IsSelected = true;
            StartUserCodeWatching();
        }
        catch (Exception error)
        {
            try { CloseEditSession(); }
            catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
            throw;
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        // Only once at startup, adjusts the bottom pane height from the live Scene View/Game width so it is 16:9.
        CenterGrid.LayoutUpdated += OnCenterLayoutUpdated;
        // Per-project type owner. Held independently even for empty projects (tests, unopened).
        _components = new ProjectComponents();
        _sceneSerializer = new SceneSerializer(_components.Registry);
        // Dedicated service set for the editing lifetime. Built from the same registrations, kept independent from Play.
        Closed += (_, _) => CloseEditSession();
        Closing += OnEditorClosing;
        AddHandler(KeyDownEvent, OnFileShortcut, RoutingStrategies.Tunnel);
        UpdateSceneTitle();
        InitHierarchy();
        ObjectName.TextChanged += OnObjectNameChanged;
        RefreshProjectExplorer();
        SceneSurface.AddHandler(PointerPressedEvent, OnScenePointerPressed, RoutingStrategies.Tunnel);
        ProjectFiles.AddHandler(PointerPressedEvent, OnAssetPressed, RoutingStrategies.Tunnel);
        ProjectFiles.AddHandler(PointerMovedEvent, OnAssetMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        ProjectFiles.AddHandler(PointerReleasedEvent, (_, _) => _assetPress = null,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        foreach (var surface in new Control[] { SceneSurface, InspectorPane })
        {
            surface.AddHandler(DragDrop.DragOverEvent, OnComponentDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
            surface.AddHandler(DragDrop.DropEvent, OnComponentDrop, RoutingStrategies.Bubble, handledEventsToo: true);
        }
        InitProjectDrop();
        InitPlayControls();
        InitConsole();
        var viewport = new PureEngine.Rendering.Avalonia.VulkanViewport();
        viewport.RenderingFailed += error => Log.Engine.Error(error);
        ConnectPreviewViewport(viewport);
        RefreshProjectAssets();
        SceneViewport.Children.Add(viewport);
        var gameViewport = new PureEngine.Rendering.Avalonia.VulkanViewport();
        gameViewport.RenderingFailed += error => Log.Engine.Error(error);
        ConnectGameViewport(gameViewport);
        GameViewport.Children.Add(gameViewport);
        InitSceneView();
        InitGameInput();
    }

    private void OnAssetPressed(object? sender, PointerPressedEventArgs e)
    {
        _assetPress = null;
        _dragTypes = null;
        if (!e.GetCurrentPoint(ProjectFiles).Properties.IsLeftButtonPressed) return;
        var entry = ((e.Source as Visual)?.GetSelfAndVisualAncestors()
            .OfType<ListBoxItem>().FirstOrDefault()?.DataContext as ProjectExplorerEntry);
        if (entry is null) return;
        if (entry.ComponentType is not null)
        {
            _dragTypes = [entry.ComponentType];
        }
        else if (entry.Kind == ProjectExplorerKind.File && entry.FullPath is not null
            && entry.FullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            // C# files in the Project column can drag in attachable classes via D&D.
            // When one file holds multiple classes, attaches all unattached classes from that file.
            var types = _components.GetTypesForFile(entry.FullPath);
            if (types.Count == 0) return;
            _dragTypes = types;
        }
        else return;
        _assetPressPosition = e.GetPosition(ProjectFiles);
        _assetPress = e;
    }

    private async void OnAssetMoved(object? sender, PointerEventArgs e)
    {
        if (_assetPress is null) return;
        if (!e.GetCurrentPoint(ProjectFiles).Properties.IsLeftButtonPressed)
        {
            _assetPress = null;
            _dragTypes = null;
            return;
        }
        var delta = e.GetPosition(ProjectFiles) - _assetPressPosition;
        if (Math.Abs(delta.X) < 4 && Math.Abs(delta.Y) < 4) return;
        var press = _assetPress;
        var types = _dragTypes!;
        _assetPress = null;
        _dragTypes = null;
        using var data = new DataTransfer();
        data.Add(DataTransferItem.Create(ComponentTypesFormat, types));
        if (types.Count == 1)
            data.Add(DataTransferItem.Create(ComponentFormat, types[0]));
        await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Copy);
    }

    private SceneObject? DropTarget(object? sender, DragEventArgs e) =>
        ReferenceEquals(sender, InspectorPane)
            ? GetSelectedSceneObject()
            : FindHierarchyNode(e.Source as Visual)?.Ref;

    private void OnComponentDragOver(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(ComponentTypesFormat) && !e.DataTransfer.Contains(ComponentFormat)) return;
        if (IsPlaying)
        {
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        var target = DropTarget(sender, e);
        var canAny = false;
        foreach (var type in GetDragTypes(e))
        {
            if (_components.CanAttach(target, type)) { canAny = true; break; }
        }
        e.DragEffects = canAny ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private static IReadOnlyList<Type> GetDragTypes(DragEventArgs e)
    {
        var list = e.DataTransfer.TryGetValue(ComponentTypesFormat);
        if (list is not null && list.Count > 0) return list;
        var single = e.DataTransfer.TryGetValue(ComponentFormat);
        return single is not null ? [single] : [];
    }

    private void OnComponentDrop(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(ComponentTypesFormat) && !e.DataTransfer.Contains(ComponentFormat)) return;
        e.Handled = true;
        e.DragEffects = DragDropEffects.None;
        if (RejectWhenPlaying("Attach")) return;
        var target = DropTarget(sender, e);
        var types = GetDragTypes(e).Where(t => _components.CanAttach(target, t)).ToArray();
        if (types.Length == 0) return;
        SelectSceneObject(target, focus: false);
        var attached = 0;
        string? firstError = null;
        Type? errorType = null;
        foreach (var type in types)
        {
            try
            {
                if (!_components.TryAttach(target, type, EditSession.Factory)) continue;
                attached++;
            }
            catch (Exception error)
            {
                if (firstError is null)
                {
                    firstError = error.GetBaseException().Message;
                    errorType = type;
                }
            }
        }
        if (attached > 0)
        {
            MarkSceneChanged();
            RefreshComponents();
            e.DragEffects = DragDropEffects.Copy;
        }
        if (firstError is not null)
        {
            AttachError.Text = $"Could not add {errorType!.Name}: {firstError}";
            AttachError.IsVisible = true;
        }
    }

    private void RefreshComponents()
    {
        ComponentEditors.Children.Clear();
        _invalidFields.Clear();
        if (_assetEdit is not null && GetSelectedSceneObject() is null)
        {
            AttachedClasses.IsVisible = false;
            NoComponentsHint.IsVisible = false;
            AttachError.IsVisible = false;
            ComponentsHeader.Text = "Components";
            RefreshDataAssetInspector();
            return;
        }
        DataAssetInspector.IsVisible = false;
        var item = GetSelectedSceneObject();
        var components = item?.Components.ToArray() ?? [];
        AttachedClasses.IsVisible = item is not null;
        NoComponentsHint.IsVisible = item is not null && components.Length == 0;
        AttachError.IsVisible = false;
        ComponentsHeader.Text = $"Components ({components.Length})";
        UpdateErrorBadge();
        if (item is null) return;
        foreach (var component in components)
            ComponentEditors.Children.Add(BuildComponentCard(item, component));
    }

    private void UpdateErrorBadge()
    {
        ComponentsError.IsVisible = _invalidFields.Count > 0;
        ComponentsError.Text = $"Error {_invalidFields.Count}";
        ToolTip.SetTip(ComponentsError, _invalidFields.Count > 0
            ? $"{_invalidFields.Count} field(s) have invalid input — fix the highlighted fields to save."
            : null);
        DataAssetInvalid.IsVisible = _invalidFields.Count > 0;
        DataAssetInvalid.Text = $"Error {_invalidFields.Count}";
        ToolTip.SetTip(DataAssetInvalid, _invalidFields.Count > 0
            ? $"{_invalidFields.Count} field(s) have invalid input — fix the highlighted fields to save."
            : null);
    }

    /// <summary>Collapse toggle colors. The glyph always carries the structural accent; collapsed state adds a wash fill.</summary>
    private static readonly SolidColorBrush CollapseToggleBrush = new(Color.Parse("#8B7CF6"));
    private static readonly SolidColorBrush CollapseToggleWashBrush = new(Color.Parse("#2E2A4A"));

    /// <summary>Collapse toggle shared by Component cards and collection editors. Keeps state in <paramref name="store"/>.</summary>
    /// <remarks>Uses a Button base. ToggleButton would paint accent directly on template parts in its Fluent theme checked state,
    /// which the transparent style cannot fully remove.</remarks>
    private static Avalonia.Controls.Button BuildCollapseToggle(string automationName, string collapseKey, Dictionary<string, bool> store, Action<bool> apply)
    {
        var expandedState = !store.TryGetValue(collapseKey, out var collapsed) || !collapsed;
        var toggle = new Avalonia.Controls.Button
        {
            Content = expandedState ? "▾" : "▸",
            // Fixed square so the ▾/▸ swap never shifts the button, title, or header buttons.
            Width = 18,
            Height = 18,
            Padding = new Thickness(0),
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Foreground = CollapseToggleBrush,
            Background = expandedState ? Brushes.Transparent : CollapseToggleWashBrush,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        toggle.Classes.Add("collapseToggle");
        toggle.SetValue(AutomationProperties.NameProperty, automationName);
        ToolTip.SetTip(toggle, expandedState ? "Collapse" : "Expand");
        toggle.Click += (_, _) =>
        {
            expandedState = !expandedState;
            toggle.Content = expandedState ? "▾" : "▸";
            toggle.Background = expandedState ? Brushes.Transparent : CollapseToggleWashBrush;
            ToolTip.SetTip(toggle, expandedState ? "Collapse" : "Expand");
            store[collapseKey] = !expandedState;
            apply(expandedState);
        };
        apply(expandedState);
        return toggle;
    }

    private Border BuildComponentCard(SceneObject item, object component)
    {
        var type = component.GetType();
        var members = ComponentSchema.GetInspectorMembers(type);
        var priorityFields = BuildPriorityFields(item, component);
        // Divider before each row so tall editors (Transform, List, Dictionary) read as separate blocks.
        var content = new StackPanel { Spacing = 8 };
        foreach (var member in members)
        {
            content.Children.Add(new Separator { Classes = { "divider" }, Margin = new Thickness(0, 2) });
            content.Children.Add(BuildMemberRow(component, member));
        }
        if (members.Count == 0 && priorityFields.Count == 0)
            content.Children.Add(new TextBlock { Classes = { "cardMeta" }, Text = "No editable fields" });
        var collapseKey = type.FullName ?? type.Name;
        var toggle = BuildCollapseToggle($"{type.Name}.Collapse", collapseKey, _collapsedCards,
            nowExpanded => content.IsVisible = nowExpanded);
        var body = new StackPanel { Spacing = 8 };
        var header = new Grid { ColumnDefinitions = [with("*,Auto")], ColumnSpacing = 8 };
        // Collapse toggle shares the title cell so the header Grid keeps a single
        // title TextBlock and the right-docked priority panel (see PriorityInspectorChecks).
        header.Children.Add(toggle);
        var title = new TextBlock { Text = type.Name, FontSize = 13, FontWeight = FontWeight.SemiBold,
            Foreground = CardTitleBrush,
            MaxWidth = 160, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(22, 0, 0, 0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        ToolTip.SetTip(title, type.FullName);
        header.Children.Add(title);
        var priorities = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        foreach (var field in priorityFields)
            priorities.Children.Add(field);
        Grid.SetColumn(priorities, 1);
        header.Children.Add(priorities);
        body.Children.Add(header);
        body.Children.Add(BuildUiWarning(item, component));
        body.Children.Add(content);
        var remove = new MenuItem { Header = "Remove" };
        var card = new Border
        {
            Classes = { "componentCard" }, Child = body, Tag = component,
            ContextMenu = new ContextMenu { Items = { remove } },
        };
        card.ContextMenu.Opening += (_, _) => remove.IsEnabled = !IsPlaying;
        remove.Click += (_, _) =>
        {
            if (RejectWhenPlaying("Remove") || !ComponentEditors.Children.Contains(card)) return;
            if (!item.Detach(component)) return;
            foreach (var box in card.GetVisualDescendants().OfType<TextBox>())
                _invalidFields.Remove(box);
            ComponentEditors.Children.Remove(card);
            ComponentsHeader.Text = $"Components ({item.Components.Count})";
            NoComponentsHint.IsVisible = item.Components.Count == 0;
            UpdateErrorBadge();
            MarkSceneChanged();
            RefreshUiWarnings(item);
            try { ComponentAssets.DisposeComponents([component]); }
            catch (Exception error) { SetFileStatus(error.ToString(), true); }
            QueuePendingUserCodeReload();
        };
        return card;
    }

    /// <summary>Attach settings, separate from Inspector members. Only lifecycles present on the class are shown.</summary>
    private List<Control> BuildPriorityFields(SceneObject item, object component)
    {
        List<Control> rows = [];
        bool hasStart, hasUpdate, hasDestroy;
        try
        {
            var type = component.GetType();
            hasStart = ComponentSchema.GetStartMethod(type) is not null;
            hasUpdate = ComponentSchema.GetUpdateMethod(type) is not null;
            hasDestroy = ComponentSchema.GetDestroyMethod(type) is not null;
        }
        catch (InvalidOperationException)
        {
            return rows;
        }
        if (!hasStart && !hasUpdate && !hasDestroy) return rows;
        if (hasStart) rows.Add(BuildPriorityField(item, component, ComponentLifecycle.Start, "Start Priority"));
        if (hasUpdate) rows.Add(BuildPriorityField(item, component, ComponentLifecycle.Update, "Update Priority"));
        if (hasDestroy) rows.Add(BuildPriorityField(item, component, ComponentLifecycle.Destroy, "Destroy Priority"));
        return rows;
    }

    private StackPanel BuildPriorityField(SceneObject item, object component, ComponentLifecycle kind, string displayName)
    {
        var type = component.GetType();
        var shortLabel = kind switch
        {
            ComponentLifecycle.Start => "S",
            ComponentLifecycle.Update => "U",
            _ => "D",
        };
        var accent = kind switch
        {
            ComponentLifecycle.Start => StartAccent,
            ComponentLifecycle.Update => UpdateAccent,
            _ => DestroyAccent,
        };
        var badgeBackground = kind switch
        {
            ComponentLifecycle.Start => StartBadgeBackground,
            ComponentLifecycle.Update => UpdateBadgeBackground,
            _ => DestroyBadgeBackground,
        };
        Func<int> getter = kind switch
        {
            ComponentLifecycle.Start => () => item.GetStartPriority(component),
            ComponentLifecycle.Update => () => item.GetUpdatePriority(component),
            _ => () => item.GetDestroyPriority(component),
        };
        Action<int> setter = kind switch
        {
            ComponentLifecycle.Start => value => item.SetStartPriority(component, value),
            ComponentLifecycle.Update => value => item.SetUpdatePriority(component, value),
            _ => value => item.SetDestroyPriority(component, value),
        };
        var letter = new TextBlock
        {
            Text = shortLabel,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = accent,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        ToolTip.SetTip(letter, displayName);
        var badge = new Border
        {
            Background = badgeBackground,
            CornerRadius = new CornerRadius(4),
            Width = 20,
            Height = 20,
            Child = letter,
        };
        ToolTip.SetTip(badge, displayName);
        var box = new TextBox
        {
            Text = getter().ToString(CultureInfo.InvariantCulture),
            Width = 24, MinHeight = 22, Height = 22,
            FontSize = 10, Padding = new Thickness(2, 1),
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            BorderBrush = accent,
            CornerRadius = new CornerRadius(4),
        };
        ToolTip.SetTip(box, displayName);
        box.Classes.Add("inspectorField");
        box.SetValue(AutomationProperties.NameProperty, $"{type.Name}.{kind}Priority");
        box.TextChanged += (_, _) =>
        {
            if (IsPlaying || !box.GetVisualAncestors().Contains(ComponentEditors)) return;
            if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                if (getter() != value)
                {
                    setter(value);
                    MarkSceneChanged();
                }
                MarkInvalid(box, null);
                box.BorderBrush = accent;
                ToolTip.SetTip(box, displayName);
            }
            else
            {
                MarkInvalid(box, "Enter an integer");
            }
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            box.Text = getter().ToString(CultureInfo.InvariantCulture);
            e.Handled = true;
        };
        var field = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        field.Children.Add(badge);
        field.Children.Add(box);
        ToolTip.SetTip(field, displayName);
        return field;
    }

    private Grid BuildMemberRow(object component, MemberInfo member)
    {
        var memberType = GetMemberType(member);
        var friendlyType = FriendlyTypeName(memberType);
        return BuildLabeledEditorRow(member.Name, $"{member.Name} : {friendlyType}", friendlyType,
            BuildMemberEditor(component, member));
    }

    /// <summary>Builds a member row with an explicit Automation name for nested custom classes. Looks the same as <see cref="BuildMemberRow"/>.</summary>
    private Grid BuildNestedMemberRow(object owner, MemberInfo member, string automationName, Guid? topOwnerId = null, string? storePath = null, Action? refreshParent = null)
    {
        var memberType = GetMemberType(member);
        var friendlyType = FriendlyTypeName(memberType);
        Control editor;
        var effectiveOwnerId = topOwnerId;
        var effectiveStore = storePath;
        if (effectiveOwnerId is not null && effectiveStore is not null)
        {
            if (SceneReferenceTypes.IsSingleReference(memberType, _components.Registry))
            {
                var capturedOwner = owner;
                var capturedMember = member;
                var capturedPath = effectiveStore;
                var capturedId = effectiveOwnerId.Value;
                var capturedAutomation = automationName;
                var capturedRefresh = refreshParent ?? RefreshComponents;
                editor = BuildNestedReferenceEditor(capturedOwner, capturedMember, capturedId, capturedPath, capturedAutomation, capturedRefresh);
                return BuildLabeledEditorRow(member.Name, $"{member.Name} : {friendlyType}", friendlyType, editor);
            }
            if (SceneReferenceTypes.ContainsReference(memberType, _components.Registry)
                && (memberType.IsArray || (memberType.IsGenericType && memberType.GetGenericTypeDefinition() == typeof(List<>))))
            {
                editor = BuildNestedCollectionEditor(owner, member, effectiveOwnerId.Value, effectiveStore, automationName);
                return BuildLabeledEditorRow(member.Name, $"{member.Name} : {friendlyType}", friendlyType, editor);
            }
            if (SceneReferenceTypes.ContainsReference(memberType, _components.Registry)
                && memberType.IsGenericType && memberType.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                editor = BuildNestedCollectionEditor(owner, member, effectiveOwnerId.Value, effectiveStore, automationName);
                return BuildLabeledEditorRow(member.Name, $"{member.Name} : {friendlyType}", friendlyType, editor);
            }
            if (InspectorValueTypes.IsCustomInspectorObject(memberType) || SceneReferenceTypes.ContainsReference(memberType, _components.Registry))
            {
                var objectType = memberType;
                var capturedBase = effectiveStore;
                var capturedId2 = effectiveOwnerId.Value;
                var capturedAutomation2 = automationName;
                var capturedRefresh2 = refreshParent ?? RefreshComponents;
                editor = BuildObjectBox(
                    () => GetMemberValue(owner, member),
                    value =>
                    {
                        if (IsPlaying)
                            return;
                        switch (member)
                        {
                            case FieldInfo field: field.SetValue(owner, value); break;
                            case PropertyInfo property: property.SetValue(owner, value); break;
                            default: throw new NotSupportedException($"Unsupported member: {member.Name}");
                        }
                        MarkEdited(owner);
                        capturedRefresh2();
                    },
                    objectType, capturedAutomation2, capturedId2, capturedBase);
                return BuildLabeledEditorRow(member.Name, $"{member.Name} : {friendlyType}", friendlyType, editor);
            }
        }
        editor = BuildMemberEditor(owner, member, automationName);
        return BuildLabeledEditorRow(member.Name, $"{member.Name} : {friendlyType}", friendlyType, editor);
    }

    private static Grid BuildLabeledEditorRow(string label, string tooltip, string typeText, Control editor)
    {
        var row = new Grid { ColumnDefinitions = [with("120,*")], ColumnSpacing = 8 };
        row.Classes.Add("inspectorRow");
        // Two-line label: name (primary) + type (secondary). Type is visible without hover
        // so int/float/string/bool scan at a glance; full "name : type" stays in the tooltip.
        var labelStack = new StackPanel { Spacing = 0, Margin = new Thickness(0, 3, 0, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        var nameBlock = new TextBlock
        {
            Text = label,
            Foreground = MemberLabelBrush,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var typeBlock = new TextBlock
        {
            Classes = { "memberType" },
            Text = typeText,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        labelStack.Children.Add(nameBlock);
        labelStack.Children.Add(typeBlock);
        ToolTip.SetTip(labelStack, tooltip);
        ToolTip.SetTip(nameBlock, tooltip);
        Grid.SetColumn(labelStack, 0);
        // Top-anchored so collapsing a tall editor (List/Dictionary) never sinks its header:
        // a centered editor drops by half the label/editor height gap once it becomes shorter than the label.
        editor.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        Grid.SetColumn(editor, 1);
        row.Children.Add(labelStack);
        row.Children.Add(editor);
        return row;
    }

    private Control BuildMemberEditor(object component, MemberInfo member, string? automationName = null)
    {
        var memberType = GetMemberType(member);
        automationName ??= $"{component.GetType().Name}.{member.Name}";

        if (ShouldShowReferenceEditor(memberType))
            return BuildMemberReferenceEditor(component, member, automationName);
        if ((memberType.IsArray || memberType.IsGenericType)
            && SceneReferenceTypes.ContainsReference(memberType, _components.Registry)
            && GetOwnerComponentId(component) is { } ownerId)
            return SceneReferenceTypes.IsSupportedInspectorType(memberType, _components.Registry)
                ? BuildNestedCollectionEditor(component, member, ownerId, member.Name, automationName)
                : UnsupportedBadge(memberType);

        if (memberType == typeof(string))
        {
            var box = new TextBox
            {
                Text = (string?)GetMemberValue(component, member) ?? "",
                PlaceholderText = "Empty",
            };
            box.Classes.Add("inspectorField");
            box.SetValue(AutomationProperties.NameProperty, automationName);
            ToolTip.SetTip(box, $"{member.Name} : string — Press Esc to revert");
            box.TextChanged += (_, _) =>
            {
                // Displaying a loaded null as an empty field must not change the scene.
                if ((box.Text ?? "") != ((string?)GetMemberValue(component, member) ?? ""))
                    SetMemberValue(component, member, box.Text ?? "");
            };
            AttachEscapeRevert(box, component, member);
            return box;
        }

        if (memberType == typeof(int))
        {
            const string hint = "Enter an integer — Press Esc to revert";
            var box = new TextBox { Text = FormatMemberValue(component, member), PlaceholderText = "0" };
            box.Classes.Add("inspectorField");
            box.SetValue(AutomationProperties.NameProperty, automationName);
            ToolTip.SetTip(box, hint);
            box.TextChanged += (_, _) =>
            {
                if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                {
                    SetMemberValue(component, member, value);
                    MarkInvalid(box, null, hint);
                }
                else
                {
                    MarkInvalid(box, "Enter an integer");
                }
            };
            AttachEscapeRevert(box, component, member);
            return box;
        }

        if (memberType == typeof(float))
        {
            const string hint = "Enter a number — Press Esc to revert";
            var box = new TextBox { Text = FormatMemberValue(component, member), PlaceholderText = "0.0" };
            box.Classes.Add("inspectorField");
            box.SetValue(AutomationProperties.NameProperty, automationName);
            ToolTip.SetTip(box, hint);
            box.TextChanged += (_, _) =>
            {
                if (float.TryParse(box.Text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value)
                    && float.IsFinite(value))
                {
                    SetMemberValue(component, member, value);
                    MarkInvalid(box, null, hint);
                }
                else
                {
                    MarkInvalid(box, "Enter a number");
                }
            };
            AttachEscapeRevert(box, component, member);
            return box;
        }

        if (memberType == typeof(bool))
        {
            var initial = GetMemberValue(component, member) is true;
            var check = new CheckBox { IsChecked = initial, Content = initial ? "True" : "False" };
            check.Classes.Add("inspectorCheck");
            check.SetValue(AutomationProperties.NameProperty, automationName);
            ToolTip.SetTip(check, $"{member.Name} : bool");
            check.IsCheckedChanged += (_, _) =>
            {
                var value = check.IsChecked == true;
                check.Content = value ? "True" : "False";
                SetMemberValue(component, member, value);
            };
            return check;
        }

        if (memberType == typeof(double))
            return BuildDoubleEditor(component, member, automationName);
        if (memberType == typeof(Vector2))
            return BuildVector2Editor(component, member, automationName);
        if (memberType == typeof(Vector3))
            return BuildVector3Editor(component, member, automationName);
        if (memberType == typeof(Vector4))
            return BuildVector4Editor(component, member, automationName, isQuaternion: false);
        if (memberType == typeof(Quaternion))
            return BuildVector4Editor(component, member, automationName, isQuaternion: true);
        if (memberType == typeof(PureEngine.Core.Color))
            return BuildColorEditor(component, member, automationName);
        if (memberType == typeof(PureEngine.Core.Transform))
            return BuildTransformEditor(component, member, automationName);
        if (memberType == typeof(Sprite))
            return BuildSpriteEditor(component, member, automationName);
        if (memberType.IsEnum)
            return BuildEnumEditor(component, member, automationName);
        if (Nullable.GetUnderlyingType(memberType) is not null)
            return BuildNullableEditor(component, member, automationName);
        if (memberType.IsArray || (memberType.IsGenericType && memberType.GetGenericTypeDefinition() == typeof(List<>)))
        {
            if (SceneReferenceTypes.IsSupportedInspectorType(memberType, _components.Registry))
                return BuildSequenceEditor(component, member, automationName);
        }
        if (memberType.IsGenericType && memberType.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            if (SceneReferenceTypes.IsSupportedInspectorType(memberType, _components.Registry))
                return BuildDictionaryEditor(component, member, automationName);
        }
        if (InspectorValueTypes.IsCustomInspectorObject(memberType) || SceneReferenceTypes.ContainsReference(memberType, _components.Registry))
            return BuildObjectEditor(component, member, automationName);

        return UnsupportedBadge(memberType);
    }

    private static readonly SolidColorBrush InvalidBrush = new(Color.Parse("#FF8A80"));
    private static readonly SolidColorBrush InvalidFieldBackground = new(Color.Parse("#3D2A2E"));

    /// <summary>Inspector member labels. Brighter than muted so label/value pairs scan as units.</summary>
    private static readonly SolidColorBrush MemberLabelBrush = new(Color.Parse("#DEE2E8"));
    private static readonly SolidColorBrush CardTitleBrush = new(Color.Parse("#F0F2F5"));
    private static readonly SolidColorBrush UnsupportedBadgeBrush = new(Color.Parse("#B8BDC5"));

    /// <summary>S/U/D priority label accents. The letter stays the primary cue; color is redundant.</summary>
    private static readonly SolidColorBrush StartAccent = new(Color.Parse("#8AB4F8"));
    private static readonly SolidColorBrush UpdateAccent = new(Color.Parse("#81C995"));
    private static readonly SolidColorBrush DestroyAccent = new(Color.Parse("#F28B82"));
    /// <summary>Priority badge chip fills, tinted per lifecycle so S/U/D read apart at a glance.</summary>
    private static readonly SolidColorBrush StartBadgeBackground = new(Color.Parse("#2A3A57"));
    private static readonly SolidColorBrush UpdateBadgeBackground = new(Color.Parse("#24402E"));
    private static readonly SolidColorBrush DestroyBadgeBackground = new(Color.Parse("#472D2D"));
    /// <summary>Neutral badge chip fill for the Quaternion W axis, which has no axis hue.</summary>
    private static readonly SolidColorBrush NeutralBadgeBackground = new(Color.Parse("#333842"));

    private void MarkInvalid(TextBox box, string? message, string? validTip = null)
    {
        if (!box.GetVisualAncestors().Contains(ComponentEditors)
            && !box.GetVisualAncestors().Contains(DataAssetEditors)) return;
        if (message is null)
        {
            box.ClearValue(TextBox.BorderBrushProperty);
            box.ClearValue(TextBox.BackgroundProperty);
            ToolTip.SetTip(box, validTip);
            _invalidFields.Remove(box);
        }
        else
        {
            box.BorderBrush = InvalidBrush;
            box.Background = InvalidFieldBackground;
            ToolTip.SetTip(box, message);
            _invalidFields.Add(box);
        }
        UpdateErrorBadge();
        QueuePendingUserCodeReload();
    }

    /// <summary>Reverts an editing numeric field to its last valid value on Esc. Invalid display is also cleared via TextChanged.</summary>
    private static void AttachEscapeRevert(TextBox box, object component, MemberInfo member) =>
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            box.Text = GetMemberType(member) == typeof(string)
                ? (string?)GetMemberValue(component, member) ?? ""
                : FormatMemberValue(component, member);
            e.Handled = true;
        };

    private static string FormatMemberValue(object component, MemberInfo member) =>
        GetMemberType(member) is var type && (type == typeof(float) || type == typeof(double))
            ? Convert.ToString(GetMemberValue(component, member), CultureInfo.InvariantCulture) ?? "0"
            : GetMemberValue(component, member)?.ToString() ?? "0";

    private static Type GetMemberType(MemberInfo member) =>
        member switch
        {
            FieldInfo field => field.FieldType,
            PropertyInfo property => property.PropertyType,
            _ => throw new NotSupportedException($"Unsupported member: {member.Name}"),
        };

    /// <summary>Rewrites leaked CLR names (Int32, List`1, Dictionary`2, Nullable`1) into human-readable form. Display only.</summary>
    private static string FriendlyTypeName(Type type) =>
        type == typeof(string) ? "string" :
        type == typeof(int) ? "int" :
        type == typeof(float) ? "float" :
        type == typeof(double) ? "double" :
        type == typeof(bool) ? "bool" :
        type == typeof(Vector2) ? nameof(Vector2) :
        type == typeof(Vector3) ? nameof(Vector3) :
        type == typeof(Vector4) ? nameof(Vector4) :
        type == typeof(Quaternion) ? nameof(Quaternion) :
        type == typeof(PureEngine.Core.Color) ? nameof(PureEngine.Core.Color) :
        type == typeof(PureEngine.Core.Transform) ? nameof(PureEngine.Core.Transform) :
        Nullable.GetUnderlyingType(type) is { } underlying ? $"{FriendlyTypeName(underlying)}?" :
        type.IsArray ? $"{FriendlyTypeName(type.GetElementType()!)}[]" :
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>) ? $"List<{FriendlyTypeName(type.GetGenericArguments()[0])}>" :
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>) ? $"Dictionary<{FriendlyTypeName(type.GetGenericArguments()[0])}, {FriendlyTypeName(type.GetGenericArguments()[1])}>" :
        type.IsEnum ? type.Name :
        type.IsGenericType ? $"{type.Name.Split('`')[0]}<{string.Join(", ", type.GetGenericArguments().Select(FriendlyTypeName))}>" :
        type.Name;

    private static object? GetMemberValue(object component, MemberInfo member) =>
        member switch
        {
            FieldInfo field => field.GetValue(component),
            PropertyInfo property => property.GetValue(component),
            _ => throw new NotSupportedException($"Unsupported member: {member.Name}"),
        };

    private void SetMemberValue(object component, MemberInfo member, object? value)
    {
        if (IsPlaying) return;
        if (Equals(GetMemberValue(component, member), value)) return;
        switch (member)
        {
            case FieldInfo field: field.SetValue(component, value); break;
            case PropertyInfo property: property.SetValue(component, value); break;
            default: throw new NotSupportedException($"Unsupported member: {member.Name}");
        }
        MarkEdited(component);
    }

    private void OnScenePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(SceneSurface).Properties.IsRightButtonPressed) return;
        SelectSceneObject(FindHierarchyNode(e.Source as Avalonia.Visual)?.Ref, focus: true);
    }

    private void OnAddUiImage(object? sender, RoutedEventArgs e) =>
        AddUiObject("Image", [typeof(Core.Transform), typeof(UiElement), typeof(Core.Image)]);

    private void OnAddUiButton(object? sender, RoutedEventArgs e) =>
        AddUiObject("Button", [typeof(Core.Transform), typeof(UiElement), typeof(Core.Image), typeof(Core.Button)]);

    private void OnAddUiText(object? sender, RoutedEventArgs e) =>
        AddUiObject("Text", [typeof(Core.Transform), typeof(UiElement), typeof(Core.Text)]);

    /// <summary>Creates the required UI setup using the current parent selection and the Component creation path.</summary>
    private void AddUiObject(string baseName, Type[] componentTypes)
    {
        if (RejectWhenPlaying("Add")) return;
        var parent = GetSelectedSceneObject();
        var item = _editScene.Current.AddNamed(baseName);
        try
        {
            foreach (var type in componentTypes)
                if (!_components.TryAttach(item, type, EditSession.Factory))
                    throw new InvalidOperationException($"Could not attach {type.Name}.");
            if (parent is not null) item.SetParent(parent);
        }
        catch (Exception error)
        {
            _editScene.Current.Remove(item);
            try { ComponentAssets.DisposeComponents(item.Components); }
            catch (Exception cleanupError) { Log.Engine.Error(cleanupError); }
            SetFileStatus($"Could not create UI: {error.GetBaseException().Message}", true);
            return;
        }
        MarkSceneChanged();
        RefreshHierarchy(item.Id, expandId: parent?.Id);
        RefreshObjectInspector();
        SceneObjects.Focus();
    }

    private void OnAddObject(object? sender, RoutedEventArgs e)
    {
        if (RejectWhenPlaying("Add")) return;
        var parent = GetSelectedSceneObject();
        var item = _editScene.Current.AddEmpty();
        if (parent is not null) item.SetParent(parent);
        MarkSceneChanged();
        RefreshHierarchy(item.Id, expandId: parent?.Id);
        RefreshObjectInspector();
        SceneObjects.Focus();
    }

    private async void OnObjectSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_hierarchyRefreshing || _assetSelectionChanging) return;
        if (GetSelectedSceneObject() is not null && _assetEdit is not null)
        {
            _assetSelectionChanging = true;
            try
            {
                await RunFileOperation(async () =>
                {
                    if (!await ConfirmCloseDataAsset()) SelectSceneObject(null, focus: false);
                    else RefreshObjectInspector();
                });
            }
            finally { _assetSelectionChanging = false; }
            return;
        }
        if (_sceneMoveKind is not PureEngine.Core.SceneViewMath.GizmoKind.None
            && !ReferenceEquals(GetSelectedSceneObject(), _dragTarget))
            CancelSceneViewDrag();
        RefreshObjectInspector();
    }

    private void RefreshObjectInspector()
    {
        var item = GetSelectedSceneObject();
        if (_assetEdit is not null && item is null)
        {
            DeleteObjectMenuItem.IsEnabled = false;
            RefreshDataAssetInspector();
            return;
        }
        DataAssetInspector.IsVisible = false;
        DeleteObjectMenuItem.IsEnabled = item is not null && !IsPlaying;
        ObjectInspector.IsVisible = item is not null;
        ObjectName.Text = item?.Name ?? "";
        var id = item?.Id.ToString() ?? "";
        ObjectId.Text = id;
        ToolTip.SetTip(ObjectId, id);
        NameError.IsVisible = false;
        RefreshComponents();
    }

    private void OnObjectNameChanged(object? sender, TextChangedEventArgs e)
    {
        if (IsPlaying) return;
        if (GetSelectedSceneObject() is not SceneObject item) return;
        NameError.IsVisible = string.IsNullOrWhiteSpace(ObjectName.Text);
        if (!NameError.IsVisible && item.Name != ObjectName.Text!.Trim())
        {
            item.Rename(ObjectName.Text!);
            MarkSceneChanged();
        }
        QueuePendingUserCodeReload();
    }

    private void OnDeleteObject(object? sender, RoutedEventArgs e) => DeleteSelectedObject();

    private void OnSceneKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        DeleteSelectedObject();
        e.Handled = true;
    }

    private void DeleteSelectedObject()
    {
        if (RejectWhenPlaying("Delete")) return;
        if (GetSelectedSceneObject() is not SceneObject item) return;
        CancelSceneViewDrag();
        var siblings = item.Parent is null ? _editScene.Current.RootObjects : item.Parent.Children;
        var siblingIndex = IndexOfSceneObject(siblings, item);
        var next = siblingIndex >= 0 && siblingIndex + 1 < siblings.Count ? siblings[siblingIndex + 1]
            : siblingIndex > 0 ? siblings[siblingIndex - 1]
            : item.Parent;
        List<object> doomed = [.. item.Components];
        var stack = new Stack<SceneObject>(item.Children);
        while (stack.Count > 0)
        {
            var descendant = stack.Pop();
            doomed.AddRange(descendant.Components);
            foreach (var child in descendant.Children)
                stack.Push(child);
        }
        _editScene.Current.Remove(item);
        MarkSceneChanged();
        RefreshHierarchy(next?.Id);
        RefreshObjectInspector();
        SceneObjects.Focus();
        try { ComponentAssets.DisposeComponents(doomed); }
        catch (Exception error) { SetFileStatus(error.ToString(), true); }
    }

    private static int IndexOfSceneObject(IReadOnlyList<SceneObject> items, SceneObject item)
    {
        for (var i = 0; i < items.Count; i++)
            if (ReferenceEquals(items[i], item)) return i;
        return -1;
    }

    private void OnPanePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is TabControl pane && !pane.IsKeyboardFocusWithin)
            pane.Focus();
    }

    private void OnCenterLayoutUpdated(object? sender, EventArgs e)
    {
        if (_viewportFitted) return;
        FitViewportToSixteenNine();
    }

    /// <summary>
    /// Only once at startup, keeps the Scene View/Game viewport width fixed so the video area becomes 16:9,
    /// by adjusting the bottom pane height. Subtracts measured chrome such as tab headers. Afterwards the splitter can be moved freely.
    /// </summary>
    private void FitViewportToSixteenNine()
    {
        if (CenterGrid.Bounds.Height <= 0 || SceneViewPane.Bounds.Width <= 0) return;
        var viewport = (Control)(ViewportTabs.SelectedIndex == 1 ? GameViewport : SceneViewport);
        if (viewport.Bounds.Width <= 0 || viewport.Bounds.Height <= 0) return;
        var chrome = Math.Max(0, SceneViewPane.Bounds.Height - viewport.Bounds.Height);
        var targetPane = viewport.Bounds.Width * 9.0 / 16.0 + chrome;
        var viewportRow = CenterGrid.RowDefinitions[0];
        var bottomRow = CenterGrid.RowDefinitions[2];
        targetPane = Math.Clamp(targetPane, viewportRow.MinHeight,
            CenterGrid.Bounds.Height - 8 - bottomRow.MinHeight);
        var bottom = CenterGrid.Bounds.Height - 8 - targetPane;
        if (bottom < bottomRow.MinHeight) return;
        bottomRow.Height = new GridLength(bottom, GridUnitType.Pixel);
        _viewportFitted = true;
    }
}
