using Avalonia.Controls;
using Avalonia.Platform.Storage;
using PureEngine.Core;

namespace PureEngine.Editor;

public partial class MainWindow
{
    private ProjectAssets _projectAssets = ProjectAssets.Scan(Path.GetTempPath());
    private Dictionary<Guid, byte[]> _previewImages = [];

    /// <summary>Scans the project asset index and refreshes the image bytes used for rendering. Creates no files.</summary>
    internal void RefreshProjectAssets()
    {
        if (_project is null)
        {
            _projectAssets = ProjectAssets.Scan(Path.GetTempPath());
            _previewImages = [];
            _sceneViewport?.InvalidateImageCache();
            _gameViewport?.InvalidateImageCache();
            return;
        }
        _projectAssets = ProjectAssets.Scan(_project.RootDirectory);
        _previewImages = _projectAssets.LoadImageBytes();
        _sceneViewport?.InvalidateImageCache();
        _gameViewport?.InvalidateImageCache();
        foreach (var diagnostic in _projectAssets.Diagnostics)
            Log.Engine.Warning(diagnostic);
        if (_projectAssets.Diagnostics.Count > 0)
            SetFileStatus($"{_projectAssets.Diagnostics.Count} asset issue(s). See Console.", true);
    }

    internal IReadOnlyList<ProjectAssets.AssetEntry> AssetImageEntries() =>
        [.. _projectAssets.Images.Values.OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)];

    internal bool IsAssetMissing(Guid imageId) => !_projectAssets.Images.ContainsKey(imageId);

    internal string AssetDisplayName(Guid imageId) =>
        _projectAssets.Images.TryGetValue(imageId, out var entry) ? entry.RelativePath : $"{imageId:D} (missing)";

    private async void OnImportImage(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await ImportImageAsync();

    private async Task ImportImageAsync()
    {
        if (_project is null) return;
        if (RejectWhenPlaying("Import")) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Image",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg"] },
            ],
        });
        if (files.Count == 0) return;
        var source = files[0].TryGetLocalPath() ?? throw new IOException("Select a local image.");
        await RunFileOperation(async () =>
        {
            var entry = ProjectAssets.ImportImage(_project.RootDirectory, source);
            RefreshProjectAssets();
            RefreshProjectExplorer();
            RefreshComponents();
            SetFileStatus($"Imported image: {entry.RelativePath}");
            await Task.CompletedTask;
        });
    }
}
