using System.Numerics;
using SkiaSharp;

namespace PureEngine.Rendering;

public static class RenderingSample
{
    private static readonly byte[] TestImage = CreateImage();
    public static void Build(DrawList list)
    {
        list.Clear();
        var clip = new Vector4(20, 20, 620, 340);
        list.Rectangle(new(600, 320), Matrix3x2.CreateTranslation(20, 20), new(.1f, .13f, .2f, 1), clip);
        list.Rectangle(new(180, 110), Matrix3x2.CreateRotation(.12f) * Matrix3x2.CreateTranslation(65, 80), new(.3f, .5f, 1, .8f), clip);
        list.Image("test", TestImage, new(130, 180), Matrix3x2.CreateScale(.9f) * Matrix3x2.CreateRotation(-.08f) * Matrix3x2.CreateTranslation(95, 95), Vector4.One, clip);
        list.Rectangle(new(140, 65), Matrix3x2.CreateTranslation(160, 200), new(1, .2f, .3f, .55f), clip);
        list.Text("PureEngine Vulkan\n日本語の得点：１２３\n画像・文字、透明度。\n幅で折り返す文章です。\nMissing: \U0001FAE8", 24, 280, 1.35f,
            Matrix3x2.CreateTranslation(320, 60), Vector4.One, new(320, 60, 575, 280));
    }

    private static byte[] CreateImage()
    {
        using var bitmap = new SKBitmap(64, 64, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(245, 220, 160));
        using var paint = new SKPaint { Color = new SKColor(35, 120, 100) };
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
                if ((x + y) % 2 == 0) canvas.DrawRect(x * 8, y * 8, 8, 8, paint);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
