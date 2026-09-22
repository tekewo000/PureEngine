using System.Numerics;
using PureEngine.Core;

static class TransformChecks
{
    private const float Tolerance = 1e-5f;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static bool Near(float a, float b) => Math.Abs(a - b) <= Tolerance;

    private static bool MatrixNear(Matrix4x4 a, Matrix4x4 b) =>
        Near(a.M11, b.M11) && Near(a.M12, b.M12) && Near(a.M13, b.M13) && Near(a.M14, b.M14) &&
        Near(a.M21, b.M21) && Near(a.M22, b.M22) && Near(a.M23, b.M23) && Near(a.M24, b.M24) &&
        Near(a.M31, b.M31) && Near(a.M32, b.M32) && Near(a.M33, b.M33) && Near(a.M34, b.M34) &&
        Near(a.M41, b.M41) && Near(a.M42, b.M42) && Near(a.M43, b.M43) && Near(a.M44, b.M44);

    private static void CheckMatrix(Matrix4x4 actual, Matrix4x4 expected, string message) => Check(MatrixNear(actual, expected), $"{message} Expected M41-43=({expected.M41},{expected.M42},{expected.M43}).");

    public static void Run()
    {
        DefaultLocalIsIdentity();
        TranslationOnly();
        ScaleOnly();
        RotationOnly();
        CombinedIsScaleRotationTranslation();
        WorldWithoutTransformIsIdentity();
        WorldSingleMatchesLocal();
        WorldComposesChildParentOrder();
        EmptyMiddleParentIsSkipped();
        ParentScaleAffectsChild();
        MultipleEmptyParentsAreSkipped();
        ReparentUpdatesWorld();
        Console.WriteLine("PASS: Transform LocalMatrix (S*R*T) and SceneObject WorldMatrix (self*parent*...).");
    }

    private static void DefaultLocalIsIdentity()
    {
        var t = new Transform();
        CheckMatrix(t.LocalMatrix, Matrix4x4.Identity, "Default LocalMatrix must be Identity.");
    }

    private static void TranslationOnly()
    {
        var t = new Transform { LocalPosition = new Vector3(1, 2, 3) };
        var expected = Matrix4x4.CreateTranslation(new Vector3(1, 2, 3));
        CheckMatrix(t.LocalMatrix, expected, "Translation-only LocalMatrix mismatch.");
    }

    private static void ScaleOnly()
    {
        var t = new Transform { LocalScale = new Vector3(2, 3, 4) };
        var expected = Matrix4x4.CreateScale(new Vector3(2, 3, 4));
        CheckMatrix(t.LocalMatrix, expected, "Scale-only LocalMatrix mismatch.");
    }

    private static void RotationOnly()
    {
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var t = new Transform { LocalRotation = q };
        var expected = Matrix4x4.CreateFromQuaternion(q);
        CheckMatrix(t.LocalMatrix, expected, "Rotation-only LocalMatrix mismatch.");
    }

    private static void CombinedIsScaleRotationTranslation()
    {
        var t = new Transform
        {
            LocalPosition = new Vector3(1, 2, 3),
            LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4),
            LocalScale = new Vector3(2, 2, 2),
        };
        var expected = Matrix4x4.CreateScale(t.LocalScale)
            * Matrix4x4.CreateFromQuaternion(t.LocalRotation)
            * Matrix4x4.CreateTranslation(t.LocalPosition);
        CheckMatrix(t.LocalMatrix, expected, "Combined LocalMatrix must be Scale*Rotation*Translation.");
    }

    private static void WorldWithoutTransformIsIdentity()
    {
        var obj = new SceneObject("NoTransform");
        CheckMatrix(obj.WorldMatrix, Matrix4x4.Identity, "WorldMatrix without Transform must be Identity.");
    }

    private static void WorldSingleMatchesLocal()
    {
        var obj = new SceneObject("Single");
        var t = new Transform { LocalPosition = new Vector3(1, 2, 3) };
        obj.Attach(t);
        CheckMatrix(obj.WorldMatrix, t.LocalMatrix, "Single WorldMatrix must equal LocalMatrix.");
    }

    private static void WorldComposesChildParentOrder()
    {
        // 親がZ90度回転、子がX+1移動の場合:
        // World = ChildLocal * ParentLocal なので原点は (1,0,0) を90度回転した (0,1,0) になる。
        // 逆順 (Parent*Child) だと (1,0,0) のままなので順序を検出できる。
        var parent = new SceneObject("Parent");
        parent.Attach(new Transform
        {
            LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2),
        });
        var child = new SceneObject("Child");
        child.Attach(new Transform { LocalPosition = new Vector3(1, 0, 0) });
        child.SetParent(parent);

        var childLocal = child.GetComponent<Transform>()!.LocalMatrix;
        var parentLocal = parent.GetComponent<Transform>()!.LocalMatrix;
        CheckMatrix(child.WorldMatrix, childLocal * parentLocal, "WorldMatrix must be Child*Parent.");
        Check(Near(child.WorldMatrix.M41, 0f) && Near(child.WorldMatrix.M42, 1f) && Near(child.WorldMatrix.M43, 0f),
            $"Child*Parent order must rotate the offset to (0,1,0), got ({child.WorldMatrix.M41},{child.WorldMatrix.M42},{child.WorldMatrix.M43}).");
    }

    private static void EmptyMiddleParentIsSkipped()
    {
        var root = new SceneObject("Root");
        root.Attach(new Transform { LocalPosition = new Vector3(10, 0, 0) });
        var middle = new SceneObject("Middle"); // Transformなし = Identity扱い
        middle.SetParent(root);
        var leaf = new SceneObject("Leaf");
        leaf.Attach(new Transform { LocalPosition = new Vector3(0, 5, 0) });
        leaf.SetParent(middle);

        var expected = leaf.GetComponent<Transform>()!.LocalMatrix
            * root.GetComponent<Transform>()!.LocalMatrix;
        CheckMatrix(leaf.WorldMatrix, expected, "Empty middle parent must be skipped as Identity.");
        Check(Near(leaf.WorldMatrix.M41, 10f) && Near(leaf.WorldMatrix.M42, 5f),
            $"Skipped chain must give (10,5,0), got ({leaf.WorldMatrix.M41},{leaf.WorldMatrix.M42},{leaf.WorldMatrix.M43}).");
    }

    private static void ParentScaleAffectsChild()
    {
        // 親が2倍拡縮、子がX+1移動の場合:
        // World = ChildLocal * ParentLocal なので子の原点は (2,0,0) になる。
        var parent = new SceneObject("Parent");
        parent.Attach(new Transform { LocalScale = new Vector3(2, 2, 2) });
        var child = new SceneObject("Child");
        child.Attach(new Transform { LocalPosition = new Vector3(1, 0, 0) });
        child.SetParent(parent);

        var expected = child.GetComponent<Transform>()!.LocalMatrix
            * parent.GetComponent<Transform>()!.LocalMatrix;
        CheckMatrix(child.WorldMatrix, expected, "Parent scale must affect child WorldMatrix.");
        Check(Near(child.WorldMatrix.M41, 2f) && Near(child.WorldMatrix.M42, 0f) && Near(child.WorldMatrix.M43, 0f),
            $"Scaled parent must move child origin to (2,0,0), got ({child.WorldMatrix.M41},{child.WorldMatrix.M42},{child.WorldMatrix.M43}).");
    }

    private static void MultipleEmptyParentsAreSkipped()
    {
        var root = new SceneObject("Root");
        root.Attach(new Transform { LocalPosition = new Vector3(10, 0, 0) });
        var empty1 = new SceneObject("Empty1");
        empty1.SetParent(root);
        var empty2 = new SceneObject("Empty2");
        empty2.SetParent(empty1);
        var leaf = new SceneObject("Leaf");
        leaf.Attach(new Transform { LocalPosition = new Vector3(0, 5, 0) });
        leaf.SetParent(empty2);

        var expected = leaf.GetComponent<Transform>()!.LocalMatrix
            * root.GetComponent<Transform>()!.LocalMatrix;
        CheckMatrix(leaf.WorldMatrix, expected, "Multiple empty parents must be skipped as Identity.");
    }

    private static void ReparentUpdatesWorld()
    {
        var parentA = new SceneObject("ParentA");
        parentA.Attach(new Transform { LocalPosition = new Vector3(10, 0, 0) });
        var parentB = new SceneObject("ParentB");
        parentB.Attach(new Transform { LocalPosition = new Vector3(0, 20, 0) });
        var child = new SceneObject("Child");
        child.Attach(new Transform { LocalPosition = new Vector3(1, 1, 1) });

        child.SetParent(parentA);
        var underA = child.GetComponent<Transform>()!.LocalMatrix
            * parentA.GetComponent<Transform>()!.LocalMatrix;
        CheckMatrix(child.WorldMatrix, underA, "WorldMatrix under parentA mismatch.");

        child.SetParent(parentB);
        var underB = child.GetComponent<Transform>()!.LocalMatrix
            * parentB.GetComponent<Transform>()!.LocalMatrix;
        CheckMatrix(child.WorldMatrix, underB, "WorldMatrix must follow reparent to parentB.");

        child.SetParent(null);
        CheckMatrix(child.WorldMatrix, child.GetComponent<Transform>()!.LocalMatrix,
            "WorldMatrix after detach must equal LocalMatrix.");
    }
}
