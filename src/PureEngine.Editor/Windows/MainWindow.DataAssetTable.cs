using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using PureEngine.Core;
using Button = Avalonia.Controls.Button;

namespace PureEngine.Editor;

public partial class MainWindow
{
    /// <summary>Default fixed row height for the data asset table. Users can drag a row taller; heights stay in memory only.</summary>
    private const double DataAssetTableDefaultRowHeight = 148;
    private const double DataAssetTableMinRowHeight = 80;
    private const double DataAssetTableMaxRowHeight = 640;

    private sealed class TableRowView
    {
        public double Height { get; set; } = DataAssetTableDefaultRowHeight;
        public Border? Card;
        public Grid? Cells;
        public readonly List<ScrollViewer> CellScrollers = [];
        public TextBlock? Title;
    }

    private readonly Dictionary<DataAssetEditState, TableRowView> _tableViews = [];

    private TableRowView TableView(DataAssetEditState row)
    {
        if (!_tableViews.TryGetValue(row, out var view))
            _tableViews.Add(row, view = new TableRowView());
        return view;
    }

    private bool _tableTypeChanging;

    /// <summary>Whether any table row has unsaved changes. For tests and close guards.</summary>
    internal bool IsDataAssetTableDirty => _documents.Table.IsDirty;

    /// <summary>Rebuilds the table type picker. Preserves the selection by type ID and rescans rows for it.</summary>
    private void RefreshDataAssetTableTypes(bool rescanRows = true)
    {
        _tableTypeChanging = true;
        try
        {
            var selectedId = _documents.Table.Type?.TypeId;
            DataAssetTableTypes.Items.Clear();
            if (_project is null)
            {
                _documents.Table.Type = null;
                ClearTableRowViews();
                UpdateDataAssetTableChrome();
                return;
            }
            var descriptors = DataAssetDescriptor.DescribeAll(_components.Registry, out _, _components.DataAssetTypes);
            ComboBoxItem? selected = null;
            foreach (var descriptor in descriptors.OrderBy(d => d.MenuPath, StringComparer.Ordinal))
            {
                var item = new ComboBoxItem { Content = descriptor.MenuPath, Tag = descriptor };
                ToolTip.SetTip(item, descriptor.TypeId);
                DataAssetTableTypes.Items.Add(item);
                if (descriptor.TypeId == selectedId) selected = item;
            }
            DataAssetTableTypes.SelectedItem = selected;
            if (selected is null)
            {
                _documents.Table.Type = null;
                ClearTableRowViews();
            }
            else
            {
                _documents.Table.Type = (DataAssetDescriptor)selected.Tag!;
                if (rescanRows) RescanTableRowsCore();
            }
        }
        finally { _tableTypeChanging = false; }
        UpdateDataAssetTableChrome();
    }

