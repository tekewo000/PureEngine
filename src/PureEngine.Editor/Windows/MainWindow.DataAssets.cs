using Avalonia.Controls;
using PureEngine.Core;

namespace PureEngine.Editor;

public partial class MainWindow
{
    private bool _assetSelectionChanging;

    private bool ContainsOpenDataAsset(string path) => _documents.Asset is { } state
        && (string.Equals(state.Path, path, PathComparison())
            || state.Path.StartsWith(Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar, PathComparison()));

    /// <summary>Whether a data asset file is open in the Inspector. For tests.</summary>
    internal bool IsDataAssetActive => _documents.Asset is not null;

    /// <summary>Builds an edit/play snapshot store for the project. Null without a project or on scan failure.</summary>
    private DataAssetStore? BuildProjectAssetStore(ComponentRegistry registry) =>
        EditorDocuments.BuildAssetStore(_project, registry);

    /// <summary>Routes an Inspector edit to the asset table, the asset file, or the scene by instance ownership.</summary>
    private void MarkEdited(object? owner)
    {
        if (owner is not null && _documents.Table.Owned.Contains(owner))
            MarkTableRowDirty(owner);
        else if (owner is not null && _documents.Asset is not null && _documents.AssetOwned.Contains(owner))
            MarkDataAssetChanged();
        else
            MarkSceneChanged();
    }

    private void MarkDataAssetChanged()
    {
        var state = _documents.Asset;
        if (state is null || IsPlaying) return;
        state.Dirty = true;
        RefreshAssetOwned();
        DataAssetTitle.Text = "* " + Path.GetFileName(state.Path);
        SaveDataAssetButton.IsEnabled = true;
    }

    /// <summary>Rebuilds the asset-owned instance set from the open root. New nested instances join on the next routed edit.</summary>
    private void RefreshAssetOwned() => _documents.RefreshAssetOwnership();

    /// <summary>Opens an asset file in the Inspector. Keeps the scene being edited untouched.</summary>
    private async Task OpenDataAssetForEdit(string path) => await RunFileOperation(() => OpenDataAssetCore(path));

    private async Task OpenDataAssetCore(string path)
    {
        if (_documents.Asset is not null && string.Equals(_documents.Asset.Path, Path.GetFullPath(path), PathComparison())) return;
        if (_documents.Asset is null && HasInputErrors)
            throw new InvalidOperationException("Fix the scene Inspector input errors before opening a data asset.");
        _project?.ValidateDataAssetPath(path);
        // Validate the destination before closing the current editing document.
        var (instance, id) = new DataAssetSerializer(_components.Registry).Deserialize(
            File.ReadAllText(path), out var typeId, out var membersChanged);
        if (!await ConfirmCloseDataAsset())
        {
            ReselectAssetFile();
            return;
        }
        _documents.Asset = new DataAssetEditState(Path.GetFullPath(path), instance, id, typeId) { Dirty = membersChanged };
        DetachInvalidFields(ComponentEditors);
        ComponentEditors.Children.Clear();
        NameError.IsVisible = false;
        SelectSceneObject(null, focus: false);
        _explorerSelectedFile = _documents.Asset.Path;
        RefreshAssetOwned();
        RefreshObjectInspector();
        SetFileStatus($"Editing data asset: {Path.GetFileName(_documents.Asset.Path)}");
        await Task.CompletedTask;
    }

    private void ReselectAssetFile()
    {
        if (_documents.Asset is null)
        {
            ProjectFiles.SelectedItem = null;
            return;
        }
        foreach (var entry in ProjectFiles.Items.OfType<ProjectExplorerEntry>())
            if (entry.Kind == ProjectExplorerKind.DataAsset
                && string.Equals(entry.FullPath, _documents.Asset.Path, PathComparison()))
            {
                ProjectFiles.SelectedItem = entry;
                return;
            }
        ProjectFiles.SelectedItem = null;
    }

