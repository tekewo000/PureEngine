namespace PureEngine.Editor;

/// <summary>Composition root for editing documents and pane presentation state.</summary>
public sealed class EditorViewModel
{
    public EditorViewModel()
    {
        Hierarchy = new HierarchyViewModel(Documents);
        Inspector = new InspectorViewModel(Documents);
        DataAsset = new DataAssetViewModel(Documents, Inspector);
        DataAssetTable = new DataAssetTableViewModel(Documents.Table, Inspector);
    }

    public EditorDocuments Documents { get; } = new();
    public HierarchyViewModel Hierarchy { get; }
    public InspectorViewModel Inspector { get; }
    public DataAssetViewModel DataAsset { get; }
    public DataAssetTableViewModel DataAssetTable { get; }
    public ProjectPaneViewModel Project { get; } = new();
    public ConsoleViewModel Console { get; } = new();
}
