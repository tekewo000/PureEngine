using PureEngine.Core;

static class ParentChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static T Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    public static void Run()
    {
        InitialState();
        AttachAndDetach();
        Reparent();
        SameParentIsNoOp();
        SelfParentIsRejected();
        DirectCycleIsRejected();
        IndirectCycleIsRejected();
        NonAncestorIsAllowed();
        FailedAttachLeavesTreeUnchanged();
        Console.WriteLine("PASS: parent attach/detach, reparent, no-op, self/direct/indirect cycle rejection, and tree integrity.");
    }

    private static void InitialState()
    {
        var item = new SceneObject("Item");
        Check(item.Parent is null, "New object must have no parent.");
        Check(item.Children.Count == 0, "New object must have no children.");
    }

    private static void AttachAndDetach()
    {
        var parent = new SceneObject("Parent");
        var child = new SceneObject("Child");

        child.SetParent(parent);
        Check(child.Parent == parent, "SetParent must update Parent.");
        Check(parent.Children.Count == 1 && ReferenceEquals(parent.Children[0], child),
            "SetParent must register the child on the parent.");
        Check(child.Children.Count == 0, "Attach must not create grandchildren.");

        child.SetParent(null);
        Check(child.Parent is null, "SetParent(null) must clear Parent.");
        Check(parent.Children.Count == 0, "SetParent(null) must unregister the child.");
    }

    private static void Reparent()
    {
        var oldParent = new SceneObject("Old");
        var newParent = new SceneObject("New");
        var child = new SceneObject("Child");
        var sibling = new SceneObject("Sibling");

        child.SetParent(oldParent);
        sibling.SetParent(oldParent);
        Check(oldParent.Children.Count == 2, "Setup must have two children.");

        child.SetParent(newParent);
        Check(child.Parent == newParent, "Reparent must update Parent.");
        Check(oldParent.Children.Count == 1 && ReferenceEquals(oldParent.Children[0], sibling),
            "Reparent must remove the child from the old parent only.");
        Check(newParent.Children.Count == 1 && ReferenceEquals(newParent.Children[0], child),
            "Reparent must add the child to the new parent.");
    }

    private static void SameParentIsNoOp()
    {
        var parent = new SceneObject("Parent");
        var child = new SceneObject("Child");
        child.SetParent(parent);

        child.SetParent(parent);
        Check(child.Parent == parent && parent.Children.Count == 1,
            "Setting the same parent must be a no-op without duplicating Children.");

        var root = new SceneObject("Root");
        root.SetParent(null);
        Check(root.Parent is null && root.Children.Count == 0,
            "SetParent(null) on a root must be a no-op.");
    }

    private static void SelfParentIsRejected()
    {
        var item = new SceneObject("Item");
        Reject<InvalidOperationException>(() => item.SetParent(item));
        Check(item.Parent is null && item.Children.Count == 0,
            "Rejected self-parent must leave the tree unchanged.");
    }

    private static void DirectCycleIsRejected()
    {
        var parent = new SceneObject("Parent");
        var child = new SceneObject("Child");
        child.SetParent(parent);

        Reject<InvalidOperationException>(() => parent.SetParent(child));
        Check(parent.Parent is null && ReferenceEquals(child.Parent, parent),
            "Rejected direct cycle must leave both links unchanged.");
        Check(parent.Children.Count == 1 && child.Children.Count == 0,
            "Rejected direct cycle must leave Children unchanged.");
    }

    private static void IndirectCycleIsRejected()
    {
        var root = new SceneObject("Root");
        var middle = new SceneObject("Middle");
        var leaf = new SceneObject("Leaf");
        middle.SetParent(root);
        leaf.SetParent(middle);

        Reject<InvalidOperationException>(() => root.SetParent(leaf));
        Reject<InvalidOperationException>(() => root.SetParent(middle));
        Reject<InvalidOperationException>(() => middle.SetParent(leaf));

        Check(root.Parent is null && ReferenceEquals(middle.Parent, root) && ReferenceEquals(leaf.Parent, middle),
            "Rejected indirect cycle must leave the chain unchanged.");
        Check(root.Children.Count == 1 && middle.Children.Count == 1 && leaf.Children.Count == 0,
            "Rejected indirect cycle must leave Children unchanged.");
    }

    private static void NonAncestorIsAllowed()
    {
        // Siblings and "uncle" relations share ancestors but are not ancestors themselves: allowed.
        var root = new SceneObject("Root");
        var left = new SceneObject("Left");
        var right = new SceneObject("Right");
        var leaf = new SceneObject("Leaf");
        left.SetParent(root);
        right.SetParent(root);
        leaf.SetParent(left);

        leaf.SetParent(right);
        Check(ReferenceEquals(leaf.Parent, right), "Moving to a sibling subtree must be allowed.");
        Check(left.Children.Count == 0 && right.Children.Count == 1,
            "Sibling move must transfer the child.");

        right.SetParent(left);
        Check(ReferenceEquals(right.Parent, left) && ReferenceEquals(leaf.Parent, right),
            "Nesting one sibling under another must be allowed.");
        Check(ReferenceEquals(left.Parent, root) && root.Children.Count == 1,
            "Sibling nesting must keep the root link.");
    }

    private static void FailedAttachLeavesTreeUnchanged()
    {
        var root = new SceneObject("Root");
        var child = new SceneObject("Child");
        var grandchild = new SceneObject("Grandchild");
        child.SetParent(root);
        grandchild.SetParent(child);

        var error = Reject<InvalidOperationException>(() => root.SetParent(grandchild));
        Check(error.Message.Contains("cycle", StringComparison.OrdinalIgnoreCase),
            "Cycle rejection must explain the reason.");
        Check(root.Parent is null && ReferenceEquals(child.Parent, root) && ReferenceEquals(grandchild.Parent, child),
            "Failed attach must not mutate any Parent link.");
        Check(root.Children.Count == 1 && child.Children.Count == 1 && grandchild.Children.Count == 0,
            "Failed attach must not mutate any Children list.");
    }
}
