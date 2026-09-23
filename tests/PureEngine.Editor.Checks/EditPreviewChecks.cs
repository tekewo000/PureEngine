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
        var image = new PureEngine.Core.Image { Sprite = new Sprite(imageId), Color = Color.White };
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

        image.Color = new Color(1, 0, 0, 0.5f);
        EditSceneRenderer.Build(draw, scene, images, viewport);
        Check(draw.Vertices[0].Color == new Vector4(1, 0, 0, 0.5f), "Color edits must reach the preview.");

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
        child.Attach(new PureEngine.Core.Image { Sprite = new Sprite(imageId) });
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
        broken.Attach(new PureEngine.Core.Image { Sprite = new Sprite(imageId) });
        diagnostics = EditSceneRenderer.Build(draw, scene, images, viewport);
        Check(diagnostics.Any(entry => entry.ObjectId == broken.Id), "Missing Transform/UiElement must be diagnosed.");

        OrderDraws(images);
        BareParentLayouts(images);
        Console.WriteLine("PASS: edit preview reflects add/remove/placement/sprite/color, Order front-back/same-order/parent layout, diagnoses gaps, and skips lifecycle.");
    }

    private static void BareParentLayouts(IReadOnlyDictionary<Guid, byte[]> images)
    {
        var scene = new Scene();
        var parent = scene.AddEmpty();
        parent.Attach(new Transform { LocalPosition = new Vector3(100, 50, 0) });
        var child = scene.AddEmpty();
        child.SetParent(parent);
        child.Attach(new Transform { LocalPosition = new Vector3(10, 20, 0) });
        child.Attach(new UiElement { Pivot = Vector2.Zero, SizeDelta = new Vector2(40, 30) });
        child.Attach(new PureEngine.Core.Image { Sprite = new Sprite(images.Keys.First()) });
        var viewport = new Vector2(400, 200);
        using var draw = new DrawList();
        Check(EditSceneRenderer.Build(draw, scene, images, viewport).Count == 0
            && draw.Vertices[0].Position == new Vector2(110, 70), "Edit rendering must propagate bare parents without diagnostics.");
        Check(GameSceneRenderer.Build(draw, scene, images, viewport).Count == 0
            && draw.Vertices[0].Position == new Vector2(110, 70), "Game rendering must propagate bare parents without diagnostics.");

        parent.Attach(new UiElement { SizeDelta = new Vector2(-1, 30) });
        Check(!SceneViewMath.TryGetTransformFrame(scene, parent, viewport, out _, out _, out _),
            "An invalid UI parent must not fall back to a bare transform frame.");
        var entry = SceneViewMath.EnumerateLayouts(scene, viewport).Single();
        Check(SceneViewMath.TryGetSceneCorners(entry, out var corners) && corners[0] == new Vector2(10, 20),
            "Children of invalid UI parents must inherit the preceding valid frame.");
        Check(EditSceneRenderer.Build(draw, scene, images, viewport).Count == 1 && draw.Vertices[0].Position == corners[0],
            "Edit rendering and selection must agree below invalid UI parents.");
        Check(GameSceneRenderer.Build(draw, scene, images, viewport).Count == 1 && draw.Vertices[0].Position == corners[0],
            "Game rendering and hit testing must agree below invalid UI parents.");
    }

    private static void OrderDraws(IReadOnlyDictionary<Guid, byte[]> images)
    {
        var viewport = new Vector2(400, 200);
        var scene = new Scene();
        var imageId = images.Keys.First();
        static SceneObject Card(Scene scene, Guid imageId, string name, Vector3 position, Vector4 color, int order)
        {
            var item = scene.AddEmpty();
            item.Rename(name);
            item.Attach(new Transform { LocalPosition = position });
            item.Attach(new UiElement { Pivot = Vector2.Zero, SizeDelta = new Vector2(100, 40) });
            item.Attach(new PureEngine.Core.Image { Sprite = new Sprite(imageId), Color = new(color.X, color.Y, color.Z, color.W), Order = order });
            return item;
        }
        var red = new Vector4(1, 0, 0, 1);
        var blue = new Vector4(0, 0, 1, 1);
        var back = Card(scene, imageId, "Back", new Vector3(10, 20, 0), red, 0);
        var front = Card(scene, imageId, "Front", new Vector3(10, 20, 0), blue, 0);
        Check(front.GetComponent<PureEngine.Core.Image>()!.Order == 0, "Front setup must start with Order 0.");
        using var draw = new DrawList();
        Check(EditSceneRenderer.Build(draw, scene, images, viewport).Count == 0, "Order setup must draw.");
        Check(draw.Vertices.Length == 12, "Order setup must draw two quads.");
        Check(draw.Vertices[0].Color == red && draw.Vertices[6].Color == blue,
            "Same Order must keep sibling order with later siblings in front.");

        // Larger Order comes to front regardless of sibling order.
        back.GetComponent<PureEngine.Core.Image>()!.Order = 5;
        EditSceneRenderer.Build(draw, scene, images, viewport);
        Check(draw.Vertices[0].Color == blue && draw.Vertices[6].Color == red,
            "Larger Order must be drawn later to stay in front.");

        var backImage = back.GetComponent<PureEngine.Core.Image>()!;
        back.Detach(backImage);
        back.Attach(new PreviewLifecycle { Order = int.MinValue });
        back.Attach(backImage);
        Check(EditSceneRenderer.Build(draw, scene, images, viewport).Count == 0
            && draw.Vertices[0].Color == blue && draw.Vertices[6].Color == red,
            "Another renderer attached before Image must not override the displayed Image's Order.");
        draw.Clear();
        var background = new Vector4(0, 1, 0, 1);
        draw.Rectangle(viewport, Matrix3x2.Identity, background, new Vector4(0, 0, viewport.X, viewport.Y));
        Check(EditSceneRenderer.Append(draw, scene, images, viewport, Matrix4x4.Identity).Count == 0
            && draw.Vertices.Length == 18 && draw.Vertices[0].Color == background
            && draw.Vertices[6].Color == blue && draw.Vertices[12].Color == red,
            "Append must preserve the existing background and use the same Image order as Build.");

        // Parent layout is computed before sorting; child uses its own Order.
        var parentScene = new Scene();
        var parent = parentScene.AddEmpty();
        parent.Rename("Parent");
        parent.Attach(new Transform { LocalPosition = new Vector3(10, 20, 0) });
        parent.Attach(new UiElement { Pivot = Vector2.Zero, SizeDelta = new Vector2(100, 40) });
        parent.Attach(new PureEngine.Core.Image { Sprite = new Sprite(images.Keys.First()), Color = new(red.X, red.Y, red.Z, red.W), Order = 0 });
        var child = parentScene.AddEmpty();
        child.Rename("Child");
        child.SetParent(parent);
        child.Attach(new Transform { LocalPosition = new Vector3(5, 5, 0) });
        child.Attach(new UiElement { Pivot = Vector2.Zero, SizeDelta = new Vector2(20, 10) });
        child.Attach(new PureEngine.Core.Image { Sprite = new Sprite(images.Keys.First()), Color = new(blue.X, blue.Y, blue.Z, blue.W), Order = 0 });
        EditSceneRenderer.Build(draw, parentScene, images, viewport);
        var childBefore = draw.Vertices[6].Position;
        Check(childBefore == new Vector2(15, 25), $"Child layout setup failed, got {childBefore}.");
        parent.GetComponent<PureEngine.Core.Image>()!.Order = 10;
        child.GetComponent<PureEngine.Core.Image>()!.Order = -10;
        EditSceneRenderer.Build(draw, parentScene, images, viewport);
        Check(draw.Vertices[0].Color == blue && draw.Vertices[6].Color == red,
            "Child with smaller Order must be drawn before its parent.");
        Check(draw.Vertices[0].Position == childBefore,
            "Changing Order must not change parent-child layout.");

        parent.GetComponent<PureEngine.Core.Image>()!.Sprite = new Sprite(Guid.NewGuid());
        var diagnostics = EditSceneRenderer.Build(draw, parentScene, images, viewport);
        Check(diagnostics.Count == 1 && diagnostics[0].ObjectId == parent.Id
            && draw.Vertices.Length == 6 && draw.Vertices[0].Position == childBefore,
            "A parent's missing image must preserve its child's layout and drawing.");
        parent.GetComponent<Transform>()!.LocalPosition = new Vector3(float.NaN, 20, 0);
        diagnostics = EditSceneRenderer.Build(draw, parentScene, images, viewport);
        var entries = SceneViewMath.EnumerateLayouts(parentScene, viewport);
        Check(diagnostics.Count == 1 && diagnostics[0].ObjectId == parent.Id
            && diagnostics[0].Message.Contains("finite", StringComparison.OrdinalIgnoreCase)
            && draw.Vertices.Length == 6 && draw.Vertices[0].Position == new Vector2(5, 5)
            && entries.Count == 1 && ReferenceEquals(entries[0].Object, child)
            && SceneViewMath.HitTest(entries, viewport, Vector2.Zero, 1f, new Vector2(10, 10),
                item => item.GetComponent<PureEngine.Core.Image>() is { Sprite: not null }) == child,
            "Invalid parent layout must report its cause and use the same fallback for child drawing and selection.");
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

    private sealed class PreviewLifecycle : RendererComponent
    {
        public bool Started;
        public bool Updated;
        [Start] public void OnStart() => Started = true;
        [Update] public void Tick() => Updated = true;
    }
}
