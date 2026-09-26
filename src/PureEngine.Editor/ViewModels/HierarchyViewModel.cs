using System.Collections.ObjectModel;
using PureEngine.Core;

namespace PureEngine.Editor;

/// <summary>Scene hierarchy, expansion, and ordered multi-selection without a TreeView.</summary>
public sealed class HierarchyViewModel(EditorDocuments documents) : EditorObservable
{
    public ObservableCollection<HierarchyNode> Roots { get; private set; } = [];
    public IReadOnlyList<HierarchyNode> SelectedNodes { get; private set; } = [];
    public HierarchyNode? Primary { get; private set; }

    public IEnumerable<HierarchyNode> EnumerateNodes(IEnumerable<HierarchyNode>? roots = null)
    {
        var stack = new Stack<HierarchyNode>(roots ?? Roots);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            foreach (var child in node.Children) stack.Push(child);
        }
    }

    public void RebuildRoots(HashSet<Guid>? expanded = null)
    {
        var selectedIds = SelectedNodes.Select(node => node.Ref.Id).ToHashSet();
        var primaryId = Primary?.Ref.Id;
        expanded ??= [.. EnumerateNodes().Where(node => node.IsExpanded).Select(node => node.Ref.Id)];
        Roots = StuffsHierarchy.Build(documents.Current.Current);
        if (documents.IsPrefabActive)
            foreach (var root in Roots) root.IsPrefab = true;
        foreach (var node in EnumerateNodes()) node.IsExpanded = expanded.Contains(node.Ref.Id);
        Select(EnumerateNodes().Where(node => selectedIds.Contains(node.Ref.Id)),
            EnumerateNodes().FirstOrDefault(node => node.Ref.Id == primaryId));
        Changed(nameof(Roots));
    }

    public void Select(IEnumerable<HierarchyNode> nodes, HierarchyNode? primary)
    {
        SelectedNodes = [.. nodes.Where(node => documents.Current.Current.Objects.Contains(node.Ref)).Distinct()];
        Primary = primary is not null && SelectedNodes.Contains(primary) ? primary
            : SelectedNodes.Count == 0 ? null : SelectedNodes[0];
        Changed(nameof(SelectedNodes));
        Changed(nameof(Primary));
    }

    public IReadOnlyList<SceneObject> SelectedObjects()
    {
        var selected = SelectedNodes.Select(node => node.Ref).ToHashSet(ReferenceEqualityComparer.Instance);
        return [.. EnumerateInDisplayOrder().Where(selected.Contains)];
    }

    public SceneObject? Find(Guid id) => documents.Current.Current.Objects.FirstOrDefault(item => item.Id == id);

    public IEnumerable<SceneObject> EnumerateInDisplayOrder() => documents.Current.Current.RootObjects.SelectMany(Subtree);

    private static IEnumerable<SceneObject> Subtree(SceneObject root)
    {
        yield return root;
        foreach (var child in root.Children)
            foreach (var item in Subtree(child)) yield return item;
    }

    public void ExpandAncestors(SceneObject item)
    {
        for (var ancestor = item.Parent; ancestor is not null; ancestor = ancestor.Parent)
            foreach (var node in EnumerateNodes())
                if (ReferenceEquals(node.Ref, ancestor)) node.IsExpanded = true;
    }
}
