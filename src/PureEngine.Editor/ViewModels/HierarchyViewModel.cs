using System.Collections.ObjectModel;
using PureEngine.Core;

namespace PureEngine.Editor;

/// <summary>Scene hierarchy, expansion, and ordered multi-selection without a TreeView.</summary>
public sealed class HierarchyViewModel(EditorDocuments documents, InspectorViewModel inspector) : EditorObservable
{
    public event Action? BeforeMutation;
    public event Action? SceneChanged;
    public event Action<string, bool>? StatusChanged;
    public bool IsMutating { get; private set; }
    public ObservableCollection<HierarchyNode> Roots { get; private set; } = [];
    public IReadOnlyList<HierarchyNode> SelectedNodes { get; private set; } = [];
    public HierarchyNode? Primary { get; private set; }

    public void Clear()
    {
        Roots = [];
        SelectedNodes = [];
        Primary = null;
        Changed(nameof(Roots));
        Changed(nameof(SelectedNodes));
        Changed(nameof(Primary));
    }

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

    private bool RejectEditing(string action)
    {
        if (!inspector.IsReadOnly) return false;
        StatusChanged?.Invoke($"Cannot {action} while playing. Stop first.", true);
        return true;
    }

    private bool IsPrefabRoot(SceneObject item) => documents.IsPrefabActive && item.Parent is null;

    private SceneObject? EditingParent() => Primary?.Ref
        ?? (documents.IsPrefabActive ? documents.Current.Current.RootObjects.Single() : null);

    private void CommitSelection(IReadOnlyList<SceneObject> selected, Guid? expandId = null)
    {
        IsMutating = true;
        try
        {
            documents.Current.MarkChanged();
            RebuildRoots();
            var nodes = EnumerateNodes().ToDictionary(node => node.Ref.Id);
            if (expandId is { } id && nodes.TryGetValue(id, out var parent)) parent.IsExpanded = true;
            var selection = selected.Where(item => nodes.ContainsKey(item.Id)).Select(item => nodes[item.Id]).ToList();
            Select(selection, selection.Count == 0 ? null : selection[0]);
            inspector.Select(Primary?.Ref);
            SceneChanged?.Invoke();
        }
        finally { IsMutating = false; }
    }

    public void AddEmpty()
    {
        if (RejectEditing("Add")) return;
        BeforeMutation?.Invoke();
        var parent = EditingParent();
        var item = documents.Current.Current.AddEmpty();
        if (parent is not null) item.SetParent(parent);
        CommitSelection([item], parent?.Id);
    }

    public void AddUiObject(string baseName, Type[] componentTypes, ProjectComponents components)
    {
        if (RejectEditing("Add")) return;
        BeforeMutation?.Invoke();
        var parent = EditingParent();
        var item = documents.Current.Current.AddNamed(baseName);
        try
        {
            foreach (var type in componentTypes)
                if (!components.TryAttach(item, type, documents.Current.Services.Factory))
                    throw new InvalidOperationException($"Could not attach {type.Name}.");
            if (parent is not null) item.SetParent(parent);
        }
        catch (Exception error)
        {
            documents.Current.Current.Remove(item);
            try { ComponentAssets.DisposeComponents(item.Components); }
            catch (Exception cleanupError) { Log.Engine.Error(cleanupError); }
            StatusChanged?.Invoke($"Could not create UI: {error.GetBaseException().Message}", true);
            return;
        }
        CommitSelection([item], parent?.Id);
    }
    public void DeleteSelected()
    {
        if (RejectEditing("Delete")) return;
        var selected = SelectedObjects();
        if (selected.Count == 0) return;
        var targets = TopLevelSelection(selected);
        List<SceneObject> deletable = [];
        var skippedPrefabRoot = false;
        foreach (var item in targets)
        {
            if (!documents.Current.Current.Objects.Contains(item)) continue;
            if (IsPrefabRoot(item)) { skippedPrefabRoot = true; continue; }
            deletable.Add(item);
        }
        if (deletable.Count == 0)
        {
            StatusChanged?.Invoke("The prefab root cannot be deleted. Edit its name or components instead.", true);
            return;
        }
        BeforeMutation?.Invoke();
        var next = FindNextAfterDelete(deletable[0], deletable);
        List<object> doomed = [];
        foreach (var item in deletable)
        {
            doomed.AddRange(item.Components);
            var stack = new Stack<SceneObject>(item.Children);
            while (stack.Count > 0)
            {
                var descendant = stack.Pop();
                doomed.AddRange(descendant.Components);
                foreach (var child in descendant.Children)
                    stack.Push(child);
            }
        }
        foreach (var item in deletable)
            documents.Current.Current.Remove(item);
        try { ComponentAssets.DisposeComponents(doomed); }
        catch (Exception error) { StatusChanged?.Invoke(error.ToString(), true); }
        CommitSelection(next is null ? [] : [next]);
        if (skippedPrefabRoot)
            StatusChanged?.Invoke($"Deleted {deletable.Count} object(s). The prefab root cannot be deleted.", true);
    }

