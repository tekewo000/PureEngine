using System.Numerics;
using PureEngine.Core;

public sealed class Image
{
    [Inspector]
    public Sprite? Sprite { get; set; }

    [Inspector]
    public Vector4 Color { get; set; } = Vector4.One;
}
