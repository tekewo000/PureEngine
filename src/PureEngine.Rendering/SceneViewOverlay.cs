using System.Numerics;
using PureEngine.Core;

namespace PureEngine.Rendering;

/// <summary>Scene View display elements for the first half of V4. Draws the grid, origin, axes, selection frame, Pivot, and move gizmo into the DrawList.</summary>
/// <remarks>Uses the same UiLayout results and view transform as SceneViewMath. Never becomes game Components or saved data.</remarks>
public static class SceneViewOverlay
{
    private static readonly Vector4 GridColor = new(0.23f, 0.25f, 0.29f, 1f);
    private static readonly Vector4 XAxisColor = new(0.88f, 0.42f, 0.35f, 1f);
    private static readonly Vector4 YAxisColor = new(0.51f, 0.79f, 0.58f, 1f);
    private static readonly Vector4 OriginColor = new(0.92f, 0.93f, 0.95f, 1f);
    private static readonly Vector4 SelectionColor = new(0.55f, 0.49f, 0.96f, 1f);
    private static readonly Vector4 PivotColor = new(1f, 1f, 1f, 1f);

    /// <summary>Draws the background grid, origin, and X/Y axes. Call before images to stay behind. Invalid values are a no-op.</summary>
    public static void DrawGrid(DrawList draw, Vector2 viewportSize, Vector2 pan, float zoom)
    {
        ArgumentNullException.ThrowIfNull(draw);
        if (!SceneViewMath.IsValidViewport(viewportSize) || !SceneViewMath.IsValidView(pan, zoom))
            return;
        var clip = new Vector4(0, 0, viewportSize.X, viewportSize.Y);
        var step = SceneViewMath.GridStep(zoom);
        if (!float.IsFinite(step) || step <= 0)
            return;
        var minScene = SceneViewMath.ViewToScene(Vector2.Zero, pan, zoom);
        var maxScene = SceneViewMath.ViewToScene(viewportSize, pan, zoom);
        if (!float.IsFinite(minScene.X + minScene.Y + maxScene.X + maxScene.Y))
            return;
        // Avoids dense drawing and unbounded enumeration. Caps counts even at spacing that keeps 48px on screen.
        var verticalCount = (int)Math.Ceiling((maxScene.X - minScene.X) / step) + 2;
        var horizontalCount = (int)Math.Ceiling((maxScene.Y - minScene.Y) / step) + 2;
        if (verticalCount is < 0 or > 500 || horizontalCount is < 0 or > 500)
            return;
        var firstX = MathF.Ceiling(minScene.X / step) * step;
        for (var i = 0; i < verticalCount; i++)
        {
            var sceneX = firstX + (i * step);
            if (sceneX > maxScene.X)
                break;
            var viewX = (sceneX * zoom) + pan.X;
            if (!float.IsFinite(viewX))
                continue;
            draw.Rectangle(new Vector2(1, viewportSize.Y),
                Matrix3x2.CreateTranslation(viewX, 0), GridColor, clip);
        }
        var firstY = MathF.Ceiling(minScene.Y / step) * step;
        for (var i = 0; i < horizontalCount; i++)
        {
            var sceneY = firstY + (i * step);
            if (sceneY > maxScene.Y)
                break;
            var viewY = (sceneY * zoom) + pan.Y;
            if (!float.IsFinite(viewY))
                continue;
            draw.Rectangle(new Vector2(viewportSize.X, 1),
                Matrix3x2.CreateTranslation(0, viewY), GridColor, clip);
        }
        var originView = SceneViewMath.SceneToView(Vector2.Zero, pan, zoom);
        if (originView.Y >= 0 && originView.Y <= viewportSize.Y)
            draw.Rectangle(new Vector2(viewportSize.X, 1.5f),
                Matrix3x2.CreateTranslation(0, originView.Y), XAxisColor, clip);
        if (originView.X >= 0 && originView.X <= viewportSize.X)
            draw.Rectangle(new Vector2(1.5f, viewportSize.Y),
                Matrix3x2.CreateTranslation(originView.X, 0), YAxisColor, clip);
        const float originSize = 7f;
        draw.Rectangle(new Vector2(originSize, originSize),
            Matrix3x2.CreateTranslation(originView.X - (originSize / 2), originView.Y - (originSize / 2)),
            OriginColor, clip);
    }

