using System.Numerics;

namespace PureEngine.Core;

/// <summary>Buttonの状態表示。通常・ホバー・押下・無効の tint とキーボードフォーカスの枠を区別する。</summary>
/// <remarks>表示のために保存済みの Image.Color を書き換えない。実効色は読み取り時に掛け合わせるだけ。</remarks>
public enum UiButtonVisualState
{
    Normal,
    Hover,
    Pressed,
    Disabled,
}

public static class UiButtonVisuals
{
    /// <summary>キーボードフォーカスの枠色。ホバー等の tint とは別に描く。</summary>
    public static Vector4 FocusOutline { get; } = new(0.55f, 0.49f, 0.96f, 1f);

    /// <summary>状態ごとの乗算 tint。いずれも互いに異なる。</summary>
    public static Vector4 TintFor(UiButtonVisualState state) => state switch
    {
        UiButtonVisualState.Hover => new Vector4(0.9f, 0.9f, 1f, 1f),
        UiButtonVisualState.Pressed => new Vector4(0.7f, 0.7f, 0.85f, 1f),
        UiButtonVisualState.Disabled => new Vector4(0.5f, 0.5f, 0.5f, 0.5f),
        _ => Vector4.One,
    };

    /// <summary>保存済み色に tint を掛けた実効色を求める。元の Image.Color は変更しない。</summary>
    public static Vector4 ApplyTint(Vector4 imageColor, UiButtonVisualState state)
    {
        var tint = TintFor(state);
        var mixed = imageColor * tint;
        return Vector4.Clamp(mixed, Vector4.Zero, Vector4.One);
    }

    /// <summary>Buttonの状態を決める。無効が最優先で、押下・ホバーの順。親Buttonの無効化は子へ波及しない。</summary>
    public static UiButtonVisualState Resolve(bool interactable, bool pressed, bool hovered) =>
        !interactable ? UiButtonVisualState.Disabled
        : pressed ? UiButtonVisualState.Pressed
        : hovered ? UiButtonVisualState.Hover
        : UiButtonVisualState.Normal;
}
