using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace PureEngine.Rendering.Avalonia;

/// <summary>GPU-only composition bridge, shared by Editor and the standalone rendering probe.</summary>
public sealed class VulkanViewport : Control
{
    private static VulkanDevice? _applicationDevice;
    private static byte[]? _adapterLuid;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CompositionDrawingSurface? _surface;
    private CompositionSurfaceVisual? _visual;
    private ICompositionGpuInterop? _interop;
    private ICompositionImportedGpuImage? _image;
    private VulkanRenderer? _renderer;
    private DrawList? _drawList;
    private ImageRenderingSample? _sample;
    private bool _attached, _failed, _closing;
    private Window? _window;
    private string? _failure;
    private bool _resetAtlas;
    public event Action<string>? RenderingFailed;
    public int FrameCount { get; private set; }
    public string? DeviceName => _renderer?.DeviceName;
    public string? Failure => _failure;

    /// <summary>編集Sceneの描画入口。未設定なら検証用サンプルを描く。例外は投げず、失敗時は描画を止めて通知する。</summary>
    public Action<DrawList, Vector2>? SceneBuilder { get; set; }

    public VulkanViewport() => _timer.Tick += Tick;

    /// <summary>Invalidate on the UI thread; apply inside the next serialized frame after the previous presentation.</summary>
    public void InvalidateImageCache()
    {
        Dispatcher.UIThread.VerifyAccess();
        _resetAtlas = true;
    }

    public static AppBuilder Configure(AppBuilder builder) => builder.With(new Win32PlatformOptions
    {
        RenderingMode = [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software]
    });

    public Vector2 ToPixel(Point point) => new((float)(point.X * (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1)),
        (float)(point.Y * (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1)));

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _window = TopLevel.GetTopLevel(this) as Window;
        _window?.Closing += OnWindowClosing;
        if (!_failed) _timer.Start();
    }

    protected override async void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _timer.Stop();
        _window?.Closing -= OnWindowClosing;
        _window = null;
        base.OnDetachedFromVisualTree(e);
        await _gate.WaitAsync();
        try { await Release(); }
        catch (Exception error) { Fail(error); }
        finally { _gate.Release(); }
    }

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (e.Cancel || _closing || (_renderer is null && _gate.CurrentCount == 1)) return;
        e.Cancel = true;
        _closing = true;
        await ShutdownAsync();
        ((Window)sender!).Close();
    }

    public async Task ShutdownAsync()
    {
        _attached = false;
        _timer.Stop();
        await _gate.WaitAsync();
        try { await Release(); }
        catch (Exception error) { Fail(error); }
        finally { _gate.Release(); }
    }

    private async void Tick(object? sender, EventArgs e)
    {
        if (!_attached || _failed || !this.IsEffectivelyVisible || !await _gate.WaitAsync(0)) return;
        try
        {
            var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            var size = PixelSize.FromSize(Bounds.Size, scaling);
            if (size.Width <= 0 || size.Height <= 0) return;
            if (_renderer is null)
            {
                var compositor = ElementComposition.GetElementVisual(this)!.Compositor;
                _interop = await compositor.TryGetCompositionGpuInterop() ?? throw new NotSupportedException("The active Avalonia backend has no GPU interop.");
                var type = KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureNtHandle;
                if (!_interop.SupportedImageHandleTypes.Contains(type) ||
                    (_interop.GetSynchronizationCapabilities(type) & CompositionGpuImportedImageSynchronizationCapabilities.KeyedMutex) == 0)
                    throw new NotSupportedException("The active backend cannot share D3D11 NT images with keyed mutex synchronization.");
                if (_interop.DeviceUuid is null && _interop.DeviceLuid is null)
                    throw new NotSupportedException("The compositor adapter cannot be identified safely.");
                if (_applicationDevice is null)
                {
                    _applicationDevice = new VulkanDevice(_interop.DeviceUuid, _interop.DeviceLuid,
                        Environment.GetEnvironmentVariable("PUREENGINE_VULKAN_VALIDATION") == "1");
                    _adapterLuid = _interop.DeviceLuid;
                    if (Application.Current?.ApplicationLifetime is global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lifetime)
                        lifetime.Exit += (_, _) => { _applicationDevice?.Dispose(); _applicationDevice = null; };
                }
                else if (_interop.DeviceLuid is null || _adapterLuid is null || !_interop.DeviceLuid.SequenceEqual(_adapterLuid))
                    throw new NotSupportedException("Multiple compositor adapters are not supported by this application context.");
                _renderer = new VulkanRenderer(_applicationDevice);
                _drawList = new DrawList();
                _sample = new ImageRenderingSample();
                _surface = compositor.CreateDrawingSurface();
                _visual = compositor.CreateSurfaceVisual();
                _visual.Surface = _surface;
                ElementComposition.SetElementChildVisual(this, _visual);
            }
            if (!_attached) return;
            if (_interop!.IsLost) throw new InvalidOperationException("Avalonia GPU device was lost.");
            if (_renderer.Width != size.Width || _renderer.Height != size.Height)
            {
                await RetireImports();
                _renderer.Resize(size.Width, size.Height);
            }
            var logicalSize = new Vector2((float)Bounds.Width, (float)Bounds.Height);
            if (_resetAtlas)
            {
                _drawList!.ResetAtlas();
                _resetAtlas = false;
            }
            if (SceneBuilder is not null) SceneBuilder(_drawList!, logicalSize);
            else _sample!.Build(_drawList!, logicalSize);
            _renderer.Render(_drawList!, logicalSize);
            if (_image is null)
            {
                using var memory = _renderer.ExportImage();
                _image = _interop.ImportImage(new PlatformHandle(memory.DangerousGetHandle(), KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureNtHandle),
                    new PlatformGraphicsExternalImageProperties { Width = size.Width, Height = size.Height, MemorySize = _renderer.MemorySize,
                        Format = PlatformGraphicsExternalImageFormat.R8G8B8A8UNorm,
                        VulkanProperties = new PlatformGraphicsExternalImageVulkanProperties { Layout = (int)VulkanRenderer.SharedLayout } });
                await _image.ImportCompleted;
            }
            _visual!.Size = new Vector2((float)Bounds.Width, (float)Bounds.Height);
            await _surface!.UpdateWithKeyedMutexAsync(_image, 1, 0);
            FrameCount++;
        }
        catch (Exception error)
        {
            Fail(error);
            try { await Release(); }
            catch (Exception cleanup) { RenderingFailed?.Invoke(cleanup.ToString()); }
        }
        finally { _gate.Release(); }
    }

    private void Fail(Exception error)
    {
        _failed = true;
        _timer.Stop();
        _failure = "Vulkan rendering unavailable: " + error.Message;
        RenderingFailed?.Invoke(error.ToString());
        InvalidateVisual();
    }

    private async Task RetireImports()
    {
        if (_image is { } image) { _image = null; await image.DisposeAsync(); }
    }

    private async Task Release()
    {
        _visual?.Surface = null;
        try { await RetireImports(); }
        finally
        {
            _surface?.Dispose(); _surface = null;
            _renderer?.Dispose(); _renderer = null;
            _drawList?.Dispose(); _drawList = null;
            _sample = null;
            _visual = null; _interop = null;
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_failure is null) return;
        var text = new FormattedText(_failure, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, Typeface.Default, 13, Brushes.OrangeRed) { MaxTextWidth = Math.Max(1, Bounds.Width - 24) };
        context.DrawText(text, new Point(12, 12));
    }
}
