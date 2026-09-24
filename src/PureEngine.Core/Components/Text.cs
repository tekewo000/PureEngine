namespace PureEngine.Core;

/// <summary>Draws a string from the UiElement origin with the bundled font. Uses the same layout and Order path as Image.</summary>
/// <remarks>
/// Wraps by Unicode text element within the resolved width, starting from the top-left. Empty content draws nothing.
/// Height does not clip overflowing lines; only the viewport clip applies.
/// Invalid sizes surface as draw-time diagnostics instead of throwing from this data.
/// The typeId is core.text.
/// </remarks>
public sealed class Text : RendererComponent
{
    [Inspector]
    public string Content { get; set; } = "New Text";

    [Inspector]
    public Color Color { get; set; } = Color.White;

    [Inspector]
    public float FontSize { get; set; } = 24f;

    [Inspector]
    public float LineSpacing { get; set; } = 1.2f;
}