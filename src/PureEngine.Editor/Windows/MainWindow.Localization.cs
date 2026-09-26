using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using PureEngine.Core;

namespace PureEngine.Editor;

public partial class MainWindow
{
    /// <summary>Rebuilds the preview language list from the edit-scene snapshot. Keeps the selection when still available.</summary>
    internal void RefreshPreviewLanguages()
    {
        if (ViewModel.IsDisposed) return;
        ViewModel.Localization.RefreshLanguages(Documents.Current.Current.Localization);
    }

    /// <summary>Loads the table file for the project into the working copy and refreshes every consumer.</summary>
    internal void LoadLocalizationForProject()
    {
        ViewModel.Localization.LoadForProject(Project);
        SyncSceneLocalization();
        RebuildLocalizationRows();
        UpdateLocalizationChrome();
    }

    /// <summary>Pushes the saved working copy into the edit scene and preview. Runs only on load, save, and rescan.</summary>
    private void SyncSceneLocalization()
    {
        if (ViewModel.IsDisposed) return;
        Documents.Current.Current.Localization.Refresh(ViewModel.Localization.Document.Table.Clone());
        RefreshPreviewLanguages();
    }

    /// <summary>Duplicate or empty display keys. Saving and closing stay blocked until these are fixed.</summary>
    internal IReadOnlyList<string> ValidateLocalizationKeys()
    {
        List<string> problems = [];
        HashSet<string> seen = [];
        foreach (var entry in ViewModel.Localization.Document.Table.Entries)
        {
            var key = entry.Key.Trim();
            if (key.Length == 0)
                problems.Add("A key is empty.");
            else if (!seen.Add(key))
                problems.Add($"Duplicate key '{key}'.");
        }
        return problems;
    }

    private bool HasLocalizationErrors => ValidateLocalizationKeys().Count > 0;

    private void MarkLocalizationDirty()
    {
        ViewModel.Localization.Document.MarkDirty();
        UpdateLocalizationChrome();
    }

    private void UpdateLocalizationChrome()
    {
        var problems = ValidateLocalizationKeys();
        LocalizationError.Text = problems.Count == 0 ? "" : string.Join(" ", problems);
        LocalizationError.IsVisible = problems.Count > 0;
        ViewModel.Localization.Refresh();
    }

    private void RebuildLocalizationRows()
    {
        _rebuildingLocalizationRows = true;
        try
        {
            RebuildLocalizationRowsCore();
        }
        finally
        {
            _rebuildingLocalizationRows = false;
        }
        UpdateLocalizationChrome();
    }

    private bool _rebuildingLocalizationRows;

    private void RebuildLocalizationRowsCore()
    {
        LocalizationRows.Children.Clear();
        var table = ViewModel.Localization.Document.Table;
        var header = new Grid { ColumnSpacing = 8, Margin = new Thickness(4, 0) };
        SetLocalizationColumns(header, table.Languages);
        var corner = new TextBlock
        {
            Text = "Key",
            Classes = { "caption" },
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(corner, 0);
        header.Children.Add(corner);
        for (var i = 0; i < table.Languages.Count; i++)
        {
            var code = table.Languages[i];
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            title.Children.Add(new TextBlock
            {
                Text = code,
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (table.Languages.Count > 1)
            {
                var remove = BuildRemoveButton($"Localization.Language.{code}.Remove");
                ToolTip.SetTip(remove, $"Remove language '{code}'.");
                var captured = code;
                remove.Click += (_, _) => RemoveLocalizationLanguage(captured);
                title.Children.Add(remove);
            }
            Grid.SetColumn(title, i + 1);
            header.Children.Add(title);
        }
        LocalizationRows.Children.Add(header);
        foreach (var entry in table.Entries.OrderBy(item => item.Key, StringComparer.Ordinal))
            LocalizationRows.Children.Add(BuildLocalizationRow(entry, table.Languages));
        UpdateLocalizationChrome();
    }

    private static void SetLocalizationColumns(Grid grid, List<string> languages)
    {
        grid.ColumnDefinitions.Add(new ColumnDefinition(160, GridUnitType.Pixel) { SharedSizeGroup = "LocKey" });
        foreach (var code in languages)
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { SharedSizeGroup = $"LocLang_{code}", MinWidth = 200 });
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = "LocDelete" });
    }

