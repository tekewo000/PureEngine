using System.Numerics;
using PureEngine.Core;
using SkiaSharp;

namespace PureEngine.Rendering;

/// <summary>A private demo scene, not the Editor's authoring scene. Exercises ordinary UI components.</summary>
public sealed class ImageRenderingSample
{
    private static readonly Guid ImageId = new("ba6104ce-7234-4673-8bd0-cee380b505de");
    private static readonly Dictionary<Guid, byte[]> Images = new() { [ImageId] = CreateImage() };
    private readonly Scene _scene = new();
    private readonly SceneObject _root;

    public ImageRenderingSample()
    {
        _root = Add(null, "UI group", new Transform { LocalPosition = new(20, 60, 0) },
            new UiElement { Pivot = Vector2.Zero, AnchorMax = new(1, 0), SizeDelta = new(-40, 270) }, null);
        Add(_root, "Whole sprite", new Transform(),
            new UiElement { Pivot = Vector2.Zero, SizeDelta = new(160) }, new Sprite(ImageId));
        Add(_root, "Cropped sprite", new Transform { LocalPosition = new(190, 0, 0) },
            new UiElement { Pivot = Vector2.Zero, SizeDelta = new(160) }, new Sprite(ImageId, (64, 0, 64, 64)));
        var tinted = Add(_root, "Tint and alpha", new Transform { LocalPosition = new(245, 65, 0) },
            new UiElement { Pivot = Vector2.Zero, SizeDelta = new(96) }, new Sprite(ImageId, (0, 0, 64, 64)));
        tinted.GetComponent<global::Image>()!.Color = new(1, .35f, .35f, .6f);
        var parent = Add(_root, "Rotated parent", new Transform
        {
            LocalPosition = new(410, 15, 0), LocalScale = new(.85f, .85f, 1),
            LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -.16f)
        }, new UiElement { Pivot = Vector2.Zero, SizeDelta = new(150) }, new Sprite(ImageId));
        Add(parent, "Centered child", new Transform { LocalPosition = new(20, 0, 0) },
            new UiElement { AnchorMin = new(.5f), AnchorMax = new(.5f), SizeDelta = new(80) },
            new Sprite(ImageId, (64, 0, 64, 64)));
        Add(_root, "Bottom-right anchor", new Transform { LocalPosition = new(-16, -16, 0) },
            new UiElement { AnchorMin = Vector2.One, AnchorMax = Vector2.One, Pivot = Vector2.One, SizeDelta = new(64) },
            new Sprite(ImageId, (64, 0, 64, 64)));
        Add(_root, "Horizontal stretch", new Transform { LocalPosition = new(0, -16, 0) },
            new UiElement { AnchorMin = new(0, 1), AnchorMax = Vector2.One, Pivot = new(0, 1), SizeDelta = new(-180, 24) },
            new Sprite(ImageId, (0, 0, 64, 64)));
        Add(_root, "Null sprite", new Transform(), new UiElement(), null);
    }

    public void Build(DrawList draw, Vector2 viewportSize)
    {
        draw.Clear();
        // Tiny viewports can occur while dragging splitters; resume normally when space returns.
        if (viewportSize.X < 240 || viewportSize.Y < 100) return;
        var clip = new Vector4(0, 0, viewportSize.X, viewportSize.Y);
        draw.Rectangle(new(viewportSize.X - 40, 270), Matrix3x2.CreateTranslation(20, 60), new(.1f, .13f, .2f, 1), clip);
        Draw(_root, viewportSize, Matrix4x4.Identity);
        draw.Text("Image + Sprite + UiLayout", 24, 700, 1.2f, Matrix3x2.CreateTranslation(20, 15), Vector4.One, clip);
        draw.Text("全体", 18, 160, 1.2f, Matrix3x2.CreateTranslation(20, 230), Vector4.One, clip);
        draw.Text("切り出し・色・透明度", 18, 230, 1.2f, Matrix3x2.CreateTranslation(210, 230), Vector4.One, clip);
        draw.Text("親の回転・子の追従", 18, 230, 1.2f, Matrix3x2.CreateTranslation(430, 230), Vector4.One, clip);

        void Draw(SceneObject item, Vector2 parentSize, Matrix4x4 parentWorld)
        {
            var (size, world) = UiImageRenderer.Draw(draw, item, parentSize, parentWorld, Images, clip);
            foreach (var child in item.Children) Draw(child, size, world);
        }
    }

    private SceneObject Add(SceneObject? parent, string name, Transform transform, UiElement element, Sprite? sprite)
    {
        var item = _scene.AddEmpty();
        item.Rename(name);
        item.SetParent(parent);
        item.Attach(transform);
        item.Attach(element);
        item.Attach(new global::Image { Sprite = sprite });
        return item;
    }

    private static byte[] CreateImage()
    {
        using var bitmap = new SKBitmap(128, 64, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(245, 220, 160));
        using var paint = new SKPaint { Color = new SKColor(35, 120, 100) };
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
                if ((x + y) % 2 == 0) canvas.DrawRect(x * 8, y * 8, 8, 8, paint);
        paint.Color = new SKColor(40, 145, 230);
        canvas.DrawRect(64, 0, 64, 64, paint);
        paint.Color = SKColors.White;
        canvas.DrawRect(72, 8, 12, 12, paint);
        paint.Color = new SKColor(255, 190, 40);
        canvas.DrawRect(96, 32, 24, 24, paint);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
