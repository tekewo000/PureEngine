using PureEngine.Core;

internal static class ColorChecks
{
    public static void Run()
    {
        var red = new Color(1f, 0f, 0f, 1f);
        Check(red.R == 1f && red.G == 0f && red.B == 0f && red.A == 1f, "RGBA construction");
        Check(Color.White == new Color(1f, 1f, 1f, 1f), "White preset");
        Check(Color.Black == new Color(0f, 0f, 0f, 1f), "Black preset");
        Check(Color.Transparent == new Color(0f, 0f, 0f, 0f), "Transparent preset");

        (float H, float S, float V)[] cases = [(0f, 1f, 1f), (120f, 1f, 1f), (240f, 1f, 1f), (60f, 0.5f, 0.75f), (359f, 0.25f, 0.5f)];
        foreach (var (h, s, v) in cases)
        {
            var (rh, rs, rv) = Color.FromHsv(h, s, v).ToHsv();
            Check(Close(rh, h) && Close(rs, s) && Close(rv, v), $"HSV round trip h={h} s={s} v={v}");
        }
        Check(Color.FromHsv(0f, 0f, 0.5f) == new Color(0.5f, 0.5f, 0.5f, 1f), "Achromatic HSV has no hue");
        Check(Color.FromHsv(360f, 1f, 1f) == Color.FromHsv(0f, 1f, 1f), "Hue wraps at 360");

        Check(Color.FromHsv(-120f, 1f, 1f) == Color.FromHsv(240f, 1f, 1f), "Negative hue wraps");
        Check(Color.FromHsv(-float.Epsilon, 1f, 1f) == red, "Rounded hue boundary wraps to zero");
        Check(new Color(1f, 0f, float.Epsilon, 1f).ToHsv().H < 360f, "Derived hue stays below 360");
        Check(Color.FromHsv(0f, 2f, 2f, 2f) == red, "HSV and alpha clamp at the upper bound");
        Check(Color.FromHsv(0f, -1f, -1f, -1f) == Color.Transparent, "HSV and alpha clamp at the lower bound");
        Check(Color.FromHsv(0f, 1f, 1f, 0.25f).A == 0.25f, "HSV preserves normalized alpha");
        var outside = new Color(2f, -1f, 0f, 2f);
        Check(outside.ToHsv() == (0f, 1f, 1f), "HSV derives from clamped RGB");
        Check(outside.ToHex() == "#FF0000FF", "Hex derives from clamped RGBA");
        Check(outside == new Color(2f, -1f, 0f, 2f), "Conversion does not change stored channels");
        foreach (var invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            RejectNonFinite(() => Color.FromHsv(invalid, 1f, 1f));
            RejectNonFinite(() => Color.FromHsv(0f, invalid, 1f));
            RejectNonFinite(() => Color.FromHsv(0f, 1f, invalid));
            RejectNonFinite(() => Color.FromHsv(0f, 1f, 1f, invalid));
            RejectNonFinite(() => new Color(invalid, 0f, 0f, 1f).ToHsv());
            RejectNonFinite(() => new Color(0f, invalid, 0f, 1f).ToHsv());
            RejectNonFinite(() => new Color(0f, 0f, invalid, 1f).ToHsv());
            RejectNonFinite(() => new Color(invalid, 0f, 0f, 1f).ToHex());
            RejectNonFinite(() => new Color(0f, invalid, 0f, 1f).ToHex());
            RejectNonFinite(() => new Color(0f, 0f, invalid, 1f).ToHex());
            RejectNonFinite(() => new Color(0f, 0f, 0f, invalid).ToHex());
        }

        Check(Color.FromHex("#FF0000") == red, "Hex parses opaque red");
        Check(Color.FromHex("#ff0000ff") == red, "Hex is case-insensitive with alpha");
        Check(Color.FromHex("00FF0080") == new Color(0f, 1f, 0f, 128f / 255f), "Leading # is optional");
        Check(Color.FromHex(red.ToHex()) == red, "Hex round trip");
        Reject(() => Color.FromHex("#FFF"));
        Reject(() => Color.FromHex("#GGGGGG"));
        Reject(() => Color.FromHex(string.Empty));
        Console.WriteLine("PASS: RGBA store, presets, HSV derivation, and hex parsing.");
    }

    private static bool Close(float a, float b) => MathF.Abs(a - b) < 1e-4f;

    private static void RejectNonFinite(Action action)
    {
        try { action(); }
        catch (ArgumentOutOfRangeException) { return; }
        throw new InvalidOperationException("A non-finite color channel was accepted.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (FormatException) { return; }
        throw new InvalidOperationException("Invalid hex color was accepted.");
    }
}
