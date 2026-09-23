using System.Collections.ObjectModel;
using System.ComponentModel;
using PureEngine.Core;

namespace PureEngine.Editor;

/// <summary>Stuffsペインのツリー表示用ノード。Coreの親子を読み取り専用で写す。</summary>
public sealed class HierarchyNode(SceneObject item) : INotifyPropertyChanged
{
    private bool _isExpanded;

    public SceneObject Ref { get; } = item ?? throw new ArgumentNullException(nameof(item));

    public ObservableCollection<HierarchyNode> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Stuffsペインのドロップ位置。UnityのHierarchy相当: 子にするか前後に挿入するか。</summary>
public enum HierarchyDropPosition
{
    AsChild,
    Before,
    After,
}

/// <summary>Stuffsペインの親子付け・並べ替えの実行本体。UI非依存で検証できる。</summary>
public static class HierarchyDrop
{
    /// <summary>ドラッグ元がドロップ先へ移動できるかを判定する。UIのカーソル表示用。</summary>
    public static bool CanDrop(Scene scene, Guid draggedId, Guid? targetId)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var dragged = Find(scene, draggedId);
        if (dragged is null) return false;
        if (targetId is null) return true;
        var target = Find(scene, targetId.Value);
        if (target is null || ReferenceEquals(dragged, target)) return false;
        return !IsDescendant(target, dragged);
    }

    /// <summary>ドラッグ元を指定位置へ移動する。拒否時は例外を投げ、ツリーを変更しない。</summary>
    public static void Execute(Scene scene, Guid draggedId, Guid? targetId, HierarchyDropPosition position)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var dragged = Find(scene, draggedId)
            ?? throw new InvalidOperationException("Dragged object is not part of this scene.");
        if (targetId is null)
        {
            dragged.SetParent(null);
            return;
        }
        var target = Find(scene, targetId.Value)
            ?? throw new InvalidOperationException("Drop target is not part of this scene.");
        if (ReferenceEquals(dragged, target))
            throw new InvalidOperationException("Cannot drop an object onto itself.");
        if (IsDescendant(target, dragged))
            throw new InvalidOperationException("Cannot drop an object into its own descendant.");
        if (position == HierarchyDropPosition.AsChild)
        {
            dragged.SetParent(target);
            return;
        }
        var siblings = target.Parent is null ? scene.RootObjects : target.Parent.Children;
        var targetIndex = IndexOfReference(siblings, target);
        if (targetIndex < 0)
            throw new InvalidOperationException("Drop target sibling order is broken.");
        var desired = position == HierarchyDropPosition.After ? targetIndex + 1 : targetIndex;
        if (target.Parent is null)
        {
            var roots = scene.RootObjects;
            var current = IndexOfReference(roots, dragged);
            if (current >= 0 && current < desired) desired--;
            dragged.SetParent(null);
            scene.SetRootSiblingIndex(dragged, desired);
            return;
        }
        if (ReferenceEquals(dragged.Parent, target.Parent))
        {
            var current = IndexOfReference(siblings, dragged);
            if (current >= 0 && current < desired) desired--;
        }
        dragged.SetParent(target.Parent);
        dragged.SetSiblingIndex(desired);
    }

    private static SceneObject? Find(Scene scene, Guid id)
    {
        foreach (var item in scene.Objects)
            if (item.Id == id) return item;
        return null;
    }

    private static bool IsDescendant(SceneObject item, SceneObject ancestor)
    {
        for (var current = item.Parent; current is not null; current = current.Parent)
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private static int IndexOfReference(IReadOnlyList<SceneObject> items, SceneObject item)
    {
        for (var i = 0; i < items.Count; i++)
            if (ReferenceEquals(items[i], item)) return i;
        return -1;
    }
}

/// <summary>Sceneの親子からTreeView用のノード列を組み立てる。</summary>
public static class StuffsHierarchy
{
    public static ObservableCollection<HierarchyNode> Build(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ObservableCollection<HierarchyNode> roots = [];
        foreach (var item in scene.RootObjects)
            roots.Add(BuildNode(item));
        return roots;
    }

    private static HierarchyNode BuildNode(SceneObject item)
    {
        var node = new HierarchyNode(item);
        foreach (var child in item.Children)
            node.Children.Add(BuildNode(child));
        return node;
    }
}
