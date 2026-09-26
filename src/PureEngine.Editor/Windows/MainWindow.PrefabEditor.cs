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
    /// <summary>Viewport tab order: Scene View, Game, Data Assets table, Localization, Prefab Editor. Prefab indices follow the tables.</summary>
    internal const int SceneViewportIndex = 0;
    internal const int GameViewportIndex = 1;
    internal const int DataAssetTableViewportIndex = 2;
    internal const int LocalizationViewportIndex = 3;
    internal const int PrefabViewportIndex = 4;

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

    internal bool IsPrefabEditing => Documents.IsPrefabActive;

    private SceneObject? EditingParent() =>
        GetSelectedSceneObject() ?? (IsPrefabEditing ? Documents.Prefab!.Current.RootObjects.Single() : null);

    private bool IsPrefabRoot(SceneObject item) => IsPrefabEditing && item.Parent is null;

    private async Task OpenPrefabEditorAsync(string path) =>
        await RunFileOperation(() => OpenPrefabEditorCore(path));

    internal async Task OpenPrefabEditorCore(string path)
    {
        if (RejectWhenPlaying("Open Prefab")) return;
        if (Project is null) throw new InvalidOperationException("Open a project first.");
        if (HasInputErrors && Documents.Asset is null && !IsPrefabEditing)
        {
            SetFileStatus("Fix the Inspector input errors before switching editing documents.", true);
            return;
        }
        if (Documents.Prefab?.Path is { } opened && SamePath(opened, path))
        {
            if (!await ConfirmCloseDataAsset()) return;
            ActivateEditorViewport(PrefabViewportIndex);
            return;
        }

        // Validate a replacement before prompting away the old document. Each document owns its services.
        var (candidate, id) = EditorDocuments.PreparePrefab(path, Project, Components);
        var ownsCandidate = true;
        try
        {
            var restored = candidate.Current;
            if (!await ConfirmDataAssetClose(closeOnConfirm: false)
                || !await ConfirmPrefabEditorClose(closeOnConfirm: false)) return;
            if (Documents.Asset is not null) CloseDataAssetForEdit();
            ClosePrefabEditor();
            Documents.AdoptPrefab(candidate, id);
            ownsCandidate = false;
            _prefabViewState = new EditingViewState
            {
                SelectionId = restored.RootObjects.Single().Id,
                Expanded = [restored.RootObjects.Single().Id],
            };
            ActivateEditorViewport(PrefabViewportIndex);
            UpdatePrefabEditorChrome();
            SetFileStatus($"Editing prefab: {Path.GetFileName(path)}");
        }
        catch
        {
            // A user Inspector getter may fail during activation. Never leave a partially adopted document.
            if (ReferenceEquals(candidate, Documents.Prefab)) ClosePrefabEditor();
            throw;
        }
        finally
        {
            if (ownsCandidate) EditorDocuments.DisposeScene(candidate);
        }
    }

    private async void OnEditorViewportChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_switchingViewport || !ReferenceEquals(e.Source, ViewportTabs)) return;
        var requested = ViewportTabs.SelectedIndex;
        if (requested == _activeViewportIndex) return;
        SetViewportSelection(_activeViewportIndex);
        if (ViewModel.FileBusy || (requested == PrefabViewportIndex && (Documents.Prefab is null || IsPlaying))
            || (requested == DataAssetTableViewportIndex && IsPlaying)
            || (requested == LocalizationViewportIndex && IsPlaying)) return;
        // The table is an additional view, not a document replacement: it never forces the single asset closed.
        if (requested != DataAssetTableViewportIndex && HasInputErrors && Documents.Asset is null)
        {
            SetFileStatus("Fix the Inspector input errors before switching editing documents.", true);
            return;
        }
        if (Documents.Asset is not null && requested != DataAssetTableViewportIndex)
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
        var prefab = index == PrefabViewportIndex && Documents.Prefab is not null;
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
            Documents.ActivatePrefab(prefab);
            var next = prefab ? _prefabViewState : _sceneViewState;
            _scenePan = next.Pan;
            _sceneZoom = next.Zoom;
            _sceneDrawFailures.Clear();
            _hierarchyRefreshing = true;
            HierarchyNode? restoredSelection = null;
            try
            {
                ViewModel.Hierarchy.RebuildRoots(next.Expanded);
                restoredSelection = Documents.Asset is null
                    ? EnumerateHierarchyNodes().FirstOrDefault(node => node.Ref.Id == next.SelectionId) : null;
                SceneObjects.SelectedItems.Clear();
                if (restoredSelection is not null) SceneObjects.SelectedItems.Add(restoredSelection);
            }
            finally { _hierarchyRefreshing = false; CaptureHierarchySelection(); }
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

    private void UpdatePrefabEditorChrome() => ViewModel.RefreshDocumentState();

    internal bool SavePrefabEditor() => ViewModel.SavePrefab();

    private async void OnClosePrefabEditor(object? sender, RoutedEventArgs e) =>
        await RunFileOperation(async () => await ConfirmClosePrefabEditor());

    private Task<bool> ConfirmClosePrefabEditor() => ConfirmPrefabEditorClose(closeOnConfirm: true);

    private async Task<bool> ConfirmPrefabEditorClose(bool closeOnConfirm)
    {
        if (Documents.Prefab is null) return true;
        CancelSceneViewDrag();
        if (closeOnConfirm && IsPrefabEditing && !await ConfirmDataAssetClose(closeOnConfirm: false)) return false;
        if (Documents.Prefab.IsDirty || (IsPrefabEditing && Documents.Asset is null && HasInputErrors))
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
                    new TextBlock { Text = $"{Path.GetFileName(Documents.Prefab.Path)} has unsaved changes. Save?", TextWrapping = TextWrapping.Wrap },
                    buttons,
                },
            };
            var answer = await dialog.ShowDialog<string?>(this);
            if (answer != "discard" && !(answer == "save" && SavePrefabEditor())) return false;
        }
        if (closeOnConfirm)
        {
            if (IsPrefabEditing && Documents.Asset is not null) CloseDataAssetForEdit();
            ClosePrefabEditor();
        }
        return true;
    }

    internal void ClosePrefabEditor()
    {
        var previous = Documents.Prefab;
        if (previous is null) return;
        try
        {
            if (IsPrefabEditing) ActivateEditorViewport(0);
        }
        finally
        {
            try { Documents.ClosePrefab(); }
            finally
            {
                _prefabViewState = new EditingViewState();
                UpdatePrefabEditorChrome();
                QueuePendingUserCodeReload();
            }
        }
    }

}
