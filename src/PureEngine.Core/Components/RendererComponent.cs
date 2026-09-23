namespace PureEngine.Core;

/// <summary>Imageと将来のSpriteRendererに共通する描画順の基底。配置計算には使わない。</summary>
public abstract class RendererComponent
{
    [Inspector]
    public int Order { get; set; }
}
