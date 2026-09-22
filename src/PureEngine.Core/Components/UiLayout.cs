using System.Numerics;

namespace PureEngine.Core;

public static class UiLayout
{
    /// <summary>Resolves an unscaled rectangle and its top-left-based transform without modifying components.</summary>
    /// <remarks>Pass the parent's UI layout result, not SceneObject.WorldMatrix. Anchors and pivot may lie outside 0–1.</remarks>
    public static (Vector2 Size, Matrix4x4 World) Calculate(
        Vector2 parentSize,
        Matrix4x4 parentWorld,
        Transform transform,
        UiElement uiElement)
    {
        ArgumentNullException.ThrowIfNull(transform);
        ArgumentNullException.ThrowIfNull(uiElement);
        if (!IsFinite(parentSize) || parentSize.X < 0 || parentSize.Y < 0)
            throw new ArgumentOutOfRangeException(nameof(parentSize), "Parent size must be finite and non-negative.");
        if (!IsFinite(parentWorld))
            throw new ArgumentException("Parent matrix must be finite.", nameof(parentWorld));
        if (!IsFinite(uiElement.AnchorMin) || !IsFinite(uiElement.AnchorMax)
            || !IsFinite(uiElement.SizeDelta) || !IsFinite(uiElement.Pivot)
            || uiElement.AnchorMin.X > uiElement.AnchorMax.X || uiElement.AnchorMin.Y > uiElement.AnchorMax.Y)
            throw new ArgumentException("UI values must be finite, with AnchorMin <= AnchorMax.", nameof(uiElement));

        var transformMatrix = transform.LocalMatrix;
        if (!IsFinite(transformMatrix))
            throw new ArgumentException("Transform must produce a finite matrix.", nameof(transform));

        var anchorMin = parentSize * uiElement.AnchorMin;
        var anchorSpan = parentSize * (uiElement.AnchorMax - uiElement.AnchorMin);
        var size = anchorSpan + uiElement.SizeDelta;
        if (!IsFinite(size) || size.X < 0 || size.Y < 0)
            throw new ArgumentException("Resolved size must be finite and non-negative.", nameof(uiElement));
        var anchorPoint = anchorMin + anchorSpan * uiElement.Pivot;
        var world = Matrix4x4.CreateTranslation(new Vector3(-uiElement.Pivot * size, 0))
            * transformMatrix
            * Matrix4x4.CreateTranslation(new Vector3(anchorPoint, 0))
            * parentWorld;
        if (!IsFinite(world)) throw new ArgumentException("UI layout calculation overflowed.");
        return (size, world);
    }

    private static bool IsFinite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static bool IsFinite(Matrix4x4 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14)
        && float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24)
        && float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34)
        && float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);
}
