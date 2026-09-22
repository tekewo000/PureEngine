namespace PureEngine.Core;

/// <summary>Image／UiElementに必要な組み合わせの確認。自動追加はせず、不足の通知に使う。</summary>
public static class UiComponentRequirements
{
    /// <summary>指定オブジェクトで不足しているUIの組み合わせを返す。揃っていれば空。</summary>
    public static IReadOnlyList<string> GetMissing(SceneObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var hasTransform = item.GetComponent<Transform>() is not null;
        var hasElement = item.GetComponent<UiElement>() is not null;
        var hasImage = item.GetComponent<global::Image>() is not null;
        if (hasImage)
        {
            List<string> missing = [];
            if (!hasTransform) missing.Add(nameof(Transform));
            if (!hasElement) missing.Add(nameof(UiElement));
            return missing;
        }
        if (hasElement && !hasTransform) return [nameof(Transform)];
        return [];
    }
}
