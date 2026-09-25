using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using PureEngine.Core;
using PureEngine.Runtime;

namespace PureEngine.Editor;

public partial class MainWindow
{
    /// <summary>Viewport tab order: Scene View, Game, Data Assets table, Prefab Editor. Prefab indices follow the table.</summary>
    internal const int SceneViewportIndex = 0;
    internal const int GameViewportIndex = 1;
    internal const int DataAssetTableViewportIndex = 2;
    internal const int PrefabViewportIndex = 3;

    private EditSceneStore? _prefabScene;
    private Guid _prefabId;
    private bool _switchingViewport;
    private int _activeViewportIndex;
    private readonly EditingViewState _sceneViewState = new();
    private EditingViewState _prefabViewState = new();

    private sealed class EditingViewState
    {
        public HashSet<Guid> Expanded { get; set; } = [];
        public Guid? SelectionId { get; set; }
        public Vector2 Pan { get; set; }
        public float Zoom { get; set; } = 1;
    }

    internal bool IsPrefabEditing => _prefabScene is not null && ReferenceEquals(_editScene, _prefabScene);

    private SceneObject? EditingParent() =>
        GetSelectedSceneObject() ?? (IsPrefabEditing ? _prefabScene!.Current.RootObjects.Single() : null);

    private bool IsPrefabRoot(SceneObject item) => IsPrefabEditing && item.Parent is null;

    private async Task OpenPrefabEditorAsync(string path) =>
        await RunFileOperation(() => OpenPrefabEditorCore(path));

    internal async Task OpenPrefabEditorCore(string path)
    {
        if (RejectWhenPlaying("Open Prefab")) return;
        if (_project is null) throw new InvalidOperationException("Open a project first.");
        _project.ValidatePrefabPath(path);
        if (HasInputErrors && _assetEdit is null && !IsPrefabEditing)
        {
            SetFileStatus("Fix the Inspector input errors before switching editing documents.", true);
            return;
        }
        if (_prefabScene?.Path is { } opened && SamePath(opened, path))
        {
            if (!await ConfirmCloseDataAsset()) return;
            ActivateEditorViewport(PrefabViewportIndex);
            return;
        }

        // Validate a replacement before prompting away the old document. Each document owns its services.
        var candidate = new EditSceneStore(new Scene());
        var ownsCandidate = true;
        try
        {
            var assets = BuildProjectAssetStore(_components.Registry);
            candidate.ReplaceServices(GameSession.Create(services =>
            {
                GameServices.ForProject(_components)(services);
                if (assets is not null) services.AddSingleton(assets);
            })).Dispose();
            var restored = PrefabFile.OpenForEditing(path, _components.Registry, out var id, out var changed,
                assets, BuildPrefabCatalog(), candidate.Services.Factory);
            candidate.Replace(restored, Path.GetFullPath(path), changed);
            if (!await ConfirmDataAssetClose(closeOnConfirm: false)
                || !await ConfirmPrefabEditorClose(closeOnConfirm: false)) return;
            if (_assetEdit is not null) CloseDataAssetForEdit();
            ClosePrefabEditor();
            _prefabScene = candidate;
            ownsCandidate = false;
            _prefabId = id;
            _prefabViewState = new EditingViewState
            {
                SelectionId = restored.RootObjects.Single().Id,
                Expanded = [restored.RootObjects.Single().Id],
            };
            PrefabEditorTab.IsVisible = true;
            ActivateEditorViewport(PrefabViewportIndex);
            UpdatePrefabEditorChrome();
            SetFileStatus($"Editing prefab: {Path.GetFileName(path)}");
        }
        catch
        {
            // A user Inspector getter may fail during activation. Never leave a partially adopted document.
            if (ReferenceEquals(candidate, _prefabScene)) ClosePrefabEditor();
            throw;
        }
        finally
        {
            if (ownsCandidate) DisposeEditingDocument(candidate);
        }
    }

