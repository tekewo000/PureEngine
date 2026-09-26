using System.Numerics;
using PureEngine.Core;
using PureEngine.Rendering;

namespace PureEngine.Player;

/// <summary>Button input for a standalone runtime. Clicks enter the lifecycle queue, never rendering.</summary>
public sealed class PlayerInput(SceneRuntime runtime)
{
    private readonly HashSet<Guid> _drawFailures = [];
    private Guid? _pressed;
    private Guid? _keyboardButton;
    private bool _keyHeld;
    public Guid? Hovered { get; private set; }
    public Guid? Focused { get; private set; }
    public bool HasPointerPress => _pressed is not null;

    public void Move(Vector2 size, Vector2 point) => Hovered = Hit(size, point)?.Id;

    public bool Press(Vector2 size, Vector2 point)
    {
        if (_pressed is not null || _keyHeld) return false;
        Move(size, point);
        Focused = Hovered;
        _pressed = Hovered;
        return HasPointerPress;
    }

    public bool Release(Vector2 size, Vector2 point)
    {
        Move(size, point);
        var clicked = _pressed is { } id && Hovered == id;
        var pressed = _pressed;
        _pressed = null;
        if (clicked) runtime.EnqueueButtonClick(pressed!.Value);
        return clicked;
    }

    public void Cancel()
    {
        _pressed = null;
        _keyboardButton = null;
        _keyHeld = false;
        Hovered = null;
    }

    public void Exit() => Hovered = null;

    public void Tab(Vector2 size, bool backward)
    {
        var held = _keyHeld;
        Cancel();
        _keyHeld = held;
        var order = SceneViewMath.SortForRender(Layouts(size))
            .Where(entry => Available(entry, size)).Select(entry => entry.Object.Id).Distinct().ToList();
        if (order.Count == 0) { Focused = null; return; }
        var index = Focused is { } id ? order.IndexOf(id) : -1;
        Focused = index < 0 ? backward ? order[^1] : order[0]
            : order[(index + (backward ? order.Count - 1 : 1)) % order.Count];
    }

    public bool KeyDown(Vector2 size)
    {
        if (_keyHeld || _pressed is not null) return false;
        _keyHeld = true;
        if (Focused is not { } id || !Layouts(size).Any(entry => entry.Object.Id == id && Available(entry, size)))
            return false;
        _keyboardButton = id;
        runtime.EnqueueButtonClick(id);
        return true;
    }

    public void KeyUp()
    {
        _keyHeld = false;
        _keyboardButton = null;
    }

    public Dictionary<Guid, GameSceneRenderer.ButtonVisual> Visuals(bool hasFocus) =>
        runtime.Scene.Objects.Where(item => item.GetComponent<Button>() is not null).ToDictionary(
            item => item.Id,
            item => new GameSceneRenderer.ButtonVisual(UiButtonVisuals.Resolve(
                item.GetComponent<Button>()!.Interactable,
                (_pressed == item.Id && Hovered == item.Id) || _keyboardButton == item.Id,
                Hovered == item.Id), hasFocus && Focused == item.Id));

    public void Validate(Vector2 size, IReadOnlyList<GameSceneRenderer.Diagnostic> diagnostics)
    {
        _drawFailures.Clear();
        foreach (var diagnostic in diagnostics) _drawFailures.Add(diagnostic.ObjectId);
        var available = Layouts(size).Where(entry => Available(entry, size)).Select(entry => entry.Object.Id).ToHashSet();
        if (_pressed is { } pressed && !available.Contains(pressed)) _pressed = null;
        if (_keyboardButton is { } keyboard && !available.Contains(keyboard)) _keyboardButton = null;
        if (Focused is { } focused && !available.Contains(focused)) Focused = null;
        if (Hovered is { } hovered && !available.Contains(hovered)) Hovered = null;
    }

    private IReadOnlyList<SceneViewMath.LayoutEntry> Layouts(Vector2 size) =>
        runtime.IsRunning && SceneViewMath.IsValidViewport(size) ? SceneViewMath.EnumerateLayouts(runtime.Scene, size) : [];

    private SceneObject? Hit(Vector2 size, Vector2 point) =>
        float.IsFinite(point.X) && float.IsFinite(point.Y)
            ? SceneViewMath.HitTest(Layouts(size), size, Vector2.Zero, 1, point, Clickable)
            : null;

    private bool Clickable(SceneObject item) =>
        item.GetComponent<Button>() is { Interactable: true } && !_drawFailures.Contains(item.Id);

    private bool Available(SceneViewMath.LayoutEntry entry, Vector2 size)
    {
        if (!Clickable(entry.Object) || !SceneViewMath.TryGetSelectionFrame(entry, Vector2.Zero, 1, out var corners, out _))
            return false;
        Vector2[] viewport = [Vector2.Zero, new(size.X, 0), size, new(0, size.Y)];
        var x = corners[1] - corners[0];
        var y = corners[3] - corners[0];
        Vector2[] axes = [Vector2.UnitX, Vector2.UnitY, new(-x.Y, x.X), new(-y.Y, y.X)];
        foreach (var axis in axes)
        {
            if (corners.Max(point => Vector2.Dot(point, axis)) <= viewport.Min(point => Vector2.Dot(point, axis))
                || corners.Min(point => Vector2.Dot(point, axis)) >= viewport.Max(point => Vector2.Dot(point, axis)))
                return false;
        }
        return true;
    }
}
