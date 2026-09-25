using System.Numerics;
using PureEngine.Core;
using PureEngine.Rendering;

internal static class RenderingChecks
{
    public static void Run()
    {
        GizmoArrow();
        ImageRenderingChecks.Run();
        using var draw = new DrawList();
        var clip = new Vector4(0, 0, 640, 360);
        draw.Rectangle(new(10, 20), Matrix3x2.CreateRotation(MathF.PI / 2) * Matrix3x2.CreateTranslation(30, 40), new(1, 0, 0, .5f), clip);
        draw.Rectangle(new(5, 5), Matrix3x2.Identity, new(0, 1, 0, .25f), clip);
        Require(draw.Vertices.Length == 12, "Ordered batch length");
        Require(Vector2.Distance(draw.Vertices[2].Position, new(10, 50)) < .001f, "Rotation then translation");
        Require(draw.Vertices[0].Color.W == .5f && draw.Vertices[6].Color.Y == 1, "Transparency preserves submission order");
        Require(draw.Vertices[0].Clip == clip, "Clip is in logical target coordinates");
        draw.Clear();
        // CJK shaping, punctuation, wrapping, and missing-glyph coverage: escapes decode to Japanese at runtime.
        draw.Text("\u65E5\u672C\u8A9Eabc\u3001\u3002\n\u6298\u308A\u8FD4\u3057\U0001FAE8", 20, 50, 1.5f, Matrix3x2.Identity, Vector4.One, clip);
        Require(draw.Vertices.Length >= 24 && draw.EntryCount >= 4, "Japanese, missing glyph, newline and wrapping");
        RenderingSample.Build(draw);
        var revision = draw.Revision;
        var count = draw.Vertices.Length;
        RenderingSample.Build(draw);
        Require(draw.Revision == revision && draw.Vertices.Length == count, "Sample cache does not grow every frame");
        try { draw.Rectangle(new(float.NaN, 1), Matrix3x2.Identity, Vector4.One, clip); throw new InvalidOperationException("Non-finite input accepted"); }
        catch (ArgumentException) { }
        draw.Clear();
        for (var i = 0; i < DrawList.MaxVertices / 6; i++) draw.Rectangle(Vector2.One, Matrix3x2.Identity, Vector4.One, clip);
        try { draw.Rectangle(Vector2.One, Matrix3x2.Identity, Vector4.One, clip); throw new Exception("Unbounded vertex allocation"); }
        catch (InvalidOperationException) { }
        draw.Dispose();
        try { draw.Clear(); throw new Exception("Disposed atlas accepted"); }
        catch (ObjectDisposedException) { }
        Console.WriteLine("Rendering CPU checks passed.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void GizmoArrow()
    {
        var clip = new Vector4(0, 0, 640, 360);
        using var triangle = new DrawList();
        triangle.Triangle(new(10, 10), new(20, 10), new(10, 20), Vector4.One, clip);
        Require(triangle.Vertices.Length == 3, "Triangle must emit one triple.");
        Require(triangle.Vertices[0].Position == new Vector2(10, 10)
            && triangle.Vertices[1].Position == new Vector2(20, 10)
            && triangle.Vertices[2].Position == new Vector2(10, 20), "Triangle must keep its corners.");
        using var degenerate = new DrawList();
        degenerate.Triangle(new(0, 0), new(5, 5), new(10, 10), Vector4.One, clip);
        Require(degenerate.Vertices.Length == 0, "Collinear triangle must draw nothing.");

        using var draw = new DrawList();
        var pivot = new Vector2(100, 100);
        SceneViewOverlay.DrawGizmo(draw, pivot, Vector2.UnitX, Vector2.UnitY, clip);
        Require(draw.Vertices.Length == 24, $"Arrow gizmo must emit shafts, two head triples, and center, got {draw.Vertices.Length}.");
        var tipX = pivot + new Vector2(SceneViewMath.GizmoLength, 0);
        var baseX = pivot + new Vector2(SceneViewMath.GizmoLength - SceneViewMath.GizmoHeadSize, 0);
        var halfHead = SceneViewMath.GizmoHeadSize / 2;
        Require(draw.Vertices[12].Position == tipX, $"X arrow tip must land on the axis, got {draw.Vertices[12].Position}.");
        var xBaseA = draw.Vertices[13].Position;
        var xBaseB = draw.Vertices[14].Position;
        Require((xBaseA == baseX + new Vector2(0, halfHead) && xBaseB == baseX - new Vector2(0, halfHead))
            || (xBaseA == baseX - new Vector2(0, halfHead) && xBaseB == baseX + new Vector2(0, halfHead)),
            $"X arrow base must span the shaft width, got {xBaseA} and {xBaseB}.");
        var tipY = pivot + new Vector2(0, SceneViewMath.GizmoLength);
        Require(draw.Vertices[15].Position == tipY, $"Y arrow tip must land on the axis, got {draw.Vertices[15].Position}.");
    }
}
