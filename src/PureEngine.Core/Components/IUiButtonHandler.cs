namespace PureEngine.Core;

/// <summary>Buttonと同じSceneObjectに付けてクリック処理を受ける契約。基底クラスは不要。</summary>
public interface IUiButtonHandler
{
    void OnClick(UiClickContext context);
}

/// <summary>クリック呼び出し時の一時的な情報。実行用SceneとButtonのSceneObjectを持つ。</summary>
public readonly record struct UiClickContext(Scene Scene, SceneObject ButtonObject);
