using System.Diagnostics;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using PureEngine.Rendering;
using PureEngine.Rendering.Avalonia;
using PureEngine.Runtime;

namespace PureEngine.Player;

/// <summary>Owns one packaged play session and its standalone presentation lifetime.</summary>
public sealed class PlayerWindow : Window
{
    private readonly GamePackage _package;
    private readonly PlaySession _session;
    private readonly PlayerInput _input;
    private readonly Dictionary<Guid, byte[]> _images;
    private readonly VulkanViewport _viewport = new() { Focusable = true };
    private readonly Grid _surface = new() { Background = Brushes.Transparent, Focusable = true };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1.0 / 60) };
    private long _lastTick;
    private IPointer? _pointer;
    private Key? _heldKey;
    private bool _stopped;

    public PlayerWindow(GamePackage package)
    {
        _package = package;
        _images = package.Assets.LoadImageBytes();
        _session = package.CreateSession();
        _input = new PlayerInput(_session.Runtime);
        Title = package.Name;
        Width = 1280;
        Height = 720;
        MinWidth = 160;
        MinHeight = 120;
        _surface.Children.Add(_viewport);
        Content = _surface;
        _viewport.SceneBuilder = Draw;
        _viewport.RenderingFailed += message => Fail(new InvalidOperationException(message));
        _timer.Tick += Tick;
        Opened += OnOpened;
        Closed += (_, _) => Stop();
        Deactivated += (_, _) => CancelInput();
        _surface.LostFocus += (_, _) => CancelInput();
        _surface.PointerExited += (_, _) => _input.Exit();
        _surface.PointerPressed += OnPointerPressed;
        _surface.PointerMoved += OnPointerMoved;
        _surface.PointerReleased += OnPointerReleased;
        _surface.PointerCaptureLost += (_, e) =>
        {
            if (ReferenceEquals(_pointer, e.Pointer)) CancelInput();
        };
        _surface.KeyDown += OnKeyDown;
        _surface.KeyUp += OnKeyUp;
    }

    private Vector2 ViewSize => new((float)_surface.Bounds.Width, (float)_surface.Bounds.Height);

    private void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            _session.Start();
            PlayerLog.Current.Drain();
            if (_session.Runtime.Errors.Count != 0)
            {
                Fail(new AggregateException("Game startup failed.", _session.Runtime.Errors.Select(error => error.Exception)));
                return;
            }
            _lastTick = Stopwatch.GetTimestamp();
            _surface.Focus();
            _timer.Start();
        }
        catch (Exception error) { Fail(error); }
    }

    private void Tick(object? sender, EventArgs e)
    {
        try
        {
            var now = Stopwatch.GetTimestamp();
            var dt = (float)Stopwatch.GetElapsedTime(_lastTick, now).TotalSeconds;
            _lastTick = now;
            _session.Step(Math.Min(dt, 0.1f));
            PlayerLog.Current.Drain();
            if (_session.Runtime.Errors.Count != 0)
                Fail(new AggregateException("Game lifecycle failed.", _session.Runtime.Errors.Select(error => error.Exception)));
            else if (!_session.Runtime.IsRunning)
                Close();
        }
        catch (Exception error) { Fail(error); }
    }

    private void Draw(DrawList draw, Vector2 size)
    {
        if (_stopped || !_session.Runtime.IsRunning) { draw.Clear(); return; }
        var diagnostics = GameSceneRenderer.Build(draw, _session.Runtime.Scene, _images, size,
            _input.Visuals(_surface.IsKeyboardFocusWithin), _session.Localization, _session.Runtime.Scene.Localization);
        _input.Validate(size, diagnostics);
        if (!_input.HasPointerPress && _pointer is not null) ReleasePointer();
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_stopped || e.GetCurrentPoint(_surface).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
            return;
        _surface.Focus();
        if (_input.Press(ViewSize, Point(e)))
        {
            _pointer = e.Pointer;
            e.Pointer.Capture(_surface);
        }
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pointer is null || ReferenceEquals(_pointer, e.Pointer)) _input.Move(ViewSize, Point(e));
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(_pointer, e.Pointer)
            || e.GetCurrentPoint(_surface).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonReleased)
            return;
        _input.Release(ViewSize, Point(e));
        ReleasePointer();
        e.Handled = true;
    }

    private Vector2 Point(PointerEventArgs e)
    {
        var point = e.GetPosition(_surface);
        return new Vector2((float)point.X, (float)point.Y);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_stopped) return;
        if (e.Key == Key.Tab && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
        {
            ReleasePointer();
            _input.Tab(ViewSize, e.KeyModifiers == KeyModifiers.Shift);
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Space && e.KeyModifiers == KeyModifiers.None)
        {
            if (_heldKey is null)
            {
                _heldKey = e.Key;
                _input.KeyDown(ViewSize);
            }
            e.Handled = true;
        }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (_heldKey != e.Key) return;
        _heldKey = null;
        _input.KeyUp();
        e.Handled = true;
    }

    private void ReleasePointer()
    {
        var pointer = _pointer;
        _pointer = null;
        if (ReferenceEquals(pointer?.Captured, _surface)) pointer!.Capture(null);
    }

    private void CancelInput()
    {
        _input.Cancel();
        _heldKey = null;
        ReleasePointer();
    }

    private void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        _timer.Stop();
        CancelInput();
        try
        {
            _session.Dispose();
            foreach (var error in _session.Runtime.Errors)
            {
                PlayerLog.Current.Report(error.Exception);
                SetFailureExitCode();
            }
        }
        catch (Exception error) { PlayerLog.Current.Report(error); SetFailureExitCode(); }
        finally
        {
            try { _package.Dispose(); }
            catch (Exception error) { PlayerLog.Current.Report(error); SetFailureExitCode(); }
            PlayerLog.Current.Drain();
        }
    }

    private void Fail(Exception error)
    {
        if (_stopped) return;
        PlayerLog.Current.Report(error);
        SetFailureExitCode();
        Stop();
        // Dispatch after a failing renderer releases its submission gate.
        Dispatcher.UIThread.Post(() => Content = new TextBlock
        {
            Text = "The game stopped because an error occurred.\n\n" + error.GetBaseException().Message
                + "\n\n" + PlayerLog.Current.LocationDescription,
            Margin = new Thickness(24),
            TextWrapping = TextWrapping.Wrap,
        });
    }

    private static void SetFailureExitCode()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Exit += (_, e) => e.ApplicationExitCode = 1;
    }
}
