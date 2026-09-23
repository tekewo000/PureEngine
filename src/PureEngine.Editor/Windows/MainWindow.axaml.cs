using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PureEngine.Core;
using PureEngine.Runtime;

namespace PureEngine.Editor;

public partial class MainWindow : Window
{
    private Scene _scene = new();
    private GameSession _editSession;
    private static readonly DataFormat<Type> ComponentFormat =
        DataFormat.CreateInProcessFormat<Type>("PureEngine.ComponentType");
    private static readonly DataFormat<IReadOnlyList<Type>> ComponentTypesFormat =
        DataFormat.CreateInProcessFormat<IReadOnlyList<Type>>("PureEngine.ComponentTypes");
    private PointerPressedEventArgs? _assetPress;
    private Point _assetPressPosition;
    private IReadOnlyList<Type>? _dragTypes;
    private readonly HashSet<TextBox> _invalidFields = [];
    private bool _viewportFitted;

    public MainWindow(ProjectSession session, GameSession? editServices = null) : this()
    {
        if (editServices is not null)
        {
            var created = _editSession;
            _editSession = editServices;
            created.Dispose();
        }
        _project = session.Project;
        SetCurrentScene(session.Scene, session.Project.StartupScenePath);
        ProjectTab.IsSelected = true;
        StartUserCodeWatching();
    }

    public MainWindow()
    {
        InitializeComponent();
        // 起動時1回だけ、Scene View/Gameの実幅から16:9になるよう下ペイン高さを初期調整する。
        CenterGrid.LayoutUpdated += OnCenterLayoutUpdated;
        // 編集期間の専用サービス群。同じ登録から作り、Play 用とは独立させる。
        _editSession = GameSession.Create(GameServices.Configure);
        Closed += (_, _) => CloseEditSession();
        Closing += OnEditorClosing;
        AddHandler(KeyDownEvent, OnFileShortcut, RoutingStrategies.Tunnel);
        UpdateSceneTitle();
        SceneObjects.ItemsSource = _scene.Objects;
        SceneObjects.SelectionChanged += OnObjectSelected;
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
            // Project欄のC#ファイルからアタッチ対象のクラスをD&Dできる。
            // 1ファイルに複数クラスがある場合はそのファイルの未アタッチ分をすべて付ける。
            var types = ComponentAssets.GetTypesForFile(entry.FullPath);
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
            ? SceneObjects.SelectedItem as SceneObject
            : (e.Source as Visual)?.GetSelfAndVisualAncestors()
                .OfType<ListBoxItem>().FirstOrDefault()?.DataContext as SceneObject;

    private void OnComponentDragOver(object? sender, DragEventArgs e)
    {
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
            if (ComponentAssets.CanAttach(target, type)) { canAny = true; break; }
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
        e.Handled = true;
        e.DragEffects = DragDropEffects.None;
        if (RejectWhenPlaying("アタッチ")) return;
        var target = DropTarget(sender, e);
        var types = GetDragTypes(e).Where(t => ComponentAssets.CanAttach(target, t)).ToArray();
        if (types.Length == 0) return;
        SceneObjects.SelectedItem = target;
        var attached = 0;
        string? firstError = null;
        Type? errorType = null;
        foreach (var type in types)
        {
            try
            {
                if (!ComponentAssets.TryAttach(target, type, _editSession.Factory)) continue;
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
            AttachError.Text = $"{errorType!.Name} を追加できませんでした: {firstError}";
            AttachError.IsVisible = true;
        }
    }

    private void RefreshComponents()
    {
        ComponentEditors.Children.Clear();
        _invalidFields.Clear();
        var item = SceneObjects.SelectedItem as SceneObject;
        var components = item?.Components.ToArray() ?? [];
        AttachedClasses.IsVisible = components.Length > 0;
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
    }

    private Border BuildComponentCard(SceneObject item, object component)
    {
        var type = component.GetType();
        var body = new StackPanel { Spacing = 6 };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        var title = new TextBlock { Text = type.Name, FontSize = 13, FontWeight = FontWeight.SemiBold,
            MaxWidth = 140, TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        ToolTip.SetTip(title, type.FullName);
        header.Children.Add(title);
        var priorities = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        foreach (var field in BuildPriorityFields(item, component))
            priorities.Children.Add(field);
        Grid.SetColumn(priorities, 1);
        header.Children.Add(priorities);
        body.Children.Add(header);
        body.Children.Add(new Separator { Classes = { "divider" } });
        foreach (var member in ComponentSchema.GetInspectorMembers(type))
            body.Children.Add(BuildMemberRow(component, member));
        return new Border { Classes = { "componentCard" }, Child = body };
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

    private Control BuildPriorityField(SceneObject item, object component, ComponentLifecycle kind, string displayName)
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
        var label = new TextBlock
        {
            Text = shortLabel,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = accent,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        ToolTip.SetTip(label, displayName);
        var box = new TextBox
        {
            Text = getter().ToString(CultureInfo.InvariantCulture),
            Width = 24, MinHeight = 22, Height = 22,
            FontSize = 10, Padding = new Thickness(2, 1),
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            BorderBrush = accent,
        };
        ToolTip.SetTip(box, displayName);
        box.Classes.Add("inspectorField");
        box.SetValue(AutomationProperties.NameProperty, $"{type.Name}.{kind}Priority");
        box.TextChanged += (_, _) =>
        {
            if (IsPlaying) return;
            if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                if (getter() != value)
                {
                    setter(value);
                    MarkSceneChanged();
                }
                MarkInvalid(box, null);
                ToolTip.SetTip(box, displayName);
            }
            else
            {
                MarkInvalid(box, "整数を入力してください");
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
            Spacing = 2,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        field.Children.Add(label);
        field.Children.Add(box);
        ToolTip.SetTip(field, displayName);
        return field;
    }

    private Control BuildMemberRow(object component, MemberInfo member)
    {
        var memberType = GetMemberType(member);
        if (memberType == typeof(bool))
        {
            // bool も他型と同じ96pxラベル列に揃え、ボックスと文字の間隔をグリッドで保証する。
            var boolRow = new Grid { ColumnDefinitions = new ColumnDefinitions("96,*"), ColumnSpacing = 8 };
            boolRow.Classes.Add("inspectorRow");
            var boolLabel = new TextBlock
            {
                Text = member.Name,
                Foreground = MemberLabelBrush,
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            boolLabel.SetValue(ToolTip.TipProperty, $"{member.Name} : bool");
            Grid.SetColumn(boolLabel, 0);
            var check = (CheckBox)BuildMemberEditor(component, member);
            check.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
            Grid.SetColumn(check, 1);
            boolRow.Children.Add(boolLabel);
            boolRow.Children.Add(check);
            return boolRow;
        }
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("96,*"), ColumnSpacing = 8 };
        row.Classes.Add("inspectorRow");
        var label = new TextBlock
        {
            Text = member.Name,
            Foreground = MemberLabelBrush,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        label.SetValue(ToolTip.TipProperty, $"{member.Name} : {memberType.Name}");
        Grid.SetColumn(label, 0);
        var editor = BuildMemberEditor(component, member);
        Grid.SetColumn(editor, 1);
        row.Children.Add(label);
        row.Children.Add(editor);
        return row;
    }

    private Control BuildMemberEditor(object component, MemberInfo member)
    {
        var memberType = GetMemberType(member);
        var automationName = $"{component.GetType().Name}.{member.Name}";

        if (memberType == typeof(string))
        {
            var box = new TextBox { Text = (string?)GetMemberValue(component, member) ?? "" };
            box.Classes.Add("inspectorField");
            box.SetValue(AutomationProperties.NameProperty, automationName);
            box.TextChanged += (_, _) =>
            {
                // Displaying a loaded null as an empty field must not change the scene.
                if ((box.Text ?? "") != ((string?)GetMemberValue(component, member) ?? ""))
                    SetMemberValue(component, member, box.Text ?? "");
            };
            return box;
        }

        if (memberType == typeof(int))
        {
            var box = new TextBox { Text = FormatMemberValue(component, member) };
            box.Classes.Add("inspectorField");
            box.SetValue(AutomationProperties.NameProperty, automationName);
            box.TextChanged += (_, _) =>
            {
                if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                {
                    SetMemberValue(component, member, value);
                    MarkInvalid(box, null);
                }
                else
                {
                    MarkInvalid(box, "整数を入力してください");
                }
            };
            AttachEscapeRevert(box, component, member);
            return box;
        }

        if (memberType == typeof(float))
        {
            var box = new TextBox { Text = FormatMemberValue(component, member) };
            box.Classes.Add("inspectorField");
            box.SetValue(AutomationProperties.NameProperty, automationName);
            box.TextChanged += (_, _) =>
            {
                if (float.TryParse(box.Text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value)
                    && float.IsFinite(value))
                {
                    SetMemberValue(component, member, value);
                    MarkInvalid(box, null);
                }
                else
                {
                    MarkInvalid(box, "数値を入力してください");
                }
            };
            AttachEscapeRevert(box, component, member);
            return box;
        }

        if (memberType == typeof(bool))
        {
            var check = new CheckBox { IsChecked = GetMemberValue(component, member) is true };
            check.SetValue(AutomationProperties.NameProperty, automationName);
            check.IsCheckedChanged += (_, _) => SetMemberValue(component, member, check.IsChecked == true);
            return check;
        }

        return new Border
        {
            Classes = { "kindBadge" },
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Child = new TextBlock { Text = memberType.Name, FontSize = 10, Foreground = UnsupportedBadgeBrush },
        };
    }

    private static readonly SolidColorBrush InvalidBrush = new(Color.Parse("#FF5252"));
    private static readonly SolidColorBrush InvalidFieldBackground = new(Color.Parse("#4A1A1A"));

    /// <summary>Inspector member labels. Brighter than muted so label/value pairs scan as units.</summary>
    private static readonly SolidColorBrush MemberLabelBrush = new(Color.Parse("#D5D5D5"));
    private static readonly SolidColorBrush UnsupportedBadgeBrush = new(Color.Parse("#AAAAAA"));

    /// <summary>S/U/D priority label accents. The letter stays the primary cue; color is redundant.</summary>
    private static readonly SolidColorBrush StartAccent = new(Color.Parse("#8AB4F8"));
    private static readonly SolidColorBrush UpdateAccent = new(Color.Parse("#81C995"));
    private static readonly SolidColorBrush DestroyAccent = new(Color.Parse("#F28B82"));

    private void MarkInvalid(TextBox box, string? message)
    {
        if (message is null)
        {
            box.ClearValue(TextBox.BorderBrushProperty);
            box.ClearValue(TextBox.BackgroundProperty);
            ToolTip.SetTip(box, null);
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

    /// <summary>Escで編集中の数値欄を最後の正常値へ戻す。TextChanged経由で無効表示も解除される。</summary>
    private void AttachEscapeRevert(TextBox box, object component, MemberInfo member)
    {
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            box.Text = FormatMemberValue(component, member);
            e.Handled = true;
        };
    }

    private string FormatMemberValue(object component, MemberInfo member) =>
        GetMemberType(member) == typeof(float)
            ? Convert.ToString(GetMemberValue(component, member), CultureInfo.InvariantCulture) ?? "0"
            : GetMemberValue(component, member)?.ToString() ?? "0";

    private Type GetMemberType(MemberInfo member) =>
        member switch
        {
            FieldInfo field => field.FieldType,
            PropertyInfo property => property.PropertyType,
            _ => throw new NotSupportedException($"Unsupported member: {member.Name}"),
        };

    private object? GetMemberValue(object component, MemberInfo member) =>
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
        MarkSceneChanged();
    }

    private void OnScenePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(SceneSurface).Properties.IsRightButtonPressed) return;
        var row = (e.Source as Avalonia.Visual)?.GetSelfAndVisualAncestors()
            .OfType<ListBoxItem>().FirstOrDefault();
        SceneObjects.SelectedItem = row?.DataContext as SceneObject;
        SceneObjects.Focus();
    }

    private void OnAddObject(object? sender, RoutedEventArgs e)
    {
        if (RejectWhenPlaying("追加")) return;
        var item = _scene.AddEmpty();
        MarkSceneChanged();
        SceneObjects.SelectedItem = item;
        SceneObjects.ScrollIntoView(item);
        SceneObjects.Focus();
    }

    private void OnObjectSelected(object? sender, SelectionChangedEventArgs e)
        => RefreshObjectInspector();

    private void RefreshObjectInspector()
    {
        var item = SceneObjects.SelectedItem as SceneObject;
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
        if (SceneObjects.SelectedItem is not SceneObject item) return;
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
        if (RejectWhenPlaying("削除")) return;
        if (SceneObjects.SelectedItem is not SceneObject item) return;
        var index = SceneObjects.SelectedIndex;
        _scene.Remove(item);
        MarkSceneChanged();
        SceneObjects.SelectedIndex = Math.Min(index, _scene.Objects.Count - 1);
        SceneObjects.Focus();
        try { ComponentAssets.DisposeComponents(item.Components); }
        catch (Exception error) { SetFileStatus(error.ToString(), true); }
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
    /// 起動時1回だけ、Scene View/Gameビューポートの横幅を変えずに映像エリアが16:9になるよう
    /// 下ペインの高さを調整する。タブヘッダー等のクローム分は実測から差し引く。以後はスプリッターで自由に変更できる。
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
