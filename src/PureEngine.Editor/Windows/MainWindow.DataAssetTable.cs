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
    private sealed class TableRowView
    {
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
    internal bool IsDataAssetTableDirty => Documents.Table.IsDirty;

    /// <summary>Rebuilds the table type picker. Preserves the selection by type ID and rescans rows for it.</summary>
    private void RefreshDataAssetTableTypes(bool rescanRows = true)
    {
        _tableTypeChanging = true;
        try
        {
            ViewModel.DataAssetTable.RefreshTypes(_project, _components);
            DataAssetTableTypes.SelectedItem = ViewModel.DataAssetTable.SelectedType;
            if (Documents.Table.Type is null) ClearTableRowViews();
            else if (rescanRows) RescanTableRowsCore();
        }
        finally { _tableTypeChanging = false; }
        UpdateDataAssetTableChrome();
    }

    private async void OnDataAssetTableTypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_tableTypeChanging) return;
        var selected = DataAssetTableTypes.SelectedItem as DataAssetDescriptor;
        if (selected is not null && Documents.Table.Type is not null && selected.TypeId == Documents.Table.Type.TypeId) return;
        await RunFileOperation(async () =>
        {
            if (!await ConfirmCloseTableRows())
            {
                _tableTypeChanging = true;
                try
                {
                    DataAssetTableTypes.SelectedItem = ViewModel.DataAssetTable.SelectedType;
                }
                finally { _tableTypeChanging = false; }
                return;
            }
            ViewModel.DataAssetTable.SelectType(selected);
            if (selected is null) ClearTableRowViews();
            else RescanTableRowsCore();
            UpdateDataAssetTableChrome();
            await Task.CompletedTask;
        });
    }

    /// <summary>Reloads rows for the selected type from disk, keeping per-row heights by path.</summary>
    private void RescanTableRows()
    {
        if (Documents.Table.Type is null) return;
        RescanTableRowsCore();
        UpdateDataAssetTableChrome();
    }

    private void RescanTableRowsCore()
    {
        if (Documents.Table.Type is null || _project is null)
        {
            ClearTableRowViews();
            return;
        }
        DetachInvalidFields(DataAssetTableRows);
        DataAssetTableRows.Children.Clear();
        _tableViews.Clear();
        var error = ViewModel.DataAssetTable.Rescan(_project, _components.Registry);
        if (error is not null) SetFileStatus(error, true);
        RebuildTableRowViews();
    }

    /// <summary>Rebuilds the table-owned instance set from all rows. New nested instances join on the next routed edit.</summary>
    private void RefreshTableOwned() => Documents.Table.RefreshOwnership();

    private void RebuildTableRowViews()
    {
        DetachInvalidFields(DataAssetTableRows);
        DataAssetTableRows.Children.Clear();
        var descriptor = Documents.Table.Type;
        if (descriptor is null)
        {
            return;
        }
        var members = ComponentSchema.GetInspectorMembers(descriptor.Type);
        if (Documents.Table.Rows.Count == 0)
        {
            return;
        }
        ViewModel.DataAssetTable.Refresh();
        foreach (var row in Documents.Table.Rows) DataAssetTableRows.Children.Add(BuildTableRowCard(row, descriptor, members));
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
        var cells = new Grid { ColumnSpacing = 8, Height = ViewModel.DataAssetTable.RowHeight(row) };
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
            var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = Math.Max(24, ViewModel.DataAssetTable.RowHeight(row) - 30) };
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
            ViewModel.DataAssetTable.SetRowHeight(row, ViewModel.DataAssetTable.RowHeight(row) + e.Vector.Y);
            ApplyTableRowHeight(row);
        };
        body.Children.Add(grip);
        card.Child = body;
        TableView(row).Card = card;
        return card;
    }

    private void ApplyTableRowHeight(DataAssetEditState row)
    {
        TableView(row).Cells?.Height = ViewModel.DataAssetTable.RowHeight(row);
        foreach (var scroller in TableView(row).CellScrollers) scroller.MaxHeight = Math.Max(24, ViewModel.DataAssetTable.RowHeight(row) - 30);
    }

    private static string TableFileBase(string path) => DataAssetTableDocument.FileBase(path);

    private void UpdateDataAssetTableChrome()
    {
        ViewModel.DataAssetTable.Refresh();
        foreach (var row in Documents.Table.Rows)
            TableView(row).Title?.Text = $"{(row.Dirty ? "* " : "")}{Path.GetFileName(row.Path)}";
        var invalid = DataAssetTableRows.GetVisualDescendants().OfType<TextBox>().Count(IsInvalidInput);
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
        ViewModel.DataAssetTable.Clear();
        _tableViews.Clear();
    }

    private void OnSaveDataAssetTable(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _ = RunFileOperation(SaveDataAssetTableAsync);

    /// <summary>Saves every dirty table row. Serializes all rows before writing any file, keeping the dirty state on failure.</summary>
    internal Task<bool> SaveDataAssetTableAsync()
    {
        if (IsPlaying) return Task.FromResult(false);
        if (Documents.Table.Type is null || !IsDataAssetTableDirty) return Task.FromResult(true);
        var saved = ViewModel.DataAssetTable.Save(_components.Registry);
        if (saved)
        {
            UpdateDataAssetTableChrome();
            RefreshProjectExplorer();
        }
        SetFileStatus(ViewModel.DataAssetTable.OperationStatus, !saved);
        return Task.FromResult(saved);
    }

    private void OnAddDataAssetTableRow(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _ = RunFileOperation(AddDataAssetTableRowAsync);

    private async Task AddDataAssetTableRowAsync()
    {
        var descriptor = Documents.Table.Type;
        if (_project is null || descriptor is null || RejectWhenPlaying("Add Row")) return;
        ViewModel.DataAssetTable.Add(_project, _components.Registry);
        RebuildTableRowViews();
        UpdateDataAssetTableChrome();
        RefreshProjectExplorer();
        SetFileStatus(ViewModel.DataAssetTable.OperationStatus);
        await Task.CompletedTask;
    }

    private async Task DuplicateTableRowAsync(DataAssetEditState row)
    {
        var descriptor = Documents.Table.Type;
        if (_project is null || descriptor is null || !Documents.Table.Rows.Contains(row) || RejectWhenPlaying("Duplicate Row")) return;
        ViewModel.DataAssetTable.Duplicate(row, _project, _components.Registry);
        RebuildTableRowViews();
        UpdateDataAssetTableChrome();
        RefreshProjectExplorer();
        SetFileStatus(ViewModel.DataAssetTable.OperationStatus);
        await Task.CompletedTask;
    }

    private async Task DeleteTableRowAsync(DataAssetEditState row)
    {
        if (_project is null || !Documents.Table.Rows.Contains(row) || RejectWhenPlaying("Delete Row")) return;
        if (Documents.Asset is not null && string.Equals(Documents.Asset.Path, row.Path, PathComparison()))
            if (!await ConfirmCloseDataAsset()) return;
        var display = Path.GetRelativePath(_project.RootDirectory, row.Path).Replace('\\', '/');
        if (!await ConfirmExplorerDelete(display, isDirectory: false)) return;
        ViewModel.DataAssetTable.Delete(row);
        if (TableView(row).Card is { } card) DetachInvalidFields(card);
        _tableViews.Remove(row);
        RefreshTableOwned();
        RebuildTableRowViews();
        UpdateDataAssetTableChrome();
        RefreshProjectExplorer();
        SetFileStatus($"Deleted: {display}");
        await Task.CompletedTask;
    }

    private bool ContainsOpenTable(string path) => Documents.Table.Rows.Any(row =>
        string.Equals(row.Path, path, PathComparison())
        || row.Path.StartsWith(Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar, PathComparison()));

    /// <summary>Whether any table cell has invalid input. Table guards scope to table-owned boxes, never the shared scene count.</summary>
    private bool HasTableInputErrors =>
        DataAssetTableRows.GetVisualDescendants().OfType<TextBox>().Any(IsInvalidInput);

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
            Children = { new TextBlock { Text = $"{Documents.Table.Rows.Count} table row(s) have unsaved changes or input errors. Save?", TextWrapping = Avalonia.Media.TextWrapping.Wrap }, buttons },
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
        _tableViews.Clear();
        ViewModel.DataAssetTable.Refresh();
        RebuildTableRowViews();
        UpdateDataAssetTableChrome();
    }
}