    /// <summary>Draws the selection frame (transformed corners) and Pivot. Call after images to stay in front.</summary>
    public static void DrawSelection(DrawList draw, Vector2[] cornersView, Vector2 pivotView, Vector4 clip)
    {
        ArgumentNullException.ThrowIfNull(draw);
        ArgumentNullException.ThrowIfNull(cornersView);
        if (cornersView.Length != 4)
            return;
        foreach (var corner in cornersView)
        {
            if (!float.IsFinite(corner.X) || !float.IsFinite(corner.Y))
                return;
        }
        if (!float.IsFinite(pivotView.X) || !float.IsFinite(pivotView.Y))
            return;
        const float thickness = 1.5f;
        for (var i = 0; i < 4; i++)
            DrawEdge(draw, cornersView[i], cornersView[(i + 1) % 4], thickness, SelectionColor, clip);
        const float arm = 9f;
        draw.Rectangle(new Vector2(arm, thickness),
            Matrix3x2.CreateTranslation(pivotView.X - (arm / 2), pivotView.Y - (thickness / 2)),
            PivotColor, clip);
        draw.Rectangle(new Vector2(thickness, arm),
            Matrix3x2.CreateTranslation(pivotView.X - (thickness / 2), pivotView.Y - (arm / 2)),
            PivotColor, clip);
    }

    /// <summary>Draws the move gizmo (X/Y arrows and center handle) at the Pivot. Appearance follows screen logical pixels.</summary>
    public static void DrawGizmo(
        DrawList draw, Vector2 pivotView, Vector2 xAxis, Vector2 yAxis, Vector4 clip)
    {
        ArgumentNullException.ThrowIfNull(draw);
        if (!float.IsFinite(pivotView.X + pivotView.Y + xAxis.X + xAxis.Y + yAxis.X + yAxis.Y))
            return;
        if (xAxis.LengthSquared() <= float.Epsilon || yAxis.LengthSquared() <= float.Epsilon)
            return;
        var x = Vector2.Normalize(xAxis);
        var y = Vector2.Normalize(yAxis);
        if (!float.IsFinite(x.X + x.Y + y.X + y.Y))
            return;
        const float shaftThickness = 3f;
        var startOffset = SceneViewMath.GizmoShaftStart;
        DrawEdge(draw, pivotView + (x * startOffset), pivotView + (x * SceneViewMath.GizmoLength),
            shaftThickness, XAxisColor, clip);
        DrawEdge(draw, pivotView + (y * startOffset), pivotView + (y * SceneViewMath.GizmoLength),
            shaftThickness, YAxisColor, clip);
        var head = SceneViewMath.GizmoHeadSize;
        var xHead = pivotView + (x * SceneViewMath.GizmoLength);
        var yHead = pivotView + (y * SceneViewMath.GizmoLength);
        draw.Rectangle(new Vector2(head, head),
            Matrix3x2.CreateTranslation(xHead.X - (head / 2), xHead.Y - (head / 2)), XAxisColor, clip);
        draw.Rectangle(new Vector2(head, head),
            Matrix3x2.CreateTranslation(yHead.X - (head / 2), yHead.Y - (head / 2)), YAxisColor, clip);
        var center = SceneViewMath.GizmoCenterSize;
        draw.Rectangle(new Vector2(center, center),
            Matrix3x2.CreateTranslation(pivotView.X - (center / 2), pivotView.Y - (center / 2)),
            PivotColor, clip);
    }

    private static void DrawEdge(DrawList draw, Vector2 from, Vector2 to, float thickness, Vector4 color, Vector4 clip)
    {
        var delta = to - from;
        var length = delta.Length();
        if (!float.IsFinite(length) || length <= 0)
            return;
        var dir = delta / length;
        var angle = MathF.Atan2(dir.Y, dir.X);
        var perpendicular = new Vector2(-dir.Y, dir.X);
        var origin = from - (perpendicular * (thickness / 2));
        var transform = Matrix3x2.CreateRotation(angle) * Matrix3x2.CreateTranslation(origin.X, origin.Y);
        draw.Rectangle(new Vector2(length, thickness), transform, color, clip);
    }
}
