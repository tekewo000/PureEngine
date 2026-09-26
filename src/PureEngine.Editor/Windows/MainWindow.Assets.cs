using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using PureEngine.Core;

namespace PureEngine.Editor;

public partial class MainWindow
{
    internal static readonly DataFormat<string> ImageIdFormat =
        DataFormat.CreateInProcessFormat<string>("PureEngine.ImageId");
    private ProjectExplorerEntry? _pressedImage;
    private ProjectAssets _projectAssets = ProjectAssets.Scan(Path.GetTempPath());
    private Dictionary<Guid, byte[]> _previewImages = [];

    /// <summary>Scans the project asset index and refreshes the image bytes used for rendering. Creates no files.</summary>
    internal void RefreshProjectAssets()
    {
        if (Project is null)
        {
            _projectAssets = ProjectAssets.Scan(Path.GetTempPath());
            _previewImages = [];
            _sceneViewport?.InvalidateImageCache();
            _gameViewport?.InvalidateImageCache();
            RefreshPreviewLanguages();
            return;
        }
        _projectAssets = ProjectAssets.Scan(Project.RootDirectory);
        _previewImages = _projectAssets.LoadImageBytes();
        _sceneViewport?.InvalidateImageCache();
        _gameViewport?.InvalidateImageCache();
        foreach (var diagnostic in _projectAssets.Diagnostics)
            Log.Engine.Warning(diagnostic);
        if (_projectAssets.Diagnostics.Count > 0)
            SetFileStatus($"{_projectAssets.Diagnostics.Count} asset issue(s). See Console.", true);
        // The Sprite Inspector resolves IDs against this index; keep the displayed selection and warnings consistent.
        if (GetSelectedSceneObject() is { } selected)
            RefreshUiWarnings(selected);
        RefreshPreviewLanguages();
    }

    internal IReadOnlyList<ProjectAssets.AssetEntry> AssetImageEntries() =>
        [.. _projectAssets.Images.Values.OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)];

    private static bool IsProjectImage(ProjectExplorerEntry entry) =>
        entry.Kind == ProjectExplorerKind.File
        && entry.FullPath is not null
        && ProjectAssets.IsSupportedImage(entry.FullPath);

    private Guid? DroppedImageId(DragEventArgs e) =>
        Guid.TryParse(e.DataTransfer.TryGetValue(ImageIdFormat), out var id)
        && _projectAssets.Images.ContainsKey(id) ? id : null;

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal bool IsAssetMissing(Guid imageId) => !_projectAssets.Images.ContainsKey(imageId);

    internal string AssetDisplayName(Guid imageId) =>
        _projectAssets.Images.TryGetValue(imageId, out var entry) ? entry.RelativePath : $"{imageId:D} (missing)";

    private async void OnImportImage(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await ImportImageAsync();

    private async Task ImportImageAsync()
    {
        if (Project is null) return;
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
            var entry = ProjectAssets.ImportImage(Project.RootDirectory, source);
            RefreshProjectAssets();
            RefreshProjectExplorer();
            RefreshComponents();
            SetFileStatus($"Imported image: {entry.RelativePath}");
            await Task.CompletedTask;
        });
    }
}
