namespace PureEngine.Core;

/// <summary>An immutable image reference and optional pixel region, independent of rendering resources.</summary>
public sealed class Sprite
{
    /// <summary>Project-local source image ID. Does not identify a GPU texture or an Image component.</summary>
    public Guid ImageId { get; }

    /// <summary>Top-left-origin pixel region. Null selects the entire source image.</summary>
    public (int X, int Y, int Width, int Height)? SourceRect { get; }

    public Sprite(Guid imageId, (int X, int Y, int Width, int Height)? sourceRect = null)
    {
        if (imageId == Guid.Empty) throw new ArgumentException("A source image ID is required.", nameof(imageId));
        if (sourceRect is { } rect && (rect.X < 0 || rect.Y < 0 || rect.Width <= 0 || rect.Height <= 0))
            throw new ArgumentOutOfRangeException(nameof(sourceRect), "Pixel coordinates must be non-negative and dimensions positive.");
        ImageId = imageId;
        SourceRect = sourceRect;
    }

    /// <summary>Resolves the region against decoded image dimensions, rejecting out-of-bounds crops without clamping.</summary>
    public (int X, int Y, int Width, int Height) ResolveSourceRect(int imageWidth, int imageHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(imageWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(imageHeight);
        var rect = SourceRect ?? (X: 0, Y: 0, Width: imageWidth, Height: imageHeight);
        // Subtract after checking the origin, so large pixel coordinates cannot overflow a right/bottom sum.
        if (rect.X > imageWidth || rect.Y > imageHeight
            || rect.Width > imageWidth - rect.X || rect.Height > imageHeight - rect.Y)
            throw new ArgumentException("Sprite region is outside the source image.");
        return rect;
    }
}
