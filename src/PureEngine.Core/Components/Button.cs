namespace PureEngine.Core.Components;

/// <summary>UiElementの領域でクリック判定を受ける普通のComponent。見た目は同じオブジェクトのImageを使う。</summary>
/// <remarks>
/// 保存されるのは <see cref="Button.Interactable"/> だけ。一時的な押下・ホバー・フォーカス状態は持たない。
/// Avalonia.Controls.Button との衝突を避けるため Components 名前空間に置く。typeId は core.button。
/// </remarks>
public sealed class Button
{
    [Inspector]
    public bool Interactable { get; set; } = true;
}
