using System.Numerics;

namespace PureEngine.Core;

public sealed class Image : RendererComponent
{
    [Inspector]
    public Sprite? Sprite { get; set; }

    [Inspector]
    public Vector4 Color { get; set; } = Vector4.One;
}
