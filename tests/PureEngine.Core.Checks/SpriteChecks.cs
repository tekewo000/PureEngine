using PureEngine.Core;

internal static class SpriteChecks
{
    public static void Run()
    {
        var imageId = Guid.NewGuid();
        var whole = new Sprite(imageId);
        Check(whole.ImageId == imageId && whole.SourceRect is null, "Whole image identity");
        Check(whole.ResolveSourceRect(128, 64) == (0, 0, 128, 64), "Whole image dimensions");
        Check(whole.ResolveSourceRect(256, 128) == (0, 0, 256, 128) && whole.SourceRect is null,
            "Whole image resolution must not cache or mutate dimensions");

        var crop = new Sprite(imageId, (16, 8, 32, 24));
        Check(crop.ResolveSourceRect(48, 32) == (16, 8, 32, 24), "Crop may touch the right and bottom edges");
        Reject(() => crop.ResolveSourceRect(47, 32));
        Reject(() => crop.ResolveSourceRect(48, 31));
        Check(crop.SourceRect == (16, 8, 32, 24), "Rejected resolution must preserve the crop");
        Check(new Sprite(imageId, (127, 63, 1, 1)).ResolveSourceRect(128, 64) == (127, 63, 1, 1), "Last pixel");
        Check(new Sprite(imageId, (int.MaxValue - 1, 0, 1, 1)).ResolveSourceRect(int.MaxValue, 1)
            == (int.MaxValue - 1, 0, 1, 1), "Large valid coordinates");
        Reject(() => new Sprite(imageId, (int.MaxValue, 0, 1, 1)).ResolveSourceRect(int.MaxValue, 1));
        Reject(() => new Sprite(imageId, (0, int.MaxValue, 1, 1)).ResolveSourceRect(1, int.MaxValue));
        Reject(() => new Sprite(imageId, (int.MaxValue, 0, int.MaxValue, 1)).ResolveSourceRect(128, 64));
        Reject(() => _ = new Sprite(Guid.Empty));
        Reject(() => _ = new Sprite(imageId, (-1, 0, 1, 1)));
        Reject(() => _ = new Sprite(imageId, (0, -1, 1, 1)));
        Reject(() => _ = new Sprite(imageId, (0, 0, 0, 1)));
        Reject(() => _ = new Sprite(imageId, (0, 0, 1, -1)));
        Reject(() => whole.ResolveSourceRect(0, 1));
        Reject(() => whole.ResolveSourceRect(1, -1));
        Console.WriteLine("PASS: sprite image identity, whole/cropped regions, immutable resolution and bounds without overflow.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Invalid sprite data was accepted.");
    }
}
