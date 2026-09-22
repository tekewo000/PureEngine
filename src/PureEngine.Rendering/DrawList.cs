using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;

namespace PureEngine.Rendering;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct DrawVertex(Vector2 Position, Vector2 Uv, Vector4 Color, Vector4 Clip);

/// <summary>One renderer-owned, bounded atlas. Entries remain valid until disposal; no hidden eviction.</summary>
public sealed class DrawList : IDisposable
{
    public const int AtlasSize = 2048;
    public const int MaxVertices = 60000;
    private readonly SKBitmap _atlas = new(AtlasSize, AtlasSize, SKColorType.Rgba8888, SKAlphaType.Premul);
    private readonly SKTypeface _typeface;
    private readonly Dictionary<string, SKRect> _entries = [with(StringComparer.Ordinal)];
    private readonly List<DrawVertex> _vertices = [];
    private int _x = 3, _y = 1, _rowHeight = 1;
    private bool _disposed;
    public int Revision { get; private set; } = 1;
    public int EntryCount => _entries.Count;
    public ReadOnlySpan<DrawVertex> Vertices
    {
        get { ObjectDisposedException.ThrowIf(_disposed, this); return CollectionsMarshal.AsSpan(_vertices); }
    }
    internal IntPtr Pixels => _atlas.GetPixels();

