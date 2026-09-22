using System.Numerics;

namespace PureEngine.Core;

// this class require Transform component
public sealed class UiElement
{
    [Inspector]
    public Vector2 SizeDelta { get; set; } = new(100f, 100f);

    [Inspector]
    public Vector2 AnchorMin { get; set; } = new(0, 0);

    [Inspector]
    public Vector2 AnchorMax { get; set; } = new(0, 0);

    [Inspector]
    public Vector2 Pivot { get; set; } = new(0.5f, 0.5f);
}
