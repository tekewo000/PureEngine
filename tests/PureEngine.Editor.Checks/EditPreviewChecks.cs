using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using PureEngine.Core;
using PureEngine.Rendering;
using SkiaSharp;

internal static class EditPreviewChecks
{
    public static void Run()
    {
        var imageId = Guid.NewGuid();
        var images = new Dictionary<Guid, byte[]> { [imageId] = CreatePng() };
        var scene = new Scene();
        var item = scene.AddEmpty();
        item.Rename("Card");
        var transform = new Transform { LocalPosition = new Vector3(10, 20, 0) };
        var element = new UiElement { Pivot = Vector2.Zero, SizeDelta = new Vector2(100, 40) };
        var image = new global::Image { Sprite = new Sprite(imageId), Color = Vector4.One };
        item.Attach(transform);
        item.Attach(element);
        item.Attach(image);
        using var draw = new DrawList();
        var viewport = new Vector2(400, 200);
        var diagnostics = EditSceneRenderer.Build(draw, scene, images, viewport);
        Check(diagnostics.Count == 0 && draw.Vertices.Length == 6, "Edit preview must draw the editing scene.");
        var first = draw.Vertices[0].Position;
        Check(first == new Vector2(10, 20), $"Placement did not reflect Inspector values, got {first}.");

        // Refresh keeps image IDs stable. Reset the atlas before rebuilding from new source bytes.
        images[imageId] = CreatePng(SKColors.Blue);
        var revision = draw.Revision;
        draw.ResetAtlas();
        Check(draw.Vertices.IsEmpty && draw.EntryCount == 0 && draw.Revision > revision, "Invalidation must clear old entries and request a GPU upload.");
        EditSceneRenderer.Build(draw, scene, images, viewport);
        Check(ReadFirstPixel(draw) == SKColors.Blue,
            "Refreshing an existing image ID must replace white pixels with blue, not reuse stale pixels.");

        transform.LocalPosition = new Vector3(30, 40, 0);
        EditSceneRenderer.Build(draw, scene, images, viewport);
        Check(draw.Vertices[0].Position == new Vector2(30, 40), "Position edits must reach the preview.");

        element.SizeDelta = new Vector2(80, 20);
        EditSceneRenderer.Build(draw, scene, images, viewport);
        Check(draw.Vertices[2].Position - draw.Vertices[0].Position == new Vector2(80, 20),
            "Size edits must reach the preview.");

        image.Color = new Vector4(1, 0, 0, 0.5f);
        EditSceneRenderer.Build(draw, scene, images, viewport);
        Check(draw.Vertices[0].Color == image.Color, "Color edits must reach the preview.");

        image.Sprite = null;
        diagnostics = EditSceneRenderer.Build(draw, scene, images, viewport);
        Check(draw.Vertices.IsEmpty && diagnostics.Count == 0, "None sprite must draw nothing without a diagnostic.");

        image.Sprite = new Sprite(Guid.NewGuid());
        diagnostics = EditSceneRenderer.Build(draw, scene, images, viewport);
        Check(draw.Vertices.IsEmpty && diagnostics.Count == 1 && diagnostics[0].ObjectId == item.Id,
            "Missing images must be skipped with a diagnostic and keep their ID.");

        image.Sprite = new Sprite(imageId);
        var child = scene.AddEmpty();
        child.Rename("Child");
        child.SetParent(item);
        child.Attach(new Transform());
        child.Attach(new UiElement { Pivot = Vector2.Zero, SizeDelta = new Vector2(20, 10) });
        child.Attach(new global::Image { Sprite = new Sprite(imageId) });
        EditSceneRenderer.Build(draw, scene, images, viewport);
        Check(draw.Vertices.Length == 12, "Children must follow their parent layout in sibling order.");

        scene.Remove(child);
        EditSceneRenderer.Build(draw, scene, images, viewport);
        Check(draw.Vertices.Length == 6, "Removal must leave the preview.");

        var lifecycle = new PreviewLifecycle();
        var lifecycleItem = scene.AddEmpty();
        lifecycleItem.Rename("Lifecycle");
        lifecycleItem.Attach(lifecycle);
        EditSceneRenderer.Build(draw, scene, images, viewport);
        Check(!lifecycle.Started && !lifecycle.Updated, "Preview must not call Start/Update.");

        var broken = scene.AddEmpty();
        broken.Rename("Broken");
        broken.Attach(new global::Image { Sprite = new Sprite(imageId) });
        diagnostics = EditSceneRenderer.Build(draw, scene, images, viewport);
        Check(diagnostics.Any(entry => entry.ObjectId == broken.Id), "Missing Transform/UiElement must be diagnosed.");
        Console.WriteLine("PASS: edit preview reflects add/remove/placement/sprite/color, diagnoses gaps, and skips lifecycle.");
    }

    private static byte[] CreatePng(SKColor? color = null)
    {
        using var bitmap = new SKBitmap(16, 16);
        bitmap.Erase(color ?? SKColors.White);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    internal static SKColor ReadFirstPixel(DrawList draw)
    {
        var uv = draw.Vertices[0].Uv * DrawList.AtlasSize;
        var pixels = (IntPtr)typeof(DrawList).GetProperty("Pixels", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(draw)!;
        var offset = (((int)uv.Y + 1) * DrawList.AtlasSize + (int)uv.X + 1) * 4;
        return new SKColor(Marshal.ReadByte(pixels, offset), Marshal.ReadByte(pixels, offset + 1),
            Marshal.ReadByte(pixels, offset + 2), Marshal.ReadByte(pixels, offset + 3));
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class PreviewLifecycle
    {
        public bool Started;
        public bool Updated;
        [Start] public void OnStart() => Started = true;
        [Update] public void Tick() => Updated = true;
    }
}