    private async void OnDataAssetTableTypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_tableTypeChanging) return;
        var selected = (DataAssetTableTypes.SelectedItem as ComboBoxItem)?.Tag as DataAssetDescriptor;
        if (selected is not null && _documents.Table.Type is not null && selected.TypeId == _documents.Table.Type.TypeId) return;
        await RunFileOperation(async () =>
        {
            if (!await ConfirmCloseTableRows())
            {
                _tableTypeChanging = true;
                try
                {
                    DataAssetTableTypes.SelectedItem = DataAssetTableTypes.Items
                        .OfType<ComboBoxItem>().FirstOrDefault(item => ReferenceEquals(item.Tag, _documents.Table.Type));
                }
                finally { _tableTypeChanging = false; }
                return;
            }
            _documents.Table.Type = selected;
            if (selected is null) ClearTableRowViews();
            else RescanTableRowsCore();
            UpdateDataAssetTableChrome();
            await Task.CompletedTask;
        });
    }

    /// <summary>Reloads rows for the selected type from disk, keeping per-row heights by path.</summary>
    private void RescanTableRows()
    {
        if (_documents.Table.Type is null) return;
        RescanTableRowsCore();
        UpdateDataAssetTableChrome();
    }

    private void RescanTableRowsCore()
    {
        var descriptor = _documents.Table.Type;
        if (descriptor is null || _project is null)
        {
            ClearTableRowViews();
            return;
        }
        var heights = _documents.Table.Rows.ToDictionary(row => row.Path, row => TableView(row).Height, StringComparer.FromComparison(PathComparison()));
        ClearTableRowViews();
        var serializer = new DataAssetSerializer(_components.Registry);
        var root = _project.RootDirectory;
        string[] files;
        try
        {
            files = [.. Directory.EnumerateFiles(root, "*" + DataAssetSerializer.FileExtension, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false,
            }).Order(StringComparer.Ordinal)];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Engine.Warning($"Data asset table: cannot scan {root} ({error.GetBaseException().Message}).");
            SetFileStatus($"Cannot scan data assets: {error.GetBaseException().Message}", true);
            return;
        }
        var byId = new Dictionary<Guid, DataAssetEditState>();
        var duplicated = new HashSet<Guid>();
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            string yaml;
            try { yaml = File.ReadAllText(file); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Log.Engine.Warning($"{relative}: cannot read ({error.GetBaseException().Message}).");
                continue;
            }
            object instance;
            Guid id;
            string typeId;
            bool membersChanged;
            try
            {
                (instance, id) = serializer.Deserialize(yaml, out typeId, out membersChanged);
            }
            catch (Exception error)
            {
                Log.Engine.Warning($"{relative}: invalid data asset ({error.GetBaseException().Message}).");
                continue;
            }
            if (typeId != descriptor.TypeId) continue;
            var full = Path.GetFullPath(file);
            if (duplicated.Contains(id) || !byId.TryAdd(id, new DataAssetEditState(full, instance, id, typeId)
                { Dirty = membersChanged }))
            {
                duplicated.Add(id);
                byId.Remove(id);
                Log.Engine.Warning($"{relative}: duplicate data asset ID {id:D}; excluded from the table.");
            }
        }
        _documents.Table.Rows.AddRange(byId.Values.OrderBy(row =>
            Path.GetRelativePath(root, row.Path).Replace('\\', '/'), StringComparer.Ordinal));
        foreach (var row in _documents.Table.Rows) TableView(row).Height = heights.GetValueOrDefault(row.Path, DataAssetTableDefaultRowHeight);
        RefreshTableOwned();
        RebuildTableRowViews();
    }

    /// <summary>Rebuilds the table-owned instance set from all rows. New nested instances join on the next routed edit.</summary>
    private void RefreshTableOwned() => _documents.Table.RefreshOwnership();

    /// <summary>Routes a table-owned edit to its row. Shared with the single-asset and scene routing in <see cref="MarkEdited"/>.</summary>
    private void MarkTableRowDirty(object owner)
    {
        if (IsPlaying) return;
        if (_documents.Table.FindOwner(owner) is { } row) row.Dirty = true;
        RefreshTableOwned();
        UpdateDataAssetTableChrome();
    }

    private void RebuildTableRowViews()
    {
        DetachInvalidFields(DataAssetTableRows);
        DataAssetTableRows.Children.Clear();
        var descriptor = _documents.Table.Type;
        if (descriptor is null)
        {
            DataAssetTableStatus.Text = _project is null ? "Open a project." : "Select a type.";
            return;
        }
        var members = ComponentSchema.GetInspectorMembers(descriptor.Type);
        if (_documents.Table.Rows.Count == 0)
        {
            DataAssetTableStatus.Text = $"No {descriptor.DisplayName} rows — Add Row.";
            return;
        }
        DataAssetTableStatus.Text = $"{_documents.Table.Rows.Count} row(s) of {descriptor.DisplayName}.";
        foreach (var row in _documents.Table.Rows) DataAssetTableRows.Children.Add(BuildTableRowCard(row, descriptor, members));
    }

    private Border BuildTableRowCard(DataAssetEditState row, DataAssetDescriptor descriptor, IReadOnlyList<MemberInfo> members)
    {
        var card = new Border
        {
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(6),
            Padding = new Avalonia.Thickness(8),
            Background = (Avalonia.Media.IBrush?)Application.Current!.FindResource("EditorFloatBrush"),
        };
        var body = new StackPanel { Spacing = 6 };
        var header = new Grid { ColumnDefinitions = [with("*,Auto,Auto,Auto")], ColumnSpacing = 8 };
        var title = new TextBlock
        {
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        title.SetValue(AutomationProperties.NameProperty, $"Table.{descriptor.Type.Name}.{TableFileBase(row.Path)}.Title");
        ToolTip.SetTip(title, $"{row.Id:D}\n{row.Path}");
        TableView(row).Title = title;
        title.Text = $"{(row.Dirty ? "* " : "")}{Path.GetFileName(row.Path)}";
        Grid.SetColumn(title, 0);
        header.Children.Add(title);
        var duplicate = new Button { Content = "Duplicate", FontSize = 11, Padding = new Avalonia.Thickness(8, 2) };
        duplicate.SetValue(AutomationProperties.NameProperty, $"Table.{descriptor.Type.Name}.{TableFileBase(row.Path)}.Duplicate");
        duplicate.Click += (_, _) => _ = RunFileOperation(() => DuplicateTableRowAsync(row));
        Grid.SetColumn(duplicate, 1);
        header.Children.Add(duplicate);
        var open = new Button { Content = "Open", FontSize = 11, Padding = new Avalonia.Thickness(8, 2) };
        open.SetValue(AutomationProperties.NameProperty, $"Table.{descriptor.Type.Name}.{TableFileBase(row.Path)}.Open");
        ToolTip.SetTip(open, "Open in the single-asset Inspector");
        open.Click += (_, _) => _ = OpenDataAssetForEdit(row.Path);
        Grid.SetColumn(open, 2);
        header.Children.Add(open);
        var delete = new Button { Content = "Delete", FontSize = 11, Padding = new Avalonia.Thickness(8, 2) };
        delete.SetValue(AutomationProperties.NameProperty, $"Table.{descriptor.Type.Name}.{TableFileBase(row.Path)}.Delete");
        delete.Click += (_, _) => _ = RunFileOperation(() => DeleteTableRowAsync(row));
        Grid.SetColumn(delete, 3);
        header.Children.Add(delete);
        body.Children.Add(header);
        var cells = new Grid { ColumnSpacing = 8, Height = TableView(row).Height };
        for (var i = 0; i < members.Count; i++)
            cells.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = GridLength.Auto,
                SharedSizeGroup = $"TableCol{i}",
                MinWidth = 220,
            });
        TableView(row).Cells = cells;
        TableView(row).CellScrollers.Clear();
        for (var i = 0; i < members.Count; i++)
        {
            var member = members[i];
            var cell = new StackPanel { Spacing = 2 };
            var caption = new TextBlock
            {
                Classes = { "memberType" },
                TextTrimming = TextTrimming.CharacterEllipsis,
                Text = $"{member.Name} : {FriendlyTypeName(GetMemberType(member))}"
            };
            ToolTip.SetTip(caption, caption.Text);
            cell.Children.Add(caption);
            var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = Math.Max(24, TableView(row).Height - 30) };
            var editor = BuildMemberEditor(row.Instance, member, $"Table.{descriptor.Type.Name}.{TableFileBase(row.Path)}.{member.Name}");
            scroller.Content = editor;
            cell.Children.Add(scroller);
            TableView(row).CellScrollers.Add(scroller);
            Grid.SetColumn(cell, i);
            cells.Children.Add(cell);
        }
        if (members.Count == 0)
            cells.Children.Add(new TextBlock { Classes = { "hint" }, Text = "No editable fields." });
        body.Children.Add(cells);
        var grip = new Thumb
        {
            Height = 12,
            Cursor = new Cursor(StandardCursorType.SizeNorthSouth),
        };
        ToolTip.SetTip(grip, "Drag to resize this row");
        grip.SetValue(AutomationProperties.NameProperty, $"Table.{descriptor.Type.Name}.{TableFileBase(row.Path)}.Resize");
        grip.DragDelta += (_, e) =>
        {
            if (IsPlaying) return;
            TableView(row).Height = Math.Clamp(TableView(row).Height + e.Vector.Y, DataAssetTableMinRowHeight, DataAssetTableMaxRowHeight);
            ApplyTableRowHeight(row);
        };
        body.Children.Add(grip);
        card.Child = body;
        TableView(row).Card = card;
        return card;
    }

    private void ApplyTableRowHeight(DataAssetEditState row)
    {
        TableView(row).Cells?.Height = TableView(row).Height;
        foreach (var scroller in TableView(row).CellScrollers) scroller.MaxHeight = Math.Max(24, TableView(row).Height - 30);
    }

    private static string TableFileBase(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.EndsWith(".pure.asset", StringComparison.Ordinal) ? name[..^".pure.asset".Length] : name;
    }

    private void UpdateDataAssetTableChrome()
    {
        var dirty = IsDataAssetTableDirty;
        DataAssetTableTab.Header = dirty ? "Data Assets *" : "Data Assets";
        SaveDataAssetTableButton.IsEnabled = dirty && !IsPlaying;
        AddDataAssetTableRowButton.IsEnabled = _documents.Table.Type is not null && !IsPlaying;
        DataAssetTableRows.IsEnabled = !IsPlaying;
        foreach (var row in _documents.Table.Rows)
            TableView(row).Title?.Text = $"{(row.Dirty ? "* " : "")}{Path.GetFileName(row.Path)}";
        var invalid = DataAssetTableRows.GetVisualDescendants().OfType<TextBox>().Count(_invalidFields.Contains);
        DataAssetTableError.IsVisible = invalid > 0;
        DataAssetTableError.Text = $"Error {invalid}";
        ToolTip.SetTip(DataAssetTableError, invalid > 0
            ? $"{invalid} field(s) have invalid input — fix the highlighted fields to save."
            : null);
    }

    private void ClearTableRowViews()
    {
        DetachInvalidFields(DataAssetTableRows);
        DataAssetTableRows.Children.Clear();
        _documents.Table.Clear();
        _tableViews.Clear();
    }

    private void OnSaveDataAssetTable(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _ = RunFileOperation(SaveDataAssetTableAsync);

    /// <summary>Saves every dirty table row. Serializes all rows before writing any file, keeping the dirty state on failure.</summary>
    internal async Task<bool> SaveDataAssetTableAsync()
    {
        if (IsPlaying) return false;
        if (_documents.Table.Type is null || !IsDataAssetTableDirty) return true;
        var saveBlock = EditorOperationGate.SaveBlockReason(_invalidFields.Count > 0);
        if (saveBlock is not null)
        {
            SetFileStatus($"Cannot save data asset table. {saveBlock}", true);
            return false;
        }
        List<(DataAssetEditState Row, string Yaml)> pending;
        try { pending = _documents.Table.PrepareSave(_components.Registry); }
        catch (Exception error)
        {
            DataAssetTableStatus.Text = error.GetBaseException().Message;
            SetFileStatus($"Cannot save data asset table: {error.GetBaseException().Message}", true);
            return false;
        }
        try { DataAssetTableDocument.Save(pending); }
        catch (Exception error)
        {
            SetFileStatus($"Cannot save data asset table: {error.GetBaseException().Message}", true);
            return false;
        }
        UpdateDataAssetTableChrome();
        RefreshProjectExplorer();
        SetFileStatus($"Saved {_documents.Table.Type.DisplayName} table: {pending.Count} row(s).");
        await Task.CompletedTask;
        return true;
    }

    private void OnAddDataAssetTableRow(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _ = RunFileOperation(AddDataAssetTableRowAsync);

    private async Task AddDataAssetTableRowAsync()
    {
        var descriptor = _documents.Table.Type;
        if (_project is null || descriptor is null || RejectWhenPlaying("Add Row")) return;
        var folder = _documents.Table.Rows.Count > 0
            ? Path.GetRelativePath(_project.RootDirectory, Path.GetDirectoryName(_documents.Table.Rows[0].Path)!).Replace('\\', '/')
            : "Assets";
        Directory.CreateDirectory(_project.ResolveDirectoryPath(folder));
        var name = _project.NextDataAssetName(folder, descriptor.DisplayName);
        var path = Path.Combine(_project.ResolveDirectoryPath(folder), name);
        _project.ValidateDataAssetPath(path);
        var id = DataAssetFile.Create(path, descriptor.Type, _components.Registry);
        var (instance, _, _) = DataAssetFile.Load(path, _components.Registry);
        _documents.Table.Rows.Add(new DataAssetEditState(path, instance, id, descriptor.TypeId));
        RefreshTableOwned();
        RebuildTableRowViews();
        UpdateDataAssetTableChrome();
        RefreshProjectExplorer();
        SetFileStatus($"Added {descriptor.DisplayName} row: {folder}/{name}");
        await Task.CompletedTask;
    }

    private async Task DuplicateTableRowAsync(DataAssetEditState row)
    {
        var descriptor = _documents.Table.Type;
        if (_project is null || descriptor is null || !_documents.Table.Rows.Contains(row) || RejectWhenPlaying("Duplicate Row")) return;
        var serializer = new DataAssetSerializer(_components.Registry);
        var folder = Path.GetRelativePath(_project.RootDirectory, Path.GetDirectoryName(row.Path)!).Replace('\\', '/');
        var name = _project.NextDataAssetName(folder, TableFileBase(row.Path));
        var path = Path.Combine(_project.ResolveDirectoryPath(folder), name);
        _project.ValidateDataAssetPath(path);
        var id = Guid.NewGuid();
        var yaml = serializer.Serialize(row.Instance, row.Id);
        var (instance, _) = serializer.Deserialize(yaml);
        SceneFile.Write(path, serializer.Serialize(instance, id));
        var duplicate = new DataAssetEditState(path, instance, id, descriptor.TypeId);
        _documents.Table.Rows.Add(duplicate);
        TableView(duplicate).Height = TableView(row).Height;
        RefreshTableOwned();
        RebuildTableRowViews();
        UpdateDataAssetTableChrome();
        RefreshProjectExplorer();
        SetFileStatus($"Duplicated row: {folder}/{name}");
        await Task.CompletedTask;
    }

    private async Task DeleteTableRowAsync(DataAssetEditState row)
    {
        if (_project is null || !_documents.Table.Rows.Contains(row) || RejectWhenPlaying("Delete Row")) return;
        if (_documents.Asset is not null && string.Equals(_documents.Asset.Path, row.Path, PathComparison()))
            if (!await ConfirmCloseDataAsset()) return;
        var display = Path.GetRelativePath(_project.RootDirectory, row.Path).Replace('\\', '/');
        if (!await ConfirmExplorerDelete(display, isDirectory: false)) return;
        File.Delete(row.Path);
        if (TableView(row).Card is { } card) DetachInvalidFields(card);
        _documents.Table.Rows.Remove(row);
        _tableViews.Remove(row);
        RefreshTableOwned();
        RebuildTableRowViews();
        UpdateDataAssetTableChrome();
        RefreshProjectExplorer();
        SetFileStatus($"Deleted: {display}");
        await Task.CompletedTask;
    }

    private bool ContainsOpenTable(string path) => _documents.Table.Rows.Any(row =>
        string.Equals(row.Path, path, PathComparison())
        || row.Path.StartsWith(Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar, PathComparison()));

    /// <summary>Whether any table cell has invalid input. Table guards scope to table-owned boxes, never the shared scene count.</summary>
    private bool HasTableInputErrors =>
        DataAssetTableRows.GetVisualDescendants().OfType<TextBox>().Any(_invalidFields.Contains);

    /// <summary>Closes table rows after confirmation. Returns false when the user cancels and rows must stay open.</summary>
    private Task<bool> ConfirmCloseTableRows() => ConfirmTableRowsClose(closeOnConfirm: true);

    private async Task<bool> ConfirmTableRowsClose(bool closeOnConfirm)
    {
        if (!EditorOperationGate.NeedsUnsavedConfirmation(IsDataAssetTableDirty, HasTableInputErrors))
        {
            if (closeOnConfirm) ClearTableRowViews();
            return true;
        }
        var dialog = new Window
        {
            Title = "Unsaved Data Assets", Width = 420, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (label, result) in new[] { ("Save", "save"), ("Discard", "discard"), ("Cancel", "cancel") })
        {
            var button = new Button { Content = label, IsDefault = result == "save", IsCancel = result == "cancel" };
            button.Click += (_, _) => dialog.Close(result);
            buttons.Children.Add(button);
        }
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20), Spacing = 20,
            Children = { new TextBlock { Text = $"{_documents.Table.Rows.Count} table row(s) have unsaved changes or input errors. Save?", TextWrapping = Avalonia.Media.TextWrapping.Wrap }, buttons },
        };
        var answer = await dialog.ShowDialog<string?>(this);
        if (answer == "discard")
        {
            if (closeOnConfirm) ClearTableRowViews();
            UpdateDataAssetTableChrome();
            return true;
        }
        if (answer == "save" && await SaveDataAssetTableAsync())
        {
            if (closeOnConfirm) ClearTableRowViews();
            UpdateDataAssetTableChrome();
            return true;
        }
        return false;
    }

    private void RefreshTableAfterReload()
    {
        var heights = _tableViews.ToDictionary(entry => entry.Key.Path, entry => entry.Value.Height, StringComparer.FromComparison(PathComparison()));
        _tableViews.Clear();
        foreach (var row in _documents.Table.Rows) TableView(row).Height = heights.GetValueOrDefault(row.Path, DataAssetTableDefaultRowHeight);
        RebuildTableRowViews();
        UpdateDataAssetTableChrome();
    }
}