    public List<SceneObject> DuplicateSelected(ComponentRegistry registry)
    {
        if (RejectEditing("Duplicate")) return [];
        var selected = SelectedObjects();
        if (selected.Count == 0) return [];
        var targets = TopLevelSelection(selected);
        List<SceneObject> sources = [];
        var skippedPrefabRoot = false;
        foreach (var item in targets)
        {
            if (!documents.Current.Current.Objects.Contains(item)) continue;
            if (IsPrefabRoot(item)) { skippedPrefabRoot = true; continue; }
            sources.Add(item);
        }
        if (sources.Count == 0)
        {
            StatusChanged?.Invoke("The prefab root cannot be duplicated. Select a non-root object instead.", true);
            return [];
        }
        BeforeMutation?.Invoke();
        var serializer = new PrefabSerializer(registry);
        List<SceneObject> copies = [];
        foreach (var source in sources)
        {
            PrefabDocument document;
            try { document = serializer.Capture(documents.Current.Current, source); }
            catch (Exception error)
            {
                StatusChanged?.Invoke($"Cannot duplicate {source.Name}: {error.GetBaseException().Message}", true);
                continue;
            }
            document.Id = source.PrefabId ?? Guid.NewGuid();
            SceneObject copy;
            try { copy = serializer.Instantiate(documents.Current.Current, document, source.Parent, documents.Current.Services.Factory); }
            catch (Exception error)
            {
                StatusChanged?.Invoke($"Cannot duplicate {source.Name}: {error.GetBaseException().Message}", true);
                continue;
            }
            copy.PrefabId = source.PrefabId;
            MoveCopyAfterSource(source, copy);
            copies.Add(copy);
        }
        if (copies.Count == 0) return [];
        CommitSelection(copies);
        if (skippedPrefabRoot)
            StatusChanged?.Invoke($"Duplicated {copies.Count} object(s). The prefab root cannot be duplicated.", true);
        else
            StatusChanged?.Invoke($"Duplicated {copies.Count} object(s).", false);
        return copies;
    }

    private List<SceneObject> TopLevelSelection(IReadOnlyList<SceneObject> selected)
    {
        var selectedSet = new HashSet<SceneObject>(selected, ReferenceEqualityComparer.Instance);
        var order = new Dictionary<SceneObject, int>(ReferenceEqualityComparer.Instance);
        var index = 0;
        foreach (var item in EnumerateInDisplayOrder())
            order[item] = index++;
        List<SceneObject> tops = [];
        foreach (var item in selected)
        {
            var inside = false;
            for (var ancestor = item.Parent; ancestor is not null; ancestor = ancestor.Parent)
                if (selectedSet.Contains(ancestor)) { inside = true; break; }
            if (!inside) tops.Add(item);
        }
        tops.Sort((left, right) =>
            (order.TryGetValue(left, out var leftIndex) ? leftIndex : int.MaxValue).CompareTo(
                order.TryGetValue(right, out var rightIndex) ? rightIndex : int.MaxValue));
        return tops;
    }

    private SceneObject? FindNextAfterDelete(SceneObject first, List<SceneObject> deletable)
    {
        var condemned = new HashSet<SceneObject>(ReferenceEqualityComparer.Instance);
        foreach (var item in deletable)
        {
            condemned.Add(item);
            var stack = new Stack<SceneObject>(item.Children);
            while (stack.Count > 0)
            {
                var descendant = stack.Pop();
                if (!condemned.Add(descendant)) continue;
                foreach (var child in descendant.Children)
                    stack.Push(child);
            }
        }
        var siblings = first.Parent is null ? documents.Current.Current.RootObjects : first.Parent.Children;
        var siblingIndex = IndexOfSceneObject(siblings, first);
        if (siblingIndex >= 0)
        {
            for (var i = siblingIndex + 1; i < siblings.Count; i++)
                if (!condemned.Contains(siblings[i])) return siblings[i];
            for (var i = siblingIndex - 1; i >= 0; i--)
                if (!condemned.Contains(siblings[i])) return siblings[i];
        }
        return first.Parent;
    }

    private void MoveCopyAfterSource(SceneObject source, SceneObject copy)
    {
        if (source.Parent is null)
        {
            var roots = documents.Current.Current.RootObjects;
            var sourceIndex = IndexOfSceneObject(roots, source);
            if (sourceIndex >= 0) documents.Current.Current.SetRootSiblingIndex(copy, sourceIndex + 1);
            return;
        }
        var siblings = source.Parent.Children;
        var desired = IndexOfSceneObject(siblings, source) + 1;
        if (desired >= 0 && desired < siblings.Count) copy.SetSiblingIndex(desired);
    }

    private static int IndexOfSceneObject(IReadOnlyList<SceneObject> items, SceneObject item)
    {
        for (var i = 0; i < items.Count; i++)
            if (ReferenceEquals(items[i], item)) return i;
        return -1;
    }

}