    private async void OnEditorViewportChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_switchingViewport || !ReferenceEquals(e.Source, ViewportTabs)) return;
        var requested = ViewportTabs.SelectedIndex;
        if (requested == _activeViewportIndex) return;
        SetViewportSelection(_activeViewportIndex);
        if (_fileBusy || (requested == PrefabViewportIndex && (_prefabScene is null || IsPlaying))
            || (requested == DataAssetTableViewportIndex && IsPlaying)) return;
        // The table is an additional view, not a document replacement: it never forces the single asset closed.
        if (requested != DataAssetTableViewportIndex && HasInputErrors && _assetEdit is null)
        {
            SetFileStatus("Fix the Inspector input errors before switching editing documents.", true);
            return;
        }
        if (_assetEdit is not null && requested != DataAssetTableViewportIndex)
        {
            await RunFileOperation(async () =>
            {
                if (await ConfirmCloseDataAsset()) ActivateEditorViewport(requested);
            });
            return;
        }
        ActivateEditorViewport(requested);
    }

    private void SetViewportSelection(int index)
    {
        _switchingViewport = true;
        try { ViewportTabs.SelectedIndex = index; }
        finally { _switchingViewport = false; }
    }

    /// <summary>Switches the shared editing surface, hierarchy, and Inspector together without replacing either document.</summary>
    private void ActivateEditorViewport(int index)
    {
        var prefab = index == PrefabViewportIndex && _prefabScene is not null;
        CancelSceneViewDrag();
        var contextChanged = prefab != IsPrefabEditing;
        if (contextChanged)
        {
            var previous = IsPrefabEditing ? _prefabViewState : _sceneViewState;
            previous.Expanded = [.. EnumerateHierarchyNodes().Where(node => node.IsExpanded).Select(node => node.Ref.Id)];
            previous.SelectionId = GetSelectedSceneObject()?.Id;
            previous.Pan = _scenePan;
            previous.Zoom = _sceneZoom;
            ClearHierarchyDropIndicator();
            _hierarchyPress = null;
            _hierarchyDragId = null;
            _editScene = prefab ? _prefabScene! : _sceneDocument;
            var next = prefab ? _prefabViewState : _sceneViewState;
            _scenePan = next.Pan;
            _sceneZoom = next.Zoom;
            _sceneDrawFailures.Clear();
            _hierarchyRefreshing = true;
            try
            {
                _hierarchyRoots = StuffsHierarchy.Build(_editScene.Current);
                foreach (var node in EnumerateHierarchyNodes())
                    node.IsExpanded = next.Expanded.Contains(node.Ref.Id);
                SceneObjects.ItemsSource = _hierarchyRoots;
                SceneObjects.SelectedItem = _assetEdit is null
                    ? EnumerateHierarchyNodes().FirstOrDefault(node => node.Ref.Id == next.SelectionId) : null;
            }
            finally { _hierarchyRefreshing = false; }
            // Reuse the exact Scene View rendering and input implementation, not a second editor.
            SceneViewHost.Children.Remove(SceneViewport);
            PrefabViewHost.Children.Remove(SceneViewport);
            (prefab ? PrefabViewHost : SceneViewHost).Children.Add(SceneViewport);
        }
        _activeViewportIndex = index;
        SetViewportSelection(index);
        UpdateSceneTitle();
        UpdatePrefabEditorChrome();
        if (contextChanged) RefreshObjectInspector();
    }

    private void UpdatePrefabEditorChrome()
    {
        if (PrefabEditorTab is null) return;
        var name = Path.GetFileName(_prefabScene?.Path);
        var dirty = _prefabScene?.IsDirty == true;
        PrefabEditorTab.Header = dirty ? "Prefab Editor *" : "Prefab Editor";
        PrefabEditorTitle.Text = $"{name}{(dirty ? " *" : "")}";
        PrefabEditorPath.Text = _prefabScene?.Path is { } path && _project is not null
            ? Path.GetRelativePath(_project.RootDirectory, path) : "";
        ToolTip.SetTip(PrefabEditorPath, _prefabScene?.Path);
        StuffsContext.IsVisible = IsPrefabEditing;
        StuffsContext.Text = $"Prefab: {name}";
        ToolTip.SetTip(StuffsContext, _prefabScene?.Path);
        SavePrefabEditorButton.IsEnabled = _prefabScene is not null && !IsPlaying;
        SaveSceneMenu.Header = IsPrefabEditing ? "Save Prefab" : "Save Scene";
        SaveSceneAsMenu.IsEnabled = !IsPrefabEditing && !IsPlaying;
        PrefabEditorTab.IsEnabled = !IsPlaying;
    }

    private async void OnSavePrefabEditor(object? sender, RoutedEventArgs e) =>
        await RunFileOperation(async () => { SavePrefabEditor(); await Task.CompletedTask; });

    internal bool SavePrefabEditor()
    {
        CancelSceneViewDrag();
        if (_prefabScene is null || RejectWhenPlaying("Save Prefab")) return false;
        if (IsPrefabEditing && _assetEdit is null && HasInputErrors)
        {
            SetFileStatus("Cannot save prefab. Fix the Inspector input errors.", true);
            return false;
        }
        var path = _prefabScene.Path!;
        _project!.ValidatePrefabPath(path);
        PrefabFile.Save(path, _prefabScene.Current, _prefabId, _components.Registry);
        _prefabScene.MarkSaved(path);
        UpdateSceneTitle();
        UpdatePrefabEditorChrome();
        SetFileStatus($"Saved prefab: {Path.GetFileName(path)}");
        return true;
    }

    private async void OnClosePrefabEditor(object? sender, RoutedEventArgs e) =>
        await RunFileOperation(async () => await ConfirmClosePrefabEditor());

    private Task<bool> ConfirmClosePrefabEditor() => ConfirmPrefabEditorClose(closeOnConfirm: true);

    private async Task<bool> ConfirmPrefabEditorClose(bool closeOnConfirm)
    {
        if (_prefabScene is null) return true;
        CancelSceneViewDrag();
        if (closeOnConfirm && IsPrefabEditing && !await ConfirmDataAssetClose(closeOnConfirm: false)) return false;
        if (_prefabScene.IsDirty || (IsPrefabEditing && _assetEdit is null && HasInputErrors))
        {
            var dialog = new Window
            {
                Title = "Unsaved Prefab", Width = 420, SizeToContent = SizeToContent.Height,
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
                Children =
                {
                    new TextBlock { Text = $"{Path.GetFileName(_prefabScene.Path)} has unsaved changes. Save?", TextWrapping = TextWrapping.Wrap },
                    buttons,
                },
            };
            var answer = await dialog.ShowDialog<string?>(this);
            if (answer != "discard" && !(answer == "save" && SavePrefabEditor())) return false;
        }
        if (closeOnConfirm)
        {
            if (IsPrefabEditing && _assetEdit is not null) CloseDataAssetForEdit();
            ClosePrefabEditor();
        }
        return true;
    }

    internal void ClosePrefabEditor()
    {
        var previous = _prefabScene;
        if (previous is null) return;
        try
        {
            if (IsPrefabEditing) ActivateEditorViewport(0);
        }
        finally
        {
            _editScene = _sceneDocument;
            _prefabScene = null;
            _prefabViewState = new EditingViewState();
            PrefabEditorTab.IsVisible = false;
            UpdatePrefabEditorChrome();
            try { DisposeEditingDocument(previous); }
            finally { QueuePendingUserCodeReload(); }
        }
    }

    private static void DisposeEditingDocument(EditSceneStore document)
    {
        try { ComponentAssets.DisposeComponents(document.Reset().OwnedComponents); }
        finally { document.Services.Dispose(); }
    }
}
