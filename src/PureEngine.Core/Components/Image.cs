using PureEngine.Core.Attributes;

namespace PureEngine.Core.Components;

/// <summary>
/// Stuffsの右クリックメニュー「UI」から作れる画像表示用の普通のComponent。
/// 描画・プレビューは将来の範囲とし、ここではInspectorで編集・YAMLで保存できる値だけを持つ。
/// </summary>
public sealed class Image
{
    /// <summary>Project内の画像ファイルへの相対パス。空のまま保存できる。</summary>
    [Inspector]
    public string SpritePath { get; set; } = "";

    /// <summary>画像に掛ける色。例: #FFFFFF。</summary>
    [Inspector]
    public string Color { get; set; } = "#FFFFFF";

    [Inspector]
    public float Width { get; set; } = 100;

    [Inspector]
    public float Height { get; set; } = 100;
}
