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
    public EditorViewModel ViewModel { get; } = new();
    private EditorDocuments Documents => ViewModel.Documents;
    internal EditSceneStore SceneDocument => Documents.Scene;
    internal EditSceneStore? PrefabDocument => Documents.Prefab;
    /// <summary>Type owner for this window (project). Serializer, attach, Play, and reload all use it explicitly.</summary>
    internal ProjectComponents Components => ViewModel.Components;
    private static readonly DataFormat<Type> ComponentFormat =
        DataFormat.CreateInProcessFormat<Type>("PureEngine.ComponentType");
    private static readonly DataFormat<IReadOnlyList<Type>> ComponentTypesFormat =
        DataFormat.CreateInProcessFormat<IReadOnlyList<Type>>("PureEngine.ComponentTypes");
    private PointerPressedEventArgs? _assetPress;
    private Point _assetPressPosition;
    private IReadOnlyList<Type>? _dragTypes;
    private readonly Dictionary<TextBox, Guid> _inputIds = [];
    private bool _viewportFitted;

    /// <summary>Whether UI-driven input errors exist. Passed as a value to gate decisions.</summary>
    internal bool HasInputErrors => ViewModel.Inspector.HasInputErrors;

    internal EditSceneStore EditSceneStore => Documents.Current;

    internal UserCodeReloadCoordinator ReloadCoordinator => ViewModel.Compilation.Coordinator;

    internal GameSession EditSession => Documents.Current.Services;

    public MainWindow(ProjectSession session) : this()
    {
        try
        {
            ViewModel.AdoptProject(session);
            // Adopt the scene before asset I/O so constructor failure cleanup owns its components.
            SetCurrentScene(session.Scene, session.Project.StartupScenePath);
            RefreshProjectAssets();
            // Rebuild before showing the window; the initial Inspector used the placeholder asset index.
            RefreshComponents();
            RefreshDataAssetTableTypes();
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
        DataContext = ViewModel;
        // Context menus need their model before the popup is first opened.
        SceneSurface.ContextMenu!.DataContext = ViewModel;
        // Only once at startup, adjusts the bottom pane height from the live Scene View/Game width so it is 16:9.
        CenterGrid.LayoutUpdated += OnCenterLayoutUpdated;
        // Per-project type owner. Held independently even for empty projects (tests, unopened).
        // Dedicated service set for the editing lifetime. Built from the same registrations, kept independent from Play.
        Closed += (_, _) => CloseEditSession();
        Closing += OnEditorClosing;
        AddHandler(KeyDownEvent, OnFileShortcut, RoutingStrategies.Tunnel);
        UpdateSceneTitle();
        InitHierarchy();
        InitInspectorModel();
        RefreshProjectExplorer();
        SceneSurface.AddHandler(PointerPressedEvent, OnScenePointerPressed, RoutingStrategies.Tunnel);
        ProjectFiles.AddHandler(PointerPressedEvent, OnAssetPressed, RoutingStrategies.Tunnel);
        ProjectFiles.AddHandler(PointerMovedEvent, OnAssetMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        ProjectFiles.AddHandler(PointerReleasedEvent, OnAssetReleased,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        foreach (var surface in new Control[] { SceneSurface, InspectorPane })
        {
            surface.AddHandler(DragDrop.DragOverEvent, OnComponentDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
            surface.AddHandler(DragDrop.DropEvent, OnComponentDrop, RoutingStrategies.Bubble, handledEventsToo: true);
        }
        InitProjectDrop();
        ViewModel.BeforeSceneSave += CancelSceneViewDrag;
        ViewModel.DocumentSaved += OnDocumentSaved;
        InitCompilationModel();
        InitPlayControls();
        InitConsole();
        RefreshDataAssetTableTypes();
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
        ViewportTabs.SelectionChanged += OnEditorViewportChanged;
    }

    private void OnAssetPressed(object? sender, PointerPressedEventArgs e)
    {
        _assetPress = null;
        _dragTypes = null;
        _pressedDataAsset = null;
        _pressedPrefab = null;
        _pressedImage = null;
        _pressedMoveEntry = null;
        if (!e.GetCurrentPoint(ProjectFiles).Properties.IsLeftButtonPressed) return;
        var entry = ((e.Source as Visual)?.GetSelfAndVisualAncestors()
            .OfType<ListBoxItem>().FirstOrDefault()?.DataContext as ProjectExplorerEntry);
        if (entry is null) return;
        if (IsMovableExplorerEntry(entry) && !IsPlaying)
            _pressedMoveEntry = entry;
        if (entry.Kind == ProjectExplorerKind.DataAsset)
        {
            if (IsPlaying) return;
            _pressedDataAsset = entry;
            e.Handled = true; // Select only on release, preserving the Inspector during a drag.
        }
        else if (IsProjectImage(entry))
        {
            if (IsPlaying) return;
            _pressedImage = entry;
            e.Handled = true;
        }
        else if (entry.Kind == ProjectExplorerKind.Prefab && entry.FullPath is not null)
        {
            if (IsPlaying) return;
            _pressedPrefab = entry;
            e.Handled = true; // Select only on release, preserving the Inspector during a drag.
        }
        else if (entry.ComponentType is not null)
        {
            _dragTypes = [entry.ComponentType];
        }
        else if (entry.Kind == ProjectExplorerKind.File && entry.FullPath is not null
            && entry.FullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            // C# files in the Project column can drag in attachable classes via D&D.
            // When one file holds multiple classes, attaches all unattached classes from that file.
            // Files without attachable types remain movable within the Project pane.
            var types = Components.GetTypesForFile(entry.FullPath);
            if (types.Count > 0)
                _dragTypes = types;
        }
        if (_pressedDataAsset is null && _pressedImage is null && _pressedPrefab is null && _dragTypes is null && _pressedMoveEntry is null) return;
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
            _pressedDataAsset = null;
            _pressedPrefab = null;
            _pressedImage = null;
            _pressedMoveEntry = null;
            return;
        }
        var delta = e.GetPosition(ProjectFiles) - _assetPressPosition;
        if (Math.Abs(delta.X) < 4 && Math.Abs(delta.Y) < 4) return;
        var press = _assetPress;
        var moveEntry = _pressedMoveEntry;
        var movePath = moveEntry?.FullPath;
        // Project pane moves share the drag with scene attaches: drop targets pick the payload they understand.
        using var transfer = new DataTransfer();
        var hasPayload = false;
        var hasMove = false;
        if (!string.IsNullOrEmpty(movePath) && (File.Exists(movePath) || Directory.Exists(movePath)))
        {
            transfer.Add(DataTransferItem.Create(ProjectPathFormat, movePath));
            hasPayload = true;
            hasMove = true;
        }
        if (_pressedDataAsset is { FullPath: not null } dataEntry && Project is not null)
        {
            RefreshReferenceAssets();
            var assets = Documents.Current.Current.DataAssets;
            var relative = Path.GetRelativePath(Project.RootDirectory, dataEntry.FullPath).Replace('\\', '/');
            var id = assets.Ids.FirstOrDefault(id => assets.DisplayName(id) == relative);
            if (id != Guid.Empty)
            {
                transfer.Add(DataTransferItem.Create(DataAssetIdFormat, id.ToString("D")));
                hasPayload = true;
            }
        }
        if (_pressedImage is { FullPath: not null } imageEntry)
        {
            RefreshProjectAssets();
            var image = _projectAssets.Images.Values.FirstOrDefault(candidate => SamePath(candidate.FullPath, imageEntry.FullPath));
            if (image is not null)
            {
                transfer.Add(DataTransferItem.Create(ImageIdFormat, image.Id.ToString("D")));
                hasPayload = true;
            }
        }
        if (_pressedPrefab is { FullPath: not null } prefabEntry)
        {
            transfer.Add(DataTransferItem.Create(PrefabPathFormat, Path.GetFullPath(prefabEntry.FullPath)));
            hasPayload = true;
        }
        if (_dragTypes is { Count: > 0 } types)
        {
            transfer.Add(DataTransferItem.Create(ComponentTypesFormat, types));
            if (types.Count == 1)
                transfer.Add(DataTransferItem.Create(ComponentFormat, types[0]));
            hasPayload = true;
        }
        _assetPress = null;
        _dragTypes = null;
        _pressedDataAsset = null;
        _pressedPrefab = null;
        _pressedImage = null;
        _pressedMoveEntry = null;
        if (!hasPayload) return;
        var effects = hasMove
            ? DragDropEffects.Move | DragDropEffects.Copy
            : DragDropEffects.Copy;
        await DragDrop.DoDragDropAsync(press, transfer, effects);
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
            if (Components.CanAttach(target, type)) { canAny = true; break; }
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
        var types = GetDragTypes(e).Where(t => Components.CanAttach(target, t)).ToArray();
        if (types.Length == 0) return;
        SelectSceneObject(target, focus: false);
        var attached = 0;
        string? firstError = null;
        Type? errorType = null;
        foreach (var type in types)
        {
            try
            {
                if (!Components.TryAttach(target, type, EditSession.Factory)) continue;
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
        DetachInvalidFields(ComponentEditors);
        ComponentEditors.Children.Clear();
        if (Documents.Asset is not null && GetSelectedSceneObject() is null)
        {
            AttachError.IsVisible = false;
            RefreshDataAssetInspector();
            return;
        }
        ViewModel.DataAsset.Refresh();
        var item = GetSelectedSceneObject();
        ViewModel.Inspector.RefreshComponents();
        var components = ViewModel.Inspector.Components;
        AttachError.IsVisible = false;
        UpdateErrorBadge();
        if (item is null) return;
        foreach (var component in components)
            ComponentEditors.Children.Add(BuildComponentCard(item, component));
    }

    /// <summary>Detaches input errors owned by a rebuilt container. Other containers keep their errors and dirty state.</summary>
    private void DetachInvalidFields(Control root)
    {
        foreach (var box in root.GetVisualDescendants().OfType<TextBox>().ToArray())
            ClearInputError(box);
    }

    private void UpdateErrorBadge() => UpdateDataAssetTableChrome();

    /// <summary>Collapse toggle colors. The glyph always carries the structural accent; collapsed state adds a wash fill.</summary>
    private static readonly SolidColorBrush CollapseToggleBrush = new(Color.Parse("#8B7CF6"));
    private static readonly SolidColorBrush CollapseToggleWashBrush = new(Color.Parse("#2E2A4A"));

    /// <summary>Collapse toggle shared by Component cards and collection editors. Keeps state in <paramref name="store"/>.</summary>
    /// <remarks>Uses a Button base. ToggleButton would paint accent directly on template parts in its Fluent theme checked state,
    /// which the transparent style cannot fully remove.</remarks>
    private static Avalonia.Controls.Button BuildCollapseToggle(string automationName, string collapseKey, Dictionary<string, bool> store, Action<bool> apply)
    {
        var expandedState = !store.TryGetValue(collapseKey, out var collapsed) || !collapsed;
        var glyph = new PathIcon
        {
            Data = (StreamGeometry?)Application.Current?.FindResource(expandedState ? "Icon.TriangleDown" : "Icon.TriangleRight"),
            Width = 10,
            Height = 10,
        };
        var toggle = new Avalonia.Controls.Button
        {
            Content = glyph,
            // Fixed square so the icon swap never shifts the button, title, or header buttons.
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
            glyph.Data = (StreamGeometry?)Application.Current?.FindResource(expandedState ? "Icon.TriangleDown" : "Icon.TriangleRight");
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
        var toggle = BuildCollapseToggle($"{type.Name}.Collapse", collapseKey, ViewModel.Inspector.CollapsedCards,
            nowExpanded => content.IsVisible = nowExpanded);
        var body = new StackPanel { Spacing = 8 };
        var header = new Grid { ColumnDefinitions = [with("*,Auto")], ColumnSpacing = 8 };
        // Collapse toggle shares the title cell so the header Grid keeps a single
        // title TextBlock and the right-docked priority panel (see PriorityInspectorChecks).
        header.Children.Add(toggle);
        // Box marks a component card, matching the Components section header.
        var icon = new PathIcon
        {
            Data = (StreamGeometry?)Application.Current?.FindResource("Icon.Component"),
            Width = 14,
            Height = 14,
            Margin = new Thickness(22, 0, 0, 0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        icon.SetValue(AutomationProperties.NameProperty, "Component");
        ToolTip.SetTip(icon, type.FullName);
        header.Children.Add(icon);
        var title = new TextBlock { Text = type.Name, FontSize = 13, FontWeight = FontWeight.SemiBold,
            Foreground = CardTitleBrush,
            MaxWidth = 140, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(42, 0, 0, 0),
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
                ClearInputError(box);
            ComponentEditors.Children.Remove(card);
            ViewModel.Inspector.RefreshComponents();
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
        box.Classes.Add("numericField");
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
            if (SceneReferenceTypes.IsSingleReference(memberType, Components.Registry))
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
            if (SceneReferenceTypes.ContainsReference(memberType, Components.Registry)
                && (memberType.IsArray || (memberType.IsGenericType && memberType.GetGenericTypeDefinition() == typeof(List<>))))
            {
                editor = BuildNestedCollectionEditor(owner, member, effectiveOwnerId.Value, effectiveStore, automationName);
                return BuildLabeledEditorRow(member.Name, $"{member.Name} : {friendlyType}", friendlyType, editor);
            }
            if (SceneReferenceTypes.ContainsReference(memberType, Components.Registry)
                && memberType.IsGenericType && memberType.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                editor = BuildNestedCollectionEditor(owner, member, effectiveOwnerId.Value, effectiveStore, automationName);
                return BuildLabeledEditorRow(member.Name, $"{member.Name} : {friendlyType}", friendlyType, editor);
            }
            if (InspectorValueTypes.IsCustomInspectorObject(memberType) || SceneReferenceTypes.ContainsReference(memberType, Components.Registry))
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

    private Grid BuildLabeledEditorRow(string label, string tooltip, string typeText, Control editor)
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
        AttachEditorDropHandlers(row, editor);
        return row;
    }

    private Control BuildMemberEditor(object component, MemberInfo member, string? automationName = null)
    {
        var memberType = GetMemberType(member);
        automationName ??= $"{component.GetType().Name}.{member.Name}";

        if (component is not InspectorValueBinding
            && !ShouldShowReferenceEditorFor(memberType, component)
            && (IsCompositeValue(memberType) || SceneReferenceTypes.ContainsReference(memberType, Components.Registry))
            && (InspectorValueTypes.IsSupportedType(memberType)
                || SceneReferenceTypes.IsSupportedInspectorType(memberType, Components.Registry)))
            return BuildBoundValueEditor(InspectorValueBinding.ForMember(component, member), automationName,
                GetOwnerComponentId(component), member.Name);

        if (ShouldShowReferenceEditorFor(memberType, component))
            return BuildMemberReferenceEditor(component, member, automationName);
        if ((memberType.IsArray || memberType.IsGenericType)
            && SceneReferenceTypes.ContainsReference(memberType, Components.Registry)
            && GetOwnerComponentId(component) is { } ownerId)
            return SceneReferenceTypes.IsSupportedInspectorType(memberType, Components.Registry)
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
            box.Classes.Add("numericField");
            box.SetValue(AutomationProperties.NameProperty, automationName);
            ToolTip.SetTip(box, hint);
            box.TextChanged += (_, _) =>
                MarkInvalid(box, ViewModel.Inspector.SetNumericText(component, member, box.Text), hint);
            AttachEscapeRevert(box, component, member);
            return box;
        }

        if (memberType == typeof(float))
        {
            const string hint = "Enter a number — Press Esc to revert";
            var box = new TextBox { Text = FormatMemberValue(component, member), PlaceholderText = "0.0" };
            box.Classes.Add("inspectorField");
            box.Classes.Add("numericField");
            box.SetValue(AutomationProperties.NameProperty, automationName);
            ToolTip.SetTip(box, hint);
            box.TextChanged += (_, _) =>
                MarkInvalid(box, ViewModel.Inspector.SetNumericText(component, member, box.Text), hint);
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
            if (SceneReferenceTypes.IsSupportedInspectorType(memberType, Components.Registry))
                return BuildSequenceEditor(component, member, automationName);
        }
        if (memberType.IsGenericType && memberType.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            if (SceneReferenceTypes.IsSupportedInspectorType(memberType, Components.Registry))
                return BuildDictionaryEditor(component, member, automationName);
        }
        if (InspectorValueTypes.IsCustomInspectorObject(memberType) || SceneReferenceTypes.ContainsReference(memberType, Components.Registry))
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
            && !box.GetVisualAncestors().Contains(DataAssetEditors)
            && !box.GetVisualAncestors().Contains(DataAssetTableRows)) return;
        if (message is null)
        {
            box.ClearValue(TextBox.BorderBrushProperty);
            box.ClearValue(TextBox.BackgroundProperty);
            ToolTip.SetTip(box, validTip);
            ClearInputError(box);
        }
        else
        {
            box.BorderBrush = InvalidBrush;
            box.Background = InvalidFieldBackground;
            ToolTip.SetTip(box, message);
            SetInputError(box, message);
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
        type.IsArray ? $"{FriendlyTypeName(type.GetElementType()!)}[{new string(',', type.GetArrayRank() - 1)}]" :
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>) ? $"List<{FriendlyTypeName(type.GetGenericArguments()[0])}>" :
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>) ? $"Dictionary<{FriendlyTypeName(type.GetGenericArguments()[0])}, {FriendlyTypeName(type.GetGenericArguments()[1])}>" :
        type.IsEnum ? type.Name :
        type.IsGenericType ? $"{type.Name.Split('`')[0]}<{string.Join(", ", type.GetGenericArguments().Select(FriendlyTypeName))}>" :
        type.Name;

    private static object? GetMemberValue(object component, MemberInfo member) => InspectorViewModel.ReadMember(component, member);

    private void SetMemberValue(object component, MemberInfo member, object? value) => ViewModel.Inspector.SetMember(component, member, value);

    private void OnScenePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(SceneSurface).Properties.IsRightButtonPressed) return;
        var node = FindHierarchyNode(e.Source as Avalonia.Visual)?.Ref;
        if (node is not null && GetSelectedSceneObjects().Contains(node)) { SceneObjects.Focus(); return; }
        SelectSceneObject(node, focus: true);
    }

    private void OnAddUiImage(object? sender, RoutedEventArgs e) =>
        AddUiObject("Image", [typeof(Core.Transform), typeof(UiElement), typeof(Core.Image)]);

    private void OnAddUiButton(object? sender, RoutedEventArgs e) =>
        AddUiObject("Button", [typeof(Core.Transform), typeof(UiElement), typeof(Core.Image), typeof(Core.Button)]);

    private void OnAddUiText(object? sender, RoutedEventArgs e) =>
        AddUiObject("Text", [typeof(Core.Transform), typeof(UiElement), typeof(Core.Text)]);

    private void AddUiObject(string baseName, Type[] componentTypes) =>
        ViewModel.Hierarchy.AddUiObject(baseName, componentTypes, Components);

    private void OnAddObject(object? sender, RoutedEventArgs e) => ViewModel.Hierarchy.AddEmpty();

    private async void OnObjectSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_hierarchyRefreshing || ViewModel.Compilation.IsApplying || ViewModel.Hierarchy.IsMutating) return;
        CaptureHierarchySelection();
        if (_assetSelectionChanging) return;
        if (GetSelectedSceneObject() is not null && Documents.Asset is not null)
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
        ViewModel.Inspector.Select(item);
        if (Documents.Asset is not null && item is null)
        {
            RefreshDataAssetInspector();
            return;
        }
        ViewModel.DataAsset.Refresh();
        RefreshComponents();
    }

    private void OnDeleteObject(object? sender, RoutedEventArgs e) => DeleteSelectedObject();

    private void OnDuplicateObject(object? sender, RoutedEventArgs e) => DuplicateSelectedObjects();

    private void OnSceneKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            DeleteSelectedObject();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.D && e.KeyModifiers == KeyModifiers.Control)
        {
            DuplicateSelectedObjects();
            e.Handled = true;
        }
    }

    private void DeleteSelectedObject() => ViewModel.Hierarchy.DeleteSelected();

    internal List<SceneObject> DuplicateSelectedForTest() => DuplicateSelectedObjects();

    private List<SceneObject> DuplicateSelectedObjects() => ViewModel.Hierarchy.DuplicateSelected(Components.Registry);

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
