using System.Numerics;

namespace PureEngine.Core;

/// <summary>Button state visuals. Distinguishes the Normal, Hover, Pressed, and Disabled tints from the keyboard-focus outline.</summary>
/// <remarks>Never rewrites the persisted Image.Color for display. The effective color is only combined by multiplication when read.</remarks>
public enum UiButtonVisualState
{
    Normal,
    Hover,
    Pressed,
    Disabled,
}

public static class UiButtonVisuals
{
    /// <summary>Outline color for keyboard focus. Drawn separately from tints such as hover.</summary>
    public static Vector4 FocusOutline { get; } = new(0.55f, 0.49f, 0.96f, 1f);

    /// <summary>Per-state multiply tint. All states differ from each other.</summary>
    public static Vector4 TintFor(UiButtonVisualState state) => state switch
    {
        UiButtonVisualState.Hover => new Vector4(0.9f, 0.9f, 1f, 1f),
        UiButtonVisualState.Pressed => new Vector4(0.7f, 0.7f, 0.85f, 1f),
        UiButtonVisualState.Disabled => new Vector4(0.5f, 0.5f, 0.5f, 0.5f),
        _ => Vector4.One,
    };

    /// <summary>Computes the effective color by multiplying the persisted color with the tint. The original Image.Color is left unchanged.</summary>
    public static Vector4 ApplyTint(Vector4 imageColor, UiButtonVisualState state)
    {
        var tint = TintFor(state);
        var mixed = imageColor * tint;
        return Vector4.Clamp(mixed, Vector4.Zero, Vector4.One);
    }

    /// <summary>Resolves the Button state. Disabled wins, then Pressed, then Hover. Disabling a parent Button does not propagate to children.</summary>
    public static UiButtonVisualState Resolve(bool interactable, bool pressed, bool hovered) =>
        !interactable ? UiButtonVisualState.Disabled
        : pressed ? UiButtonVisualState.Pressed
        : hovered ? UiButtonVisualState.Hover
        : UiButtonVisualState.Normal;
}