    public DrawList()
    {
        try
        {
            _atlas.Erase(SKColors.Transparent);
            _atlas.SetPixel(1, 1, SKColors.White);
            using var stream = typeof(DrawList).Assembly.GetManifestResourceStream("PureEngine.Rendering.Assets.NotoSansCJKjp-Regular.otf")!;
            _typeface = SKTypeface.FromStream(stream) ?? throw new InvalidOperationException("Bundled font could not be loaded.");
        }
        catch { _atlas.Dispose(); throw; }
    }

    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _vertices.Clear();
    }

    public void Rectangle(Vector2 size, Matrix3x2 transform, Vector4 color, Vector4 clip) =>
        Quad(size, new SKRect(1.5f, 1.5f, 1.5f, 1.5f), transform, color, clip);

    public void Image(string key, ReadOnlySpan<byte> encodedImage, Vector2 size, Matrix3x2 transform, Vector4 color, Vector4 clip)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_entries.TryGetValue("image:" + key, out var uv))
        {
            using var data = SKData.CreateCopy(encodedImage);
            using var codec = SKCodec.Create(data) ?? throw new ArgumentException("Invalid image.", nameof(encodedImage));
            if (codec.Info.Width > AtlasSize - 2 || codec.Info.Height > AtlasSize - 2)
                throw new ArgumentException("Image exceeds atlas dimensions.", nameof(encodedImage));
            using var image = SKBitmap.Decode(codec) ?? throw new ArgumentException("Invalid image.", nameof(encodedImage));
            uv = Add("image:" + key, image);
        }
        Quad(size, uv, transform, color, clip);
    }

    /// <summary>Japanese/Latin display text. Width wraps by Unicode text element; input/IME is outside this API.</summary>
    public void Text(string text, float fontSize, float width, float lineSpacing, Matrix3x2 transform, Vector4 color, Vector4 clip)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fontSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lineSpacing);
        if (!float.IsFinite(fontSize + width + lineSpacing) || fontSize > 256 || width > AtlasSize - 2)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(text.Length, 16384);
        using var font = new SKFont(_typeface, fontSize);
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        var y = 0f;
        foreach (var paragraph in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var line = new StringBuilder();
            var elements = System.Globalization.StringInfo.GetTextElementEnumerator(paragraph);
            while (elements.MoveNext())
            {
                var element = elements.GetTextElement();
                // The bundled face's .notdef is not readable; use an explicit replacement box.
                if (element.EnumerateRunes().Any(r => font.GetGlyph((int)r.Value) == 0)) element = "□";
                if (line.Length > 0 && font.MeasureText(line.ToString() + element) > width)
                {
                    Emit(line.ToString());
                    line.Clear();
                    y += fontSize * lineSpacing;
                }
                line.Append(element);
            }
            Emit(line.ToString());
            y += fontSize * lineSpacing;
        }

        void Emit(string line)
        {
            if (line.Length == 0) return;
            var key = FormattableString.Invariant($"text:{fontSize}:{line}");
            if (!_entries.TryGetValue(key, out var uv))
            {
                var bounds = new SKRect();
                font.MeasureText(line, out bounds);
                var w = Math.Max(1, (int)Math.Ceiling(Math.Max(font.MeasureText(line), bounds.Right) + 4));
                var h = Math.Max(1, (int)Math.Ceiling(font.Metrics.Descent - font.Metrics.Ascent + 4));
                using var bitmap = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
                bitmap.Erase(SKColors.Transparent);
                using var canvas = new SKCanvas(bitmap);
                canvas.DrawText(line, 2, 2 - font.Metrics.Ascent, font, paint);
                uv = Add(key, bitmap);
            }
            Quad(new Vector2(uv.Width, uv.Height), uv,
                Matrix3x2.CreateTranslation(-2, y - 2) * transform, color, clip);
        }
    }

    private SKRect Add(string key, SKBitmap bitmap)
    {
        if (bitmap.Width > AtlasSize - 2 || bitmap.Height > AtlasSize - 2 || _entries.Count >= 4096)
            throw new InvalidOperationException("Atlas capacity exceeded. Dispose the draw list to release its cache.");
        var x = _x;
        var y = _y;
        var rowHeight = _rowHeight;
        if (x + bitmap.Width + 1 > AtlasSize) { x = 1; y += rowHeight + 2; rowHeight = 0; }
        if (y + bitmap.Height + 1 > AtlasSize)
            throw new InvalidOperationException("2048×2048 atlas is full. Dispose the draw list to release its cache.");
        using var canvas = new SKCanvas(_atlas);
        canvas.DrawBitmap(bitmap, x, y);
        var rect = new SKRect(x, y, x + bitmap.Width, y + bitmap.Height);
        _entries.Add(key, rect);
        _x = x + bitmap.Width + 2; _y = y; _rowHeight = Math.Max(rowHeight, bitmap.Height);
        Revision++;
        return rect;
    }

    private void Quad(Vector2 size, SKRect uv, Matrix3x2 transform, Vector4 color, Vector4 clip)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!float.IsFinite(size.X + size.Y + transform.M11 + transform.M12 + transform.M21 + transform.M22 + transform.M31 + transform.M32 + color.X + color.Y + color.Z + color.W + clip.X + clip.Y + clip.Z + clip.W))
            throw new ArgumentException("Draw coordinates must be finite.");
        if (size.X <= 0 || size.Y <= 0 || clip.Z <= clip.X || clip.W <= clip.Y) return;
        if (_vertices.Count + 6 > MaxVertices) throw new InvalidOperationException("Draw vertex limit exceeded.");
        color = Vector4.Clamp(color, Vector4.Zero, Vector4.One);
        var a = Vertex(0, 0, uv.Left, uv.Top);
        var b = Vertex(size.X, 0, uv.Right, uv.Top);
        var c = Vertex(size.X, size.Y, uv.Right, uv.Bottom);
        var d = Vertex(0, size.Y, uv.Left, uv.Bottom);
        _vertices.Add(a); _vertices.Add(b); _vertices.Add(c);
        _vertices.Add(a); _vertices.Add(c); _vertices.Add(d);
        DrawVertex Vertex(float x, float y, float u, float v) =>
            new(Vector2.Transform(new Vector2(x, y), transform), new Vector2(u, v) / AtlasSize, color, clip);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _typeface.Dispose();
        _atlas.Dispose();
        _vertices.Clear();
        _entries.Clear();
    }
}

