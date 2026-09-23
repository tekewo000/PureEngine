using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PureEngine.Core;
using PureEngine.Rendering;

namespace PureEngine.Editor;

public partial class MainWindow
{
    private Rendering.Avalonia.VulkanViewport? _gameViewport;
    private Guid? _gameHovered;
    private Guid? _gamePressed;
    private IPointer? _gamePressedPointer;
    private Guid? _gameFocused;
    private Key? _gameKeyboardKey;
    private Guid? _gameKeyboardButton;
    private readonly HashSet<Guid> _gameDrawFailures = [];

    internal Guid? GameHoveredForTest => _gameHovered;
    internal Guid? GamePressedForTest => _gamePressed;
    internal Guid? GameFocusedForTest => _gameFocused;

    /// <summary>GameをPlaySessionの実行用Sceneへ接続する。Start／Updateは描画から呼ばない。</summary>
    private void ConnectGameViewport(Rendering.Avalonia.VulkanViewport viewport)
    {
        _gameViewport = viewport;
        viewport.SceneBuilder = (draw, size) =>
        {
            try
            {
                DrawGameView(draw, size);
            }
            catch (Exception error)
            {
                Log.Engine.Error($"Game preview failed: {error.GetBaseException().Message}", error);
            }
        };
    }

    /// <summary>実行Sceneを配置・Order順に描き、Button状態を重ねる。非表示時は描画しないが進行は止めない。</summary>
    private void DrawGameView(DrawList draw, Vector2 size)
    {
        var runtime = ActivePlay?.Runtime;
        if (!IsPlaying || runtime is null || !runtime.IsRunning)
        {
            draw.Clear();
            return;
        }
        Dictionary<Guid, GameSceneRenderer.ButtonVisual> states = [];
        foreach (var item in runtime.Scene.Objects)
        {
            if (item.GetComponent<PureEngine.Core.Components.Button>() is not { } button)
                continue;
            var id = item.Id;
            var state = UiButtonVisuals.Resolve(
                button.Interactable,
                (_gamePressed == id && _gameHovered == id) || _gameKeyboardButton == id,
                _gameHovered == id);
            states[id] = new GameSceneRenderer.ButtonVisual(state, GameViewport.IsKeyboardFocusWithin && _gameFocused == id);
        }
        var diagnostics = GameSceneRenderer.Build(draw, runtime.Scene, _previewImages, size, states);
        _gameDrawFailures.Clear();
        foreach (var diagnostic in diagnostics)
            _gameDrawFailures.Add(diagnostic.ObjectId);
        ValidateGameInput();
    }

    private Vector2 GameViewportSize()
    {
        var size = GameViewport.Bounds.Size;
        return new Vector2((float)size.Width, (float)size.Height);
    }

    private bool IsDrawableGameViewport(out Vector2 viewportSize)
    {
        viewportSize = GameViewportSize();
        return SceneViewMath.IsValidViewport(viewportSize)
            && ViewportTabs.SelectedIndex == 1
            && GameViewport.IsEffectivelyVisible;
    }

    private IReadOnlyList<SceneViewMath.LayoutEntry> GameLayouts(Vector2 viewportSize)
    {
        var runtime = ActivePlay?.Runtime;
        if (!IsPlaying || runtime is null)
            return [];
        return SceneViewMath.EnumerateLayouts(runtime.Scene, viewportSize);
    }

    private bool IsClickableButton(SceneObject item)
    {
        if (item.GetComponent<PureEngine.Core.Components.Button>() is not { Interactable: true })
            return false;
        return !_gameDrawFailures.Contains(item.Id);
    }

    private SceneObject? HitGameButton(Vector2 viewportSize, Vector2 viewPoint)
    {
        if (!IsPlaying)
            return null;
        return SceneViewMath.HitTest(
            GameLayouts(viewportSize), viewportSize, Vector2.Zero, 1f, viewPoint, IsClickableButton);
    }

