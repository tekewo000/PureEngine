namespace PureEngine.Core;

/// <summary>Base class for render order shared by Image and the future SpriteRenderer. Not used for layout calculation.</summary>
public abstract class RendererComponent
{
    [Inspector]
    public int Order { get; set; }
}
