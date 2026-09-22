using System.Numerics;
using PureEngine.Core;

internal static class UiLayoutChecks
{
    public static void Run()
    {
        var transform = new Transform();
        var element = new UiElement { SizeDelta = new(100, 40), AnchorMin = new(.5f), AnchorMax = new(.5f) };
        var parentSize = new Vector2(400, 200);
        var (Size, World) = UiLayout.Calculate(parentSize, Matrix4x4.Identity, transform, element);
        Near(new(Size, 0), new(100, 40, 0), "Unscaled size");
        Near(Vector3.Transform(Vector3.Zero, World), new(150, 80, 0), "Centered rectangle");
        element.AnchorMin = element.AnchorMax = element.Pivot = Vector2.One;
        var corner = UiLayout.Calculate(parentSize, Matrix4x4.Identity, transform, element);
        Near(Vector3.Transform(Vector3.Zero, corner.World), new(300, 160, 0), "Bottom-right fixed");

        element.AnchorMin = Vector2.Zero;
        element.AnchorMax = Vector2.One;
        element.Pivot = new(.5f);
        element.SizeDelta = new(-20);
        var stretch = UiLayout.Calculate(parentSize, Matrix4x4.Identity, transform, element);
        Near(new(stretch.Size, 0), new(380, 180, 0), "Full stretch size");
        Near(Vector3.Transform(Vector3.Zero, stretch.World), new(10, 10, 0), "Full stretch margins");
        element.AnchorMax = new(1, 0);
        element.SizeDelta = new(-20, 40);
        var horizontal = UiLayout.Calculate(parentSize, Matrix4x4.Identity, transform, element);
        Near(new(horizontal.Size, 0), new(380, 40, 0), "Horizontal stretch");

        element.AnchorMin = element.AnchorMax = new(.5f);
        element.SizeDelta = new(100, 40);
        transform.LocalPosition = new(10, 20, 3);
        transform.LocalScale = new(2, 3, 1);
        transform.LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var before = transform.LocalMatrix;
        var rotated = UiLayout.Calculate(parentSize, Matrix4x4.Identity, transform, element);
        Near(Vector3.Transform(new(50, 20, 0), rotated.World), new(210, 120, 3), "Pivot stays at anchor plus offset");
        Near(Vector3.Transform(Vector3.Zero, rotated.World), new(270, 20, 3), "Scale then rotate around pivot");
        var child = UiLayout.Calculate(rotated.Size, rotated.World, new Transform(),
            new UiElement { AnchorMin = Vector2.One, AnchorMax = Vector2.One, Pivot = Vector2.Zero, SizeDelta = new(10) });
        Near(Vector3.Transform(Vector3.Zero, child.World), new(150, 220, 3), "Child follows parent's UI rectangle exactly once");
        if (transform.LocalMatrix != before || element.SizeDelta != new Vector2(100, 40) || element.Pivot != new Vector2(.5f))
            throw new InvalidOperationException("Layout modified authoring values.");

        var outside = UiLayout.Calculate(parentSize, Matrix4x4.Identity, new Transform(),
            new UiElement { AnchorMin = new(2), AnchorMax = new(2), Pivot = new(-1), SizeDelta = new(10) });
        Near(Vector3.Transform(Vector3.Zero, outside.World), new(810, 410, 0), "Out-of-range anchors and pivot are preserved");
        var zero = UiLayout.Calculate(Vector2.Zero, Matrix4x4.Identity, new Transform { LocalScale = Vector3.Zero },
            new UiElement { SizeDelta = Vector2.Zero });
        if (zero.Size != Vector2.Zero || Matrix4x4.Invert(zero.World, out _))
            throw new InvalidOperationException("Zero size/scale should remain a valid degenerate layout.");

        Reject(() => UiLayout.Calculate(parentSize, Matrix4x4.Identity, null!, element));
        Reject(() => UiLayout.Calculate(parentSize, Matrix4x4.Identity, transform, null!));
        Reject(() => UiLayout.Calculate(new(-1, 1), Matrix4x4.Identity, transform, element));
        Reject(() => UiLayout.Calculate(parentSize, Matrix4x4.Identity, transform, new UiElement { SizeDelta = new(-1) }));
        Reject(() => UiLayout.Calculate(parentSize, Matrix4x4.Identity, transform, new UiElement { AnchorMin = Vector2.One }));
        foreach (var invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            Reject(() => UiLayout.Calculate(new(invalid, 1), Matrix4x4.Identity, transform, element));
            Reject(() => UiLayout.Calculate(parentSize, Matrix4x4.Identity, transform, new UiElement { Pivot = new(invalid) }));
            Reject(() => UiLayout.Calculate(parentSize, Matrix4x4.CreateTranslation(invalid, 0, 0), transform, element));
            Reject(() => UiLayout.Calculate(parentSize, Matrix4x4.Identity, new Transform { LocalPosition = new(invalid) }, element));
        }
        Reject(() => UiLayout.Calculate(new(float.MaxValue), Matrix4x4.Identity, transform,
            new UiElement { AnchorMax = new(2) }));
        Reject(() => UiLayout.Calculate(parentSize, Matrix4x4.Identity, new Transform { LocalScale = new(float.MaxValue) }, element));
        Console.WriteLine("PASS: UI layout fixed/stretch anchors, pivot, transform, hierarchy, immutability and invalid inputs.");
    }

    private static void Near(Vector3 actual, Vector3 expected, string message)
    {
        if (!float.IsFinite(actual.X) || !float.IsFinite(actual.Y) || !float.IsFinite(actual.Z)
            || Vector3.Distance(actual, expected) > .001f)
            throw new InvalidOperationException($"{message}: expected {expected}, got {actual}.");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Invalid UI input was accepted.");
    }
}
