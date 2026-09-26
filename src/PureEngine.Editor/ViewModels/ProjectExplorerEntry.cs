namespace PureEngine.Editor;

public enum ProjectExplorerKind
{
    Folder,
    Scene,
    File,
    Component,
    DataAsset,
    Prefab,
}

/// <summary>One row in the Project Explorer right pane. Shows folders, scene files, plain files, and compiled classes in a unified view.</summary>
public sealed record ProjectExplorerEntry(
    ProjectExplorerKind Kind,
    string DisplayName,
    string Detail,
    string ToolTip,
    string? RelativePath,
    string? FullPath,
    Type? ComponentType,
    bool IsStartup)
{
    public string KindLabel => Kind switch
    {
        ProjectExplorerKind.Folder => "Folder",
        ProjectExplorerKind.Scene => "Scene",
        ProjectExplorerKind.DataAsset => "Data Asset",
        ProjectExplorerKind.Prefab => "Prefab",
        ProjectExplorerKind.File => IsImageFile ? "Image" : "File",
        _ => "C#",
    };

    /// <summary>Tile frame. Scene reuses the structural purple accent (same as Startup pill/Engine border/focus ring);
    /// others stay neutral so the grid reads calm.</summary>
    public string TileBorderBrush => IsScene
        ? "#8B7CF6"
        : "#333842";

    /// <summary>Icon selectors. Exactly one is true per row; C#, images, data assets, scenes, and prefabs are told apart from plain files by kind and extension.</summary>
    public bool IsFolder => Kind == ProjectExplorerKind.Folder;

    public bool IsScene => Kind == ProjectExplorerKind.Scene;

    public bool IsDataAsset => Kind == ProjectExplorerKind.DataAsset;

    public bool IsPrefab => Kind == ProjectExplorerKind.Prefab;

    public bool IsImageFile => !IsFolder && !IsScene && !IsDataAsset && !IsPrefab
        && FullPath is not null && ProjectAssets.IsSupportedImage(FullPath);

    public bool IsCSharpFile => (Kind == ProjectExplorerKind.File || Kind == ProjectExplorerKind.Component)
        && ExternalEditor.IsCSharpFile(FullPath);

    public bool IsPlainFile => !IsFolder && !IsScene && !IsDataAsset && !IsPrefab && !IsImageFile && !IsCSharpFile;

    public bool HasDetail => !string.IsNullOrEmpty(Detail);
}

