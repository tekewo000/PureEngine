namespace PureEngine.Core;

/// <summary>Buttonに対応するhandler数の検証。0個は有効、1個は実行、複数は完成Scene検証で拒否する。</summary>
/// <remarks>保存するのは既存のcomponent typeIdとInspector値のみで、メソッド名やデリゲートは保存しない。</remarks>
public static class UiButtonValidation
{
    /// <summary>同じSceneObjectのhandler数を数える。</summary>
    public static int CountHandlers(SceneObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var count = 0;
        foreach (var component in item.Components)
        {
            if (component is IUiButtonHandler)
                count++;
        }
        return count;
    }

    /// <summary>Scene内の全Buttonを検証し、複数handlerがあれば理由付きで拒否する。</summary>
    public static void Validate(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        foreach (var item in scene.Objects)
        {
            if (item.GetComponent<Components.Button>() is null)
                continue;
            var count = CountHandlers(item);
            if (count > 1)
                throw new InvalidOperationException(
                    $"{item.Name}: Button has {count} IUiButtonHandler components, at most one is allowed.");
        }
    }
}
