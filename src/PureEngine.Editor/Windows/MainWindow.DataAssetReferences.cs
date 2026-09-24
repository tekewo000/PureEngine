using Avalonia.Controls;
using Avalonia.Input;
using PureEngine.Core;

namespace PureEngine.Editor;

public partial class MainWindow
{
    private static readonly DataFormat<string> DataAssetIdFormat =
        DataFormat.CreateInProcessFormat<string>("PureEngine.DataAssetId");
    private ProjectExplorerEntry? _pressedDataAsset;

    private void RefreshReferenceAssets()
    {
        if (IsPlaying) return;
        if (BuildProjectAssetStore(_components.Registry) is { } assets)
            _editScene.Current.DataAssets.Refresh(assets);
    }

    private object? DroppedDataAsset(DragEventArgs e, Type expectedType)
    {
        if (!DataAssetStore.IsAssetType(expectedType)
            || !Guid.TryParse(e.DataTransfer.TryGetValue(DataAssetIdFormat), out var id)) return null;
        var assets = _editScene.Current.DataAssets;
        return assets.TryGet<object>(id, out var value) && expectedType.IsInstanceOfType(value) ? value : null;
    }

    private async void OnAssetReleased(object? sender, PointerReleasedEventArgs e)
    {
        var entry = _pressedDataAsset;
        var prefab = _pressedPrefab;
        _pressedDataAsset = null;
        _pressedPrefab = null;
        _assetPress = null;
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if (entry is not null)
        {
            if (ReferenceEquals(ProjectFiles.SelectedItem, entry))
                await OpenDataAssetForEdit(entry.FullPath!);
            else
                ProjectFiles.SelectedItem = entry;
            return;
        }
        // Prefab clicks only select; placement stays on the menu, double-click, or drag-drop.
        if (prefab is not null)
            ProjectFiles.SelectedItem = prefab;
    }
}