    private Border BuildLocalizationRow(LocalizationEntry entry, List<string> languages)
    {
        var card = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            Background = (Avalonia.Media.IBrush?)Application.Current!.FindResource("EditorFloatBrush"),
        };
        var grid = new Grid { ColumnSpacing = 8 };
        SetLocalizationColumns(grid, languages);
        var tag = ShortId(entry.Id);
        var keyBox = new TextBox
        {
            Text = entry.Key,
            VerticalAlignment = VerticalAlignment.Top,
        };
        ToolTip.SetTip(keyBox, $"{entry.Key} ({entry.Id:D})");
        keyBox.SetValue(AutomationProperties.NameProperty, $"Localization.{tag}.Key");
        keyBox.TextChanged += (_, _) =>
        {
            if (_rebuildingLocalizationRows || IsPlaying) return;
            var key = keyBox.Text ?? "";
            if (entry.Key == key) return;
            entry.Key = key;
            ToolTip.SetTip(keyBox, $"{entry.Key} ({entry.Id:D})");
            MarkLocalizationDirty();
        };
        Grid.SetColumn(keyBox, 0);
        grid.Children.Add(keyBox);
        for (var i = 0; i < languages.Count; i++)
        {
            var code = languages[i];
            var cell = new StackPanel { Spacing = 4 };
            var textBox = new TextBox
            {
                Text = entry.Texts.GetValueOrDefault(code) ?? "",
                AcceptsReturn = true,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                MinHeight = 52,
            };
            ToolTip.SetTip(textBox, $"{entry.Key} : {code}");
            textBox.SetValue(AutomationProperties.NameProperty, $"Localization.{tag}.Text.{code}");
            textBox.TextChanged += (_, _) =>
            {
                if (_rebuildingLocalizationRows || IsPlaying) return;
                var text = textBox.Text ?? "";
                if ((entry.Texts.GetValueOrDefault(code) ?? "") == text) return;
                if (text.Length == 0) entry.Texts.Remove(code);
                else entry.Texts[code] = text;
                MarkLocalizationDirty();
            };
            var voiceBox = new TextBox
            {
                Text = entry.Voices.GetValueOrDefault(code) ?? "",
                PlaceholderText = "voice clip (optional)",
            };
            ToolTip.SetTip(voiceBox, $"{entry.Key} : {code} voice");
            voiceBox.SetValue(AutomationProperties.NameProperty, $"Localization.{tag}.Voice.{code}");
            voiceBox.TextChanged += (_, _) =>
            {
                if (_rebuildingLocalizationRows || IsPlaying) return;
                var voice = voiceBox.Text ?? "";
                if ((entry.Voices.GetValueOrDefault(code) ?? "") == voice) return;
                if (voice.Length == 0) entry.Voices.Remove(code);
                else entry.Voices[code] = voice;
                MarkLocalizationDirty();
            };
            cell.Children.Add(textBox);
            cell.Children.Add(voiceBox);
            Grid.SetColumn(cell, i + 1);
            grid.Children.Add(cell);
        }
        var delete = BuildRemoveButton($"Localization.{tag}.Delete");
        ToolTip.SetTip(delete, $"Delete key '{entry.Key}'.");
        delete.Click += (_, _) => DeleteLocalizationKey(entry.Id);
        Grid.SetColumn(delete, languages.Count + 1);
        grid.Children.Add(delete);
        card.Child = grid;
        return card;
    }

    private void DeleteLocalizationKey(Guid id)
    {
        if (Project is null || RejectWhenPlaying("Delete key")) return;
        var table = ViewModel.Localization.Document.Table;
        var entry = table.Entries.FirstOrDefault(item => item.Id == id);
        if (entry is null) return;
        table.Entries.Remove(entry);
        MarkLocalizationDirty();
        RebuildLocalizationRows();
    }

    private void RemoveLocalizationLanguage(string code)
    {
        if (Project is null || RejectWhenPlaying("Remove language")) return;
        var table = ViewModel.Localization.Document.Table;
        if (table.Languages.Count <= 1 || !table.Languages.Contains(code)) return;
        table.Languages.Remove(code);
        foreach (var row in table.Entries)
        {
            row.Texts.Remove(code);
            row.Voices.Remove(code);
        }
        MarkLocalizationDirty();
        RebuildLocalizationRows();
    }

    private async void OnAddLocalizationKey(object? sender, RoutedEventArgs e) =>
        await RunFileOperation(async () =>
        {
            if (Project is null || RejectWhenPlaying("Add key")) return;
            var table = ViewModel.Localization.Document.Table;
            var key = "menu.new";
            for (var number = 2; table.Entries.Any(item => item.Key == key); number++)
                key = $"menu.new {number}";
            table.Entries.Add(new LocalizationEntry { Key = key });
            MarkLocalizationDirty();
            RebuildLocalizationRows();
            SetFileStatus($"Added key: {key}");
            await Task.CompletedTask;
        });

    private async void OnAddLocalizationLanguage(object? sender, RoutedEventArgs e) =>
        await RunFileOperation(async () =>
        {
            if (Project is null || RejectWhenPlaying("Add language")) return;
            var raw = AddLocalizationLanguageBox.Text ?? "";
            var code = raw.Trim().ToLowerInvariant();
            if (!LocalizationService.IsValidLanguageCode(code))
            {
                SetFileStatus($"Invalid language code '{raw}'. Use a short tag such as ko.", true);
                return;
            }
            var table = ViewModel.Localization.Document.Table;
            if (table.Languages.Contains(code))
            {
                SetFileStatus($"Language '{code}' already exists.", true);
                return;
            }
            table.Languages.Add(code);
            AddLocalizationLanguageBox.Text = "";
            MarkLocalizationDirty();
            RebuildLocalizationRows();
            SetFileStatus($"Added language: {code}");
            await Task.CompletedTask;
        });

    private Task<bool> SaveLocalizationTableAsync() => Task.FromResult(SaveLocalizationTable());

    private bool SaveLocalizationTable()
    {
        if (HasLocalizationErrors)
        {
            UpdateLocalizationChrome();
            SetFileStatus("Fix the localization key errors before saving.", true);
            return false;
        }
        if (!ViewModel.SaveLocalization()) return false;
        SyncSceneLocalization();
        RebuildLocalizationRows();
        RefreshObjectInspector();
        RefreshProjectExplorer();
        return true;
    }

    private async Task RescanLocalizationRows()
    {
        if (Project is null) return;
        if (!await ConfirmLocalizationClose()) return;
        ViewModel.Localization.LoadForProject(Project);
        SyncSceneLocalization();
        RebuildLocalizationRows();
        RefreshObjectInspector();
    }

    private async Task<bool> ConfirmLocalizationClose()
    {
        if (!EditorOperationGate.NeedsUnsavedConfirmation(ViewModel.Localization.Document.IsDirty, HasLocalizationErrors))
            return true;
        var dialog = new Window
        {
            Title = "Unsaved Localization", Width = 420, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (label, result) in new[] { ("Save", "save"), ("Discard", "discard"), ("Cancel", "cancel") })
        {
            var button = new Avalonia.Controls.Button { Content = label, IsDefault = result == "save", IsCancel = result == "cancel" };
            button.Click += (_, _) => dialog.Close(result);
            buttons.Children.Add(button);
        }
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 20,
            Children = { new TextBlock { Text = "The localization table has unsaved changes. Save?", TextWrapping = Avalonia.Media.TextWrapping.Wrap }, buttons },
        };
        var answer = await dialog.ShowDialog<string?>(this);
        if (answer == "discard") return true;
        if (answer == "save" && await SaveLocalizationTableAsync())
            return true;
        return false;
    }

    private bool ContainsOpenLocalization(string path) =>
        ViewModel.Localization.Document.Path is { } open
        && string.Equals(open, Path.GetFullPath(path), PathComparison());
}