    private void RefreshDataAssetInspector()
    {
        if (DataAssetInspector.IsVisible && _invalidFields.Count > 0) return;
        var state = _documents.Asset;
        if (state is null)
        {
            DataAssetInspector.IsVisible = false;
            return;
        }
        ObjectInspector.IsVisible = false;
        DataAssetInspector.IsVisible = true;
        DataAssetTitle.Text = (state.Dirty ? "* " : "") + Path.GetFileName(state.Path);
        DataAssetId.Text = state.Id.ToString("D");
        ToolTip.SetTip(DataAssetId, state.Id.ToString("D"));
        DataAssetType.Text = state.TypeId;
        ToolTip.SetTip(DataAssetType, state.Instance.GetType().FullName);
        SaveDataAssetButton.IsEnabled = state.Dirty && !IsPlaying;
        DataAssetError.IsVisible = false;
        DetachInvalidFields(DataAssetEditors);
        DataAssetEditors.Children.Clear();
        var members = ComponentSchema.GetInspectorMembers(state.Instance.GetType());
        foreach (var member in members)
        {
            DataAssetEditors.Children.Add(new Separator { Classes = { "divider" }, Margin = new Avalonia.Thickness(0, 2) });
            DataAssetEditors.Children.Add(BuildMemberRow(state.Instance, member));
        }
        if (members.Count == 0)
            DataAssetEditors.Children.Add(new TextBlock { Classes = { "hint" }, Text = "No editable fields." });
        UpdateErrorBadge();
    }

    private async void OnSaveDataAsset(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await RunFileOperation(SaveDataAssetAsync);

    /// <summary>Saves the open asset. Returns false when input errors or serialization fail, keeping the dirty state.</summary>
    private async Task<bool> SaveDataAssetAsync()
    {
        if (IsPlaying) return false;
        var state = _documents.Asset;
        if (state is null) return true;
        var saveBlock = EditorOperationGate.SaveBlockReason(_invalidFields.Count > 0);
        if (saveBlock is not null)
        {
            SetFileStatus($"Cannot save data asset. {saveBlock}", true);
            return false;
        }
        string yaml;
        try
        {
            yaml = state.Serialize(_components.Registry);
        }
        catch (Exception error)
        {
            DataAssetError.Text = error.GetBaseException().Message;
            DataAssetError.IsVisible = true;
            SetFileStatus($"Cannot save data asset: {error.GetBaseException().Message}", true);
            return false;
        }
        try
        {
            state.Save(yaml, _project);
        }
        catch (Exception error)
        {
            SetFileStatus($"Cannot save data asset: {error.GetBaseException().Message}", true);
            return false;
        }
        RefreshDataAssetInspector();
        SetFileStatus($"Saved data asset: {Path.GetFileName(state.Path)}");
        await Task.CompletedTask;
        return true;
    }

    /// <summary>Closes the open asset after confirmation. Returns false when the user cancels and the asset must stay open.</summary>
    private Task<bool> ConfirmCloseDataAsset() => ConfirmDataAssetClose(closeOnConfirm: true);

    private async Task<bool> ConfirmDataAssetClose(bool closeOnConfirm)
    {
        var state = _documents.Asset;
        if (state is null) return true;
        if (!EditorOperationGate.NeedsUnsavedConfirmation(state.Dirty, _invalidFields.Count > 0))
        {
            if (closeOnConfirm) CloseDataAssetForEdit();
            return true;
        }
        var dialog = new Window
        {
            Title = "Unsaved Data Asset", Width = 420, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        foreach (var (label, result) in new[] { ("Save", "save"), ("Discard", "discard"), ("Cancel", "cancel") })
        {
            var button = new Avalonia.Controls.Button { Content = label, IsDefault = result == "save", IsCancel = result == "cancel" };
            button.Click += (_, _) => dialog.Close(result);
            buttons.Children.Add(button);
        }
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20), Spacing = 20,
            Children = { new TextBlock { Text = $"{FileLabel(state)} has unsaved changes. Save?", TextWrapping = Avalonia.Media.TextWrapping.Wrap }, buttons },
        };
        var answer = await dialog.ShowDialog<string?>(this);
        if (answer == "discard")
        {
            if (closeOnConfirm) CloseDataAssetForEdit();
            return true;
        }
        if (answer == "save" && await SaveDataAssetAsync())
        {
            if (closeOnConfirm) CloseDataAssetForEdit();
            return true;
        }
        return false;
    }

    private static string FileLabel(DataAssetEditState state) => Path.GetFileName(state.Path);

    private void CloseDataAssetForEdit()
    {
        _documents.Asset = null;
        _documents.AssetOwned.Clear();
        DetachInvalidFields(DataAssetEditors);
        DataAssetEditors.Children.Clear();
        DataAssetInspector.IsVisible = false;
        RefreshObjectInspector();
    }

}
