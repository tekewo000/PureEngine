namespace PureEngine.Core;

/// <summary>UiElementの領域でクリック判定を受ける普通のComponent。見た目は同じオブジェクトのImageを使う。</summary>
/// <remarks>
/// 保存されるのは <see cref="Button.Interactable"/> だけ。一時的な押下・ホバー・フォーカス状態は持たない。
/// typeId は core.button。
/// </remarks>
public sealed class Button : IUiButtonHandler
{
    [Inspector]
    public bool Interactable { get; set; } = true;

    /// <summary>実行用インスタンスへ登録するクリック処理。保存・Cloneでは引き継がない。</summary>
    public event Action<UiClickContext>? Clicked;

    public void OnClick(UiClickContext context) => Clicked?.Invoke(context);
}
