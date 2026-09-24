using System.Collections;
using System.Reflection;
using Avalonia.Controls;
using PureEngine.Core;

namespace PureEngine.Editor;

public partial class MainWindow
{
    /// <summary>Asset file open in the Inspector. Edits apply to the instance; the file updates only on Save.</summary>
    private sealed class DataAssetEditState(string path, object instance, Guid id, string typeId)
    {
        public string Path { get; } = path;
        public object Instance { get; set; } = instance;
        public Guid Id { get; set; } = id;
        public string TypeId { get; } = typeId;
        public bool Dirty { get; set; }
    }

    private DataAssetEditState? _assetEdit;
    private readonly HashSet<object> _assetOwned = [with(ReferenceEqualityComparer.Instance)];

    /// <summary>Whether a data asset file is open in the Inspector. For tests.</summary>
    internal bool IsDataAssetActive => _assetEdit is not null;

    /// <summary>Builds an edit/play snapshot store for the project. Null without a project or on scan failure.</summary>
    private DataAssetStore? BuildProjectAssetStore(ComponentRegistry registry)
    {
        if (_project is null) return null;
        try
        {
            var store = DataAssetStore.ScanFolder(_project.RootDirectory, registry, out var diagnostics);
            foreach (var diagnostic in diagnostics) Log.Engine.Warning(diagnostic);
            return store;
        }
        catch (Exception error)
        {
            Log.Engine.Error("Cannot scan data assets.", error);
            return null;
        }
    }

    /// <summary>Routes an Inspector edit to the asset file or the scene by instance ownership.</summary>
    private void MarkEdited(object? owner)
    {
        if (owner is not null && _assetEdit is not null && _assetOwned.Contains(owner))
            MarkDataAssetChanged();
        else
            MarkSceneChanged();
    }

    private void MarkDataAssetChanged()
    {
        var state = _assetEdit;
        if (state is null || IsPlaying) return;
        state.Dirty = true;
        RefreshAssetOwned();
        DataAssetTitle.Text = "* " + Path.GetFileName(state.Path);
        SaveDataAssetButton.IsEnabled = true;
    }

    /// <summary>Rebuilds the asset-owned instance set from the open root. New nested instances join on the next routed edit.</summary>
    private void RefreshAssetOwned()
    {
        _assetOwned.Clear();
        if (_assetEdit is not null) CollectAssetObjects(_assetEdit.Instance, _assetOwned);
    }

    private static void CollectAssetObjects(object? value, HashSet<object> into)
    {
        if (value is null || value is string || value.GetType().IsValueType) return;
        if (!into.Add(value)) return;
        if (value is Array array)
        {
            foreach (var element in array) CollectAssetObjects(element, into);
            return;
        }
        if (value is IDictionary dictionary)
        {
            foreach (var entry in dictionary.Values) CollectAssetObjects(entry, into);
            return;
        }
        if (value is IEnumerable sequence && value.GetType() is { IsGenericType: true } sequenceType
            && sequenceType.GetGenericTypeDefinition() == typeof(List<>))
        {
            foreach (var element in sequence) CollectAssetObjects(element, into);
            return;
        }
        foreach (var member in ComponentSchema.GetInspectorMembers(value.GetType()))
            CollectAssetObjects(GetMemberValue(value, member), into);
    }

    /// <summary>Opens an asset file in the Inspector. Keeps the scene being edited untouched.</summary>
    private async Task OpenDataAssetForEdit(string path) => await RunFileOperation(() => OpenDataAssetCore(path));

    private async Task OpenDataAssetCore(string path)
    {
        if (_assetEdit is not null && string.Equals(_assetEdit.Path, Path.GetFullPath(path), PathComparison())) return;
        if (!await ConfirmCloseDataAsset())
        {
            ReselectAssetFile();
            return;
        }
        var (instance, id, typeId) = DataAssetFile.Load(Path.GetFullPath(path), _components.Registry);
        _assetEdit = new DataAssetEditState(Path.GetFullPath(path), instance, id, typeId);
        _invalidFields.Clear();
        SelectSceneObject(null, focus: false);
        _explorerSelectedFile = _assetEdit.Path;
        RefreshAssetOwned();
        RefreshObjectInspector();
        SetFileStatus($"Editing data asset: {Path.GetFileName(_assetEdit.Path)}");
        await Task.CompletedTask;
    }

    private void ReselectAssetFile()
    {
        if (_assetEdit is null)
        {
            ProjectFiles.SelectedItem = null;
            return;
        }
        foreach (var entry in ProjectFiles.Items.OfType<ProjectExplorerEntry>())
            if (entry.Kind == ProjectExplorerKind.DataAsset
                && string.Equals(entry.FullPath, _assetEdit.Path, PathComparison()))
            {
                ProjectFiles.SelectedItem = entry;
                return;
            }
        ProjectFiles.SelectedItem = null;
    }

    private void RefreshDataAssetInspector()
    {
        var state = _assetEdit;
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
        DataAssetEditors.Children.Clear();
        _invalidFields.Clear();
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
        var state = _assetEdit;
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
            yaml = new DataAssetSerializer(_components.Registry).Serialize(state.Instance, state.Id);
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
            _project?.ValidateDataAssetPath(state.Path);
            SceneFile.Write(state.Path, yaml);
        }
        catch (Exception error)
        {
            SetFileStatus($"Cannot save data asset: {error.GetBaseException().Message}", true);
            return false;
        }
        state.Dirty = false;
        RefreshDataAssetInspector();
        SetFileStatus($"Saved data asset: {Path.GetFileName(state.Path)}");
        await Task.CompletedTask;
        return true;
    }

    /// <summary>Closes the open asset after confirmation. Returns false when the user cancels and the asset must stay open.</summary>
    private async Task<bool> ConfirmCloseDataAsset()
    {
        var state = _assetEdit;
        if (state is null) return true;
        if (!state.Dirty)
        {
            CloseDataAssetForEdit();
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
            CloseDataAssetForEdit();
            return true;
        }
        if (answer == "save" && await SaveDataAssetAsync())
        {
            CloseDataAssetForEdit();
            return true;
        }
        return false;
    }

    private static string FileLabel(DataAssetEditState state) => Path.GetFileName(state.Path);

    private void CloseDataAssetForEdit()
    {
        _assetEdit = null;
        _assetOwned.Clear();
        _invalidFields.Clear();
        DataAssetInspector.IsVisible = false;
        RefreshObjectInspector();
    }

    /// <summary>Captures the open asset before a code reload so the new types can rebind afterward.</summary>
    private string? CaptureDataAssetForReload()
    {
        var state = _assetEdit;
        if (state is null) return null;
        try
        {
            return new DataAssetSerializer(_components.Registry).Serialize(state.Instance, state.Id);
        }
        catch (Exception error)
        {
            SetFileStatus($"Cannot preserve data asset across reload: {error.GetBaseException().Message}", true);
            return null;
        }
    }

    /// <summary>Rebinds the open asset to reloaded types. Keeps the old instance with an error when migration fails.</summary>
    private void RebindDataAssetAfterReload(string? yaml)
    {
        var state = _assetEdit;
        if (state is null || yaml is null) return;
        try
        {
            var (instance, id) = new DataAssetSerializer(_components.Registry).Deserialize(yaml, out _, out var membersChanged);
            state.Instance = instance;
            state.Id = id;
            state.Dirty = state.Dirty || membersChanged;
            RefreshAssetOwned();
            RefreshDataAssetInspector();
        }
        catch (Exception error)
        {
            SetFileStatus($"Data asset kept on previous code: {error.GetBaseException().Message} Close and reopen it to recover.", true);
        }
    }
}