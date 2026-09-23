namespace PureEngine.Core;

/// <summary>Checks the required Image/Button/UiElement combinations. Never auto-adds; used to report what is missing.</summary>
public static class UiComponentRequirements
{
    /// <summary>Returns the missing UI combinations for the given object. Empty when everything is present.</summary>
    public static IReadOnlyList<string> GetMissing(SceneObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var hasTransform = item.GetComponent<Transform>() is not null;
        var hasElement = item.GetComponent<UiElement>() is not null;
        var hasImage = item.GetComponent<global::Image>() is not null;
        var hasButton = item.GetComponent<Components.Button>() is not null;
        if (hasImage || hasButton)
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
