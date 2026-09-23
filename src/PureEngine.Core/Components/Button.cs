using PureEngine.Core.Attributes;

namespace PureEngine.Core.Components;

/// <summary>
/// Stuffsの右クリックメニュー「UI」から作れるボタン用の普通のComponent。
/// 見た目（背景・大きさ）は同じオブジェクトの <see cref="Image"/> が持ち、
/// ここでは操作可否と表示文言だけを保存する。
/// Avalonia.Controls.Button との衝突を避けるため Components 名前空間に置く。
/// </summary>
public sealed class Button
{
    /// <summary>ボタンに表示する文言。</summary>
    [Inspector]
    public string Text { get; set; } = "Button";

    [Inspector]
    public bool Interactable { get; set; } = true;
}