    private SceneObject? FindRuntimeObject(Guid id)
    {
        var runtime = ActivePlay?.Runtime;
        if (!IsPlaying || runtime is null)
            return null;
        foreach (var item in runtime.Scene.Objects)
        {
            if (item.Id == id)
                return item;
        }
        return null;
    }

    private void InitGameInput()
    {
        GameViewport.Focusable = true;
        GameViewport.Background = Avalonia.Media.Brushes.Transparent;
        GameViewport.AddHandler(PointerPressedEvent, OnGamePointerPressed, RoutingStrategies.Bubble);
        GameViewport.AddHandler(PointerMovedEvent, OnGamePointerMoved, RoutingStrategies.Bubble);
        GameViewport.AddHandler(PointerReleasedEvent, OnGamePointerReleased, RoutingStrategies.Bubble);
        GameViewport.AddHandler(KeyDownEvent, OnGameKeyDown, RoutingStrategies.Bubble);
        GameViewport.AddHandler(KeyUpEvent, OnGameKeyUp, RoutingStrategies.Bubble);
        GameViewport.PointerCaptureLost += OnGameCaptureLost;
        GameViewport.PointerExited += (_, _) => _gameHovered = null;
        GameViewport.LostFocus += (_, _) => CancelGamePress();
        GameViewport.DetachedFromVisualTree += (_, _) => CancelGamePress();
        ViewportTabs.SelectionChanged += (_, _) =>
        {
            if (ViewportTabs.SelectedIndex != 1)
                CancelGamePress();
        };
        Deactivated += (_, _) => CancelGamePress();
    }

    private void OnGamePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsPlaying || !IsDrawableGameViewport(out var viewportSize))
            return;
        if (e.GetCurrentPoint(GameViewport).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
            return;
        if (_gamePressed is not null || _gameKeyboardKey is not null)
        {
            e.Handled = true;
            return;
        }
        GameViewport.Focus();
        var point = e.GetPosition(GameViewport);
        var viewPoint = new Vector2((float)point.X, (float)point.Y);
        if (!float.IsFinite(viewPoint.X) || !float.IsFinite(viewPoint.Y))
            return;
        var hit = HitGameButton(viewportSize, viewPoint);
        if (hit is null)
        {
            _gameFocused = null;
            e.Handled = true;
            return;
        }
        _gamePressed = hit.Id;
        _gamePressedPointer = e.Pointer;
        _gameHovered = hit.Id;
        _gameFocused = hit.Id;
        e.Pointer.Capture(GameViewport);
        e.Handled = true;
    }

    private void OnGamePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!IsPlaying || !IsDrawableGameViewport(out var viewportSize))
            return;
        if (_gamePressed is not null && !ReferenceEquals(e.Pointer, _gamePressedPointer))
            return;
        var point = e.GetPosition(GameViewport);
        var viewPoint = new Vector2((float)point.X, (float)point.Y);
        if (!float.IsFinite(viewPoint.X) || !float.IsFinite(viewPoint.Y))
            return;
        _gameHovered = HitGameButton(viewportSize, viewPoint)?.Id;
        e.Handled = true;
    }

    private void OnGamePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.GetCurrentPoint(GameViewport).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonReleased)
            return;
        if (_gamePressed is not { } pressedId || !ReferenceEquals(e.Pointer, _gamePressedPointer))
            return;
        var pointer = _gamePressedPointer;
        if (!IsPlaying || !IsDrawableGameViewport(out var viewportSize))
        {
            CancelGamePress();
            e.Handled = true;
            return;
        }
        var point = e.GetPosition(GameViewport);
        var viewPoint = new Vector2((float)point.X, (float)point.Y);
        var hit = float.IsFinite(viewPoint.X) && float.IsFinite(viewPoint.Y)
            ? HitGameButton(viewportSize, viewPoint)
            : null;
        var clicked = hit is not null && hit.Id == pressedId
            && FindRuntimeObject(pressedId)?.GetComponent<PureEngine.Core.Components.Button>() is { Interactable: true };
        ClearGamePress(pointer);
        if (clicked)
            ActivePlay?.Runtime.EnqueueButtonClick(pressedId);
        e.Handled = true;
    }

    private void OnGameCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (ReferenceEquals(e.Pointer, _gamePressedPointer))
            CancelGamePress();
    }

    private void OnGameKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsPlaying || !IsDrawableGameViewport(out var viewportSize))
            return;
        if (!GameViewport.IsKeyboardFocusWithin)
            return;
        if (e.Key == Key.Tab)
        {
            var backward = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            if (e.KeyModifiers is not KeyModifiers.None and not KeyModifiers.Shift)
                return;
            var heldKey = _gameKeyboardKey;
            CancelGamePress();
            _gameKeyboardKey = heldKey;
            FocusGameNeighbor(viewportSize, backward);
            e.Handled = true;
            return;
        }
        if (e.Key is not (Key.Enter or Key.Space))
            return;
        if (e.KeyModifiers != KeyModifiers.None)
            return;
        e.Handled = true;
        if (_gameKeyboardKey is not null || _gamePressed is not null)
            return;
        if (_gameFocused is not { } focusedId)
            return;
        if (FindRuntimeObject(focusedId)?.GetComponent<PureEngine.Core.Components.Button>() is not { Interactable: true })
            return;
        if (!IsKeyboardActivatable(viewportSize, focusedId))
            return;
        _gameKeyboardKey = e.Key;
        _gameKeyboardButton = focusedId;
        ActivePlay?.Runtime.EnqueueButtonClick(focusedId);
    }

    private void OnGameKeyUp(object? sender, KeyEventArgs e)
    {
        if (_gameKeyboardKey != e.Key)
            return;
        _gameKeyboardKey = null;
        _gameKeyboardButton = null;
        e.Handled = true;
    }

    private bool IsKeyboardActivatable(Vector2 viewportSize, Guid id)
    {
        foreach (var entry in GameLayouts(viewportSize))
        {
            if (entry.Object.Id != id)
                continue;
            return IsGameButtonAvailable(entry, viewportSize);
        }
        return false;
    }

    private bool IsGameButtonAvailable(SceneViewMath.LayoutEntry entry, Vector2 viewportSize)
    {
        if (!IsClickableButton(entry.Object)
            || !SceneViewMath.TryGetSelectionFrame(entry, Vector2.Zero, 1f, out var corners, out _))
            return false;
        // Separating axes of the projected UI rectangle and the viewport: fully clipped buttons cannot focus.
        Vector2[] viewport = [Vector2.Zero, new(viewportSize.X, 0), viewportSize, new(0, viewportSize.Y)];
        var x = corners[1] - corners[0];
        var y = corners[3] - corners[0];
        Vector2[] axes = [Vector2.UnitX, Vector2.UnitY, new(-x.Y, x.X), new(-y.Y, y.X)];
        foreach (var axis in axes)
        {
            var min = corners.Min(point => Vector2.Dot(point, axis));
            var max = corners.Max(point => Vector2.Dot(point, axis));
            if (max <= viewport.Min(point => Vector2.Dot(point, axis))
                || min >= viewport.Max(point => Vector2.Dot(point, axis)))
                return false;
        }
        return true;
    }

    private void ValidateGameInput()
    {
        if (!IsDrawableGameViewport(out var viewportSize))
        {
            CancelGamePress();
            return;
        }
        if ((_gamePressed is { } pressed && !IsKeyboardActivatable(viewportSize, pressed))
            || (_gameKeyboardButton is { } keyboard && !IsKeyboardActivatable(viewportSize, keyboard)))
        {
            var heldKey = _gameKeyboardKey;
            CancelGamePress();
            _gameKeyboardKey = heldKey;
        }
        if (_gameFocused is { } focused && !IsKeyboardActivatable(viewportSize, focused))
            _gameFocused = null;
        if (_gameHovered is { } hovered && !IsKeyboardActivatable(viewportSize, hovered))
            _gameHovered = null;
    }

    private void FocusGameNeighbor(Vector2 viewportSize, bool backward)
    {
        var entries = SceneViewMath.SortForRender(GameLayouts(viewportSize));
        List<Guid> order = [];
        foreach (var entry in entries)
        {
            if (!IsGameButtonAvailable(entry, viewportSize))
                continue;
            if (!order.Contains(entry.Object.Id))
                order.Add(entry.Object.Id);
        }
        if (order.Count == 0)
        {
            _gameFocused = null;
            return;
        }
        if (_gameFocused is not { } current || !order.Contains(current))
        {
            _gameFocused = backward ? order[^1] : order[0];
            return;
        }
        var index = order.IndexOf(current);
        var next = backward ? (index - 1 + order.Count) % order.Count : (index + 1) % order.Count;
        _gameFocused = order[next];
    }

    private void ClearGamePress(IPointer? pointer)
    {
        _gamePressed = null;
        _gamePressedPointer = null;
        if (ReferenceEquals(pointer?.Captured, GameViewport))
            pointer!.Capture(null);
    }

    /// <summary>押下状態だけを解除し、クリックを通知しない。フォーカス喪失・タブ切替・Stop用。</summary>
    internal void CancelGamePress()
    {
        var pointer = _gamePressedPointer;
        _gamePressed = null;
        _gamePressedPointer = null;
        _gameKeyboardKey = null;
        _gameKeyboardButton = null;
        _gameHovered = null;
        if (ReferenceEquals(pointer?.Captured, GameViewport))
            pointer!.Capture(null);
    }

    /// <summary>再Playに向けた入力状態の初期化。Stop・開始失敗・自動停止でも呼ぶ。</summary>
    internal void ResetGameInput()
    {
        CancelGamePress();
        _gameHovered = null;
        _gameFocused = null;
        _gameDrawFailures.Clear();
    }

    internal bool TryGamePressForTest(Vector2 viewPoint)
    {
        if (!IsPlaying || !IsDrawableGameViewport(out var viewportSize))
            return false;
        var hit = HitGameButton(viewportSize, viewPoint);
        if (hit is null)
            return false;
        _gamePressed = hit.Id;
        _gameHovered = hit.Id;
        _gameFocused = hit.Id;
        return true;
    }

    internal bool TryGameMoveForTest(Vector2 viewPoint)
    {
        if (!IsPlaying || !IsDrawableGameViewport(out var viewportSize))
            return false;
        _gameHovered = HitGameButton(viewportSize, viewPoint)?.Id;
        return true;
    }

    internal bool TryGameReleaseForTest(Vector2 viewPoint)
    {
        if (_gamePressed is not { } pressedId)
            return false;
        if (!IsPlaying || !IsDrawableGameViewport(out var viewportSize))
        {
            CancelGamePress();
            return false;
        }
        var hit = HitGameButton(viewportSize, viewPoint);
        var clicked = hit is not null && hit.Id == pressedId
            && FindRuntimeObject(pressedId)?.GetComponent<PureEngine.Core.Components.Button>() is { Interactable: true };
        _gamePressed = null;
        _gamePressedPointer = null;
        if (clicked)
            ActivePlay?.Runtime.EnqueueButtonClick(pressedId);
        return clicked;
    }

    internal bool TryGameTabForTest(bool backward)
    {
        if (!IsPlaying || !IsDrawableGameViewport(out var viewportSize))
            return false;
        var before = _gameFocused;
        FocusGameNeighbor(viewportSize, backward);
        return _gameFocused != before;
    }

    internal bool TryGameActivateForTest()
    {
        if (!IsPlaying || !IsDrawableGameViewport(out var viewportSize))
            return false;
        if (_gameFocused is not { } focusedId)
            return false;
        if (FindRuntimeObject(focusedId)?.GetComponent<PureEngine.Core.Components.Button>() is not { Interactable: true })
            return false;
        if (!IsKeyboardActivatable(viewportSize, focusedId))
            return false;
        ActivePlay?.Runtime.EnqueueButtonClick(focusedId);
        return true;
    }
}
