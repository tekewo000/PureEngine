namespace PureEngine.Core;

/// <summary>Button自身が実装するクリック入力の契約。</summary>
public interface IUiButtonHandler
{
    void OnClick(UiClickContext context);
}

/// <summary>クリック呼び出し時の一時的な情報。実行用SceneとButtonのSceneObjectを持つ。</summary>
public readonly record struct UiClickContext(Scene Scene, SceneObject ButtonObject);
