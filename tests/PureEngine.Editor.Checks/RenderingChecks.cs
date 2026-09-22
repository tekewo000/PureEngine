using System.Numerics;
using PureEngine.Rendering;

internal static class RenderingChecks
{
    public static void Run()
    {
        using var draw = new DrawList();
        var clip = new Vector4(0, 0, 640, 360);
        draw.Rectangle(new(10, 20), Matrix3x2.CreateRotation(MathF.PI / 2) * Matrix3x2.CreateTranslation(30, 40), new(1, 0, 0, .5f), clip);
        draw.Rectangle(new(5, 5), Matrix3x2.Identity, new(0, 1, 0, .25f), clip);
        Require(draw.Vertices.Length == 12, "Ordered batch length");
        Require(Vector2.Distance(draw.Vertices[2].Position, new(10, 50)) < .001f, "Rotation then translation");
        Require(draw.Vertices[0].Color.W == .5f && draw.Vertices[6].Color.Y == 1, "Transparency preserves submission order");
        Require(draw.Vertices[0].Clip == clip, "Clip is in logical target coordinates");
        draw.Clear();
        draw.Text("日本語abc、。\n折り返し\U0001FAE8", 20, 50, 1.5f, Matrix3x2.Identity, Vector4.One, clip);
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
}
