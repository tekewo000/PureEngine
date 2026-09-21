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

namespace PureEngine.Editor;

public partial class MainWindow : Window
{
    private Scene _scene = new();
    private GameSession _editSession;
    private static readonly DataFormat<Type> ComponentFormat =
        DataFormat.CreateInProcessFormat<Type>("PureEngine.ComponentType");
    private PointerPressedEventArgs? _assetPress;
    private Point _assetPressPosition;
    private Type? _dragType;
    private readonly HashSet<TextBox> _invalidFields = [];

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
    }

    public MainWindow()
    {
        InitializeComponent();
        // 編集期間の専用サービス群。同じ登録から作り、Play 用とは独立させる。
        _editSession = GameSession.Create();
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
        InitPlayControls();
    }

    private void OnAssetPressed(object? sender, PointerPressedEventArgs e)
    {
        _assetPress = null;
        if (!e.GetCurrentPoint(ProjectFiles).Properties.IsLeftButtonPressed) return;
        _dragType = ((e.Source as Visual)?.GetSelfAndVisualAncestors()
            .OfType<ListBoxItem>().FirstOrDefault()?.DataContext as ProjectExplorerEntry)?.ComponentType;
        if (_dragType is null) return;
        _assetPressPosition = e.GetPosition(ProjectFiles);
        _assetPress = e;
    }

    private async void OnAssetMoved(object? sender, PointerEventArgs e)
    {
        if (_assetPress is null) return;
        if (!e.GetCurrentPoint(ProjectFiles).Properties.IsLeftButtonPressed)
        {
            _assetPress = null;
            return;
        }
        var delta = e.GetPosition(ProjectFiles) - _assetPressPosition;
        if (Math.Abs(delta.X) < 4 && Math.Abs(delta.Y) < 4) return;
        var press = _assetPress;
        var type = _dragType!;
        _assetPress = null;
        using var data = new DataTransfer();
        data.Add(DataTransferItem.Create(ComponentFormat, type));
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
        e.DragEffects = ComponentAssets.CanAttach(DropTarget(sender, e), e.DataTransfer.TryGetValue(ComponentFormat))
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnComponentDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        e.DragEffects = DragDropEffects.None;
        if (RejectWhenPlaying("アタッチ")) return;
        var target = DropTarget(sender, e);
        var type = e.DataTransfer.TryGetValue(ComponentFormat);
        if (!ComponentAssets.CanAttach(target, type)) return;
        SceneObjects.SelectedItem = target;
        try
        {
            if (!ComponentAssets.TryAttach(target, type, _editSession.Factory)) return;
            MarkSceneChanged();
            RefreshComponents();
            e.DragEffects = DragDropEffects.Copy;
        }
        catch (Exception error)
        {
            AttachError.Text = $"{type!.Name} を追加できませんでした: {error.GetBaseException().Message}";
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
        var body = new StackPanel { Spacing = 4 };
        var title = new TextBlock { Text = type.Name, FontWeight = FontWeight.SemiBold };
        ToolTip.SetTip(title, type.FullName);
        body.Children.Add(title);
        body.Children.Add(new Separator { Classes = { "divider" } });
        foreach (var member in ComponentSchema.GetInspectorMembers(type))
            body.Children.Add(BuildMemberRow(component, member));
        foreach (var row in BuildPriorityRows(item, component))
            body.Children.Add(row);
        return new Border { Classes = { "componentCard" }, Child = body };
    }

    /// <summary>Attach settings, separate from Inspector members. Only lifecycles present on the class are shown.</summary>
    private List<Control> BuildPriorityRows(SceneObject item, object component)
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
        rows.Add(new Separator { Classes = { "divider" } });
        if (hasStart) rows.Add(BuildPriorityRow(item, component, ComponentLifecycle.Start, "Start Priority"));
        if (hasUpdate) rows.Add(BuildPriorityRow(item, component, ComponentLifecycle.Update, "Update Priority"));
        if (hasDestroy) rows.Add(BuildPriorityRow(item, component, ComponentLifecycle.Destroy, "Destroy Priority"));
        return rows;
    }

    private Control BuildPriorityRow(SceneObject item, object component, ComponentLifecycle kind, string displayName)
    {
        var type = component.GetType();
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
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("96,*"), ColumnSpacing = 8 };
        var label = new TextBlock
        {
            Text = displayName,
            Classes = { "muted" },
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        label.SetValue(ToolTip.TipProperty, $"{displayName} (attach setting)");
        Grid.SetColumn(label, 0);
        var box = new TextBox { Text = getter().ToString(CultureInfo.InvariantCulture) };
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
        Grid.SetColumn(box, 1);
        row.Children.Add(label);
        row.Children.Add(box);
        return row;
    }

    private Control BuildMemberRow(object component, MemberInfo member)
    {
        if (GetMemberType(member) == typeof(bool))
        {
            var check = (CheckBox)BuildMemberEditor(component, member);
            check.Content = member.Name;
            return check;
        }
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("96,*"), ColumnSpacing = 8 };
        var label = new TextBlock
        {
            Text = member.Name,
            Classes = { "muted" },
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        label.SetValue(ToolTip.TipProperty, member.Name);
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

        return new TextBlock { Text = "対応外", Classes = { "muted" } };
    }

    private static readonly SolidColorBrush InvalidBrush = new(Color.Parse("#FF5252"));

    private void MarkInvalid(TextBox box, string? message)
    {
        if (message is null)
        {
            box.ClearValue(TextBox.BorderBrushProperty);
            ToolTip.SetTip(box, null);
            _invalidFields.Remove(box);
        }
        else
        {
            box.BorderBrush = InvalidBrush;
            ToolTip.SetTip(box, message);
            _invalidFields.Add(box);
        }
        UpdateErrorBadge();
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
}
