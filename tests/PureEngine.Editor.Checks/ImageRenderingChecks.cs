using System.Numerics;
using PureEngine.Core;
using PureEngine.Rendering;
using SkiaSharp;

internal static class ImageRenderingChecks
{
    public static void Run()
    {
        var id = Guid.NewGuid();
        using var bitmap = new SKBitmap(64, 32);
        bitmap.Erase(SKColors.Blue);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var images = new Dictionary<Guid, byte[]> { [id] = encoded.ToArray() };
        var clip = new Vector4(0, 0, 400, 200);
        var item = new SceneObject("Image check");
        item.Attach(new Transform());
        item.Attach(new UiElement { SizeDelta = new(100, 40), AnchorMin = new(.5f), AnchorMax = new(.5f) });
        var image = new global::Image { Sprite = new Sprite(id), Color = new(.4f, .6f, 1, .5f) };
        item.Attach(image);
        Check(image.Order == 0, "New Images must start with Order 0.");
        using var draw = new DrawList();
        var (Size, World) = UiImageRenderer.Draw(draw, item, new(400, 200), Matrix4x4.Identity, images, clip);
        Check(Size == new Vector2(100, 40) && draw.Vertices.Length == 6, "Component produces one quad");
        Check(draw.Vertices[0].Position == new Vector2(150, 80) && draw.Vertices[2].Position == new Vector2(250, 120),
            "Image uses UiLayout geometry");
        Check(draw.Vertices[0].Color == image.Color && draw.Vertices[0].Clip == clip, "Color and alpha reach the batch");
        Near((draw.Vertices[2].Uv - draw.Vertices[0].Uv) * DrawList.AtlasSize, new(64, 32), "Whole image UV extent");
        using (var orderDraw = new DrawList())
        {
            image.Order = 7;
            var (orderSize, _) = UiImageRenderer.Draw(orderDraw, item, new(400, 200), Matrix4x4.Identity, images, clip);
            Check(orderSize == new Vector2(100, 40) && orderDraw.Vertices[0].Position == new Vector2(150, 80),
                "Single-object Order must not change its own layout.");
            image.Order = 0;
        }
        image.Sprite = new Sprite(id, (32, 0, 32, 16));
        UiImageRenderer.Draw(draw, item, new(400, 200), Matrix4x4.Identity, images, clip);
        Check(draw.Vertices.Length == 12 && draw.EntryCount == 2, "Crop of the same image has its own atlas entry and preserves draw order");
        Near((draw.Vertices[8].Uv - draw.Vertices[6].Uv) * DrawList.AtlasSize, new(32, 16), "Crop UV extent");
        var revision = draw.Revision;
        UiImageRenderer.Draw(draw, item, new(800, 400), Matrix4x4.Identity, images, clip);
        Check(draw.Revision == revision && draw.EntryCount == 2, "Resizing reuses the crop texture");
        Check(draw.Vertices[12].Position == new Vector2(350, 180), "Anchor is recalculated for new parent size");

        draw.Clear();
        image.Sprite = null;
        UiImageRenderer.Draw(draw, item, new(400, 200), Matrix4x4.Identity, new Dictionary<Guid, byte[]>(), clip);
        Check(draw.Vertices.IsEmpty && draw.EntryCount == 2, "Null sprite does not resolve an image or draw");
        image.Sprite = new Sprite(id);
        item.GetComponent<Transform>()!.LocalScale = Vector3.Zero;
        UiImageRenderer.Draw(draw, item, new(400, 200), Matrix4x4.Identity, new Dictionary<Guid, byte[]>(), clip);
        Check(draw.Vertices.IsEmpty, "Zero scale does not draw or resolve an image");
        item.GetComponent<Transform>()!.LocalScale = Vector3.One;
        Reject<InvalidOperationException>(() => UiImageRenderer.Draw(draw, item, new(400, 200), Matrix4x4.Identity, new Dictionary<Guid, byte[]>(), clip));
        Reject<InvalidOperationException>(() => UiImageRenderer.Draw(draw, new SceneObject("Missing components"), new(400, 200), Matrix4x4.Identity, images, clip));
        image.Sprite = new Sprite(id, (60, 0, 16, 16));
        Reject<ArgumentException>(() => UiImageRenderer.Draw(draw, item, new(400, 200), Matrix4x4.Identity, images, clip));
        Check(draw.Vertices.IsEmpty && draw.EntryCount == 2, "Invalid crop publishes neither vertices nor atlas entries");

        var sample = new ImageRenderingSample();
        sample.Build(draw, new(800, 450));
        revision = draw.Revision;
        var count = draw.Vertices.Length;
        sample.Build(draw, new(1200, 700));
        Check(draw.Vertices.Length == count && draw.Revision == revision, "Demo resizes without growing its atlas");
        sample.Build(draw, new(10, 10));
        Check(draw.Vertices.IsEmpty, "Tiny demo viewport is temporarily empty");
        sample.Build(draw, new(800, 450));
        Check(draw.Vertices.Length == count && draw.Revision == revision, "Demo resumes after tiny viewport");
        Console.WriteLine("PASS: Image component layout, sprite crop UVs, color/alpha, Order default/single layout, null, failures and resize cache reuse.");
    }

    private static void Near(Vector2 actual, Vector2 expected, string message) => Check(Vector2.Distance(actual, expected) < .001f, message);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
