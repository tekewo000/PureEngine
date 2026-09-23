namespace PureEngine.Core;

/// <summary>An RGBA color with float channels. RGBA is the canonical store; HSV and hex forms are derived on demand.</summary>
public readonly record struct Color(float R, float G, float B, float A)
{
    /// <summary>Opaque white.</summary>
    public static Color White => new(1f, 1f, 1f, 1f);

    /// <summary>Opaque black.</summary>
    public static Color Black => new(0f, 0f, 0f, 1f);

    /// <summary>Fully transparent black.</summary>
    public static Color Transparent => new(0f, 0f, 0f, 0f);

    /// <summary>Creates a color from finite HSV channels and alpha. Hue wraps into [0, 360); saturation, value, and alpha clamp into [0, 1].</summary>
    /// <exception cref="ArgumentOutOfRangeException">A channel is not finite.</exception>
    public static Color FromHsv(float h, float s, float v, float a = 1f)
    {
        RequireFinite(h, nameof(h));
        var hue = h % 360f;
        if (hue < 0f) hue += 360f;
        if (hue >= 360f) hue = 0f;
        s = ClampChannel(s, nameof(s));
        v = ClampChannel(v, nameof(v));
        a = ClampChannel(a, nameof(a));
        var sector = (int)(hue / 60f);
        var f = hue / 60f - sector;
        var p = v * (1f - s);
        var q = v * (1f - f * s);
        var t = v * (1f - (1f - f) * s);
        var (r, g, b) = sector switch
        {
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            5 => (v, p, q),
            _ => (v, t, p),
        };
        return new(r, g, b, a);
    }

    /// <summary>Parses "#RRGGBB" (opaque) or "#RRGGBBAA". The leading "#" is optional; digits are case-insensitive.</summary>
    /// <exception cref="FormatException">The text is not a 6- or 8-digit hex color.</exception>
    public static Color FromHex(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        var digits = hex.StartsWith('#') ? hex[1..] : hex;
        if (digits.Length is not (6 or 8) || !digits.All(char.IsAsciiHexDigit))
            throw new FormatException($"Invalid hex color: '{hex}'. Use '#RRGGBB' or '#RRGGBBAA'.");
        var r = Convert.ToByte(digits[..2], 16) / 255f;
        var g = Convert.ToByte(digits[2..4], 16) / 255f;
        var b = Convert.ToByte(digits[4..6], 16) / 255f;
        var a = digits.Length == 8 ? Convert.ToByte(digits[6..8], 16) / 255f : 1f;
        return new(r, g, b, a);
    }

    /// <summary>Derives HSV from RGB clamped into [0, 1]: hue in [0, 360), saturation and value in [0, 1]. Alpha stays on this color.</summary>
    /// <exception cref="ArgumentOutOfRangeException">An RGB channel is not finite.</exception>
    public (float H, float S, float V) ToHsv()
    {
        var r = ClampChannel(R, nameof(R));
        var g = ClampChannel(G, nameof(G));
        var b = ClampChannel(B, nameof(B));
        var max = MathF.Max(r, MathF.Max(g, b));
        var min = MathF.Min(r, MathF.Min(g, b));
        var diff = max - min;
        var s = max == 0f ? 0f : diff / max;
        if (diff == 0f) return (0f, s, max);
        var h = max == r ? 60f * ((g - b) / diff)
            : max == g ? 60f * ((b - r) / diff) + 120f
            : 60f * ((r - g) / diff) + 240f;
        if (h < 0f) h += 360f;
        if (h >= 360f) h = 0f;
        return (h, s, max);
    }

    /// <summary>Derives "#RRGGBBAA". Channels clamp into [0, 1] before quantization.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A channel is not finite.</exception>
    public string ToHex()
    {
        var r = (byte)MathF.Round(ClampChannel(R, nameof(R)) * 255f);
        var g = (byte)MathF.Round(ClampChannel(G, nameof(G)) * 255f);
        var b = (byte)MathF.Round(ClampChannel(B, nameof(B)) * 255f);
        var a = (byte)MathF.Round(ClampChannel(A, nameof(A)) * 255f);
        return $"#{r:X2}{g:X2}{b:X2}{a:X2}";
    }

    private static float ClampChannel(float value, string parameterName)
    {
        RequireFinite(value, parameterName);
        return Math.Clamp(value, 0f, 1f);
    }

    private static void RequireFinite(float value, string parameterName)
    {
        if (!float.IsFinite(value))
            throw new ArgumentOutOfRangeException(parameterName, value, "A finite color channel is required.");
    }
}
