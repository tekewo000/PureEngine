using System.Numerics;

namespace PureEngine.Core;

public sealed class Image : RendererComponent
{
    [Inspector]
    public Sprite? Sprite { get; set; }

    [Inspector]
    public Color Color { get; set; } = Color.White;
}
