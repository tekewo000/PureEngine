using Avalonia.Controls;
using PureEngine.Core;

namespace PureEngine.Editor;

public partial class MainWindow
{
    private bool _assetSelectionChanging;

    private bool ContainsOpenDataAsset(string path) => Documents.Asset is { } state
        && (string.Equals(state.Path, path, PathComparison())
            || state.Path.StartsWith(Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar, PathComparison()));

    /// <summary>Whether a data asset file is open in the Inspector. For tests.</summary>
    internal bool IsDataAssetActive => Documents.Asset is not null;

    /// <summary>Builds an edit/play snapshot store for the project. Null without a project or on scan failure.</summary>
    private DataAssetStore? BuildProjectAssetStore(ComponentRegistry registry) =>
        EditorDocuments.BuildAssetStore(Project, registry);

    private void MarkEdited(object? owner) => ViewModel.Inspector.MarkEdited(owner);

    /// <summary>Rebuilds the asset-owned instance set from the open root. New nested instances join on the next routed edit.</summary>
    private void RefreshAssetOwned() => Documents.RefreshAssetOwnership();

    /// <summary>Opens an asset file in the Inspector. Keeps the scene being edited untouched.</summary>
    private async Task OpenDataAssetForEdit(string path) => await RunFileOperation(() => OpenDataAssetCore(path));

    private async Task OpenDataAssetCore(string path)
    {
        if (Documents.Asset is not null && string.Equals(Documents.Asset.Path, Path.GetFullPath(path), PathComparison())) return;
        if (Documents.Asset is null && HasInputErrors)
            throw new InvalidOperationException("Fix the scene Inspector input errors before opening a data asset.");
        // Validate the destination before closing the current editing document.
        var candidate = DataAssetViewModel.PrepareOpen(path, Components.Registry, Project);
        if (!await ConfirmCloseDataAsset())
        {
            ReselectAssetFile();
            return;
        }
        ViewModel.DataAsset.Adopt(candidate);
        DetachInvalidFields(ComponentEditors);
        ComponentEditors.Children.Clear();
        SelectSceneObject(null, focus: false);
        ViewModel.Project.SelectedFile = candidate.Path;
        RefreshAssetOwned();
        RefreshObjectInspector();
        SetFileStatus($"Editing data asset: {Path.GetFileName(candidate.Path)}");
        await Task.CompletedTask;
    }

    private void ReselectAssetFile()
    {
        if (Documents.Asset is null)
        {
            ProjectFiles.SelectedItem = null;
            return;
        }
        foreach (var entry in ProjectFiles.Items.OfType<ProjectExplorerEntry>())
            if (entry.Kind == ProjectExplorerKind.DataAsset
                && string.Equals(entry.FullPath, Documents.Asset.Path, PathComparison()))
            {
                ProjectFiles.SelectedItem = entry;
                return;
            }
        ProjectFiles.SelectedItem = null;
    }

    private void RefreshDataAssetInspector()
    {
        if (DataAssetEditors.Children.Count > 0 && DataAssetInspector.IsVisible && ViewModel.Inspector.InvalidCount > 0) return;
        ViewModel.DataAsset.Refresh(clearError: true);
        var state = Documents.Asset;
        if (state is null) return;
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

    private Task<bool> SaveDataAssetAsync() => Task.FromResult(ViewModel.SaveDataAsset());

    /// <summary>Closes the open asset after confirmation. Returns false when the user cancels and the asset must stay open.</summary>
    private Task<bool> ConfirmCloseDataAsset() => ConfirmDataAssetClose(closeOnConfirm: true);

    private async Task<bool> ConfirmDataAssetClose(bool closeOnConfirm)
    {
        var state = Documents.Asset;
        if (state is null) return true;
        if (!EditorOperationGate.NeedsUnsavedConfirmation(state.Dirty, ViewModel.Inspector.InvalidCount > 0))
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
        ViewModel.DataAsset.Close();
        DetachInvalidFields(DataAssetEditors);
        DataAssetEditors.Children.Clear();
        RefreshObjectInspector();
    }

}
