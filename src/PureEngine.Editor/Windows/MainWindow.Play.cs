using System.Diagnostics;
using Avalonia.Threading;
using PureEngine.Runtime;

namespace PureEngine.Editor;

public partial class MainWindow
{
    private DispatcherTimer? _playTimer;
    private Stopwatch? _playClock;
    private TimeSpan _playLast;
    public bool IsPlaying => ViewModel.Play.IsPlaying;
    internal PlaySession? ActivePlay => ViewModel.Play.Session;

    private void InitPlayControls()
    {
        _playTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0 / 60.0) };
        _playTimer.Tick += OnPlayTick;
        ViewModel.Play.BeforeStart += CancelSceneViewDrag;
        ViewModel.Play.Started += OnPlayStarted;
        ViewModel.Play.Stopping += StopPlayUpdates;
        ViewModel.Play.InputReset += ResetGameInput;
        ViewModel.Play.ReturnToScene += OnPlayReturnedToScene;
        ViewModel.Play.FrameCompleted += ValidateGameInput;
        ViewModel.Play.PropertyChanged += OnPlayModelChanged;
        UpdatePlayUI();
    }

    private void OnPlayModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(PlayViewModel.IsPlaying)) UpdatePlayUI();
    }

    internal void StartPlay() => ViewModel.Play.Start();
    internal bool StopPlay() => ViewModel.Play.Stop();
    internal void StepPlayOnce(float dt) => ViewModel.Play.Step(dt);

    private void OnPlayStarted()
    {
        _playClock = Stopwatch.StartNew();
        _playLast = TimeSpan.Zero;
        // Layout must complete before the first timer tick.
        ActivateEditorViewport(GameViewportIndex);
        _playTimer?.Start();
    }

    private void StopPlayUpdates()
    {
        _playTimer?.Stop();
        _playClock?.Stop();
        _playClock = null;
        CancelGamePress();
    }

    private void OnPlayReturnedToScene() => ActivateEditorViewport(SceneViewportIndex);

    private void OnPlayTick(object? sender, EventArgs e)
    {
        if (!IsPlaying || _playClock is not { } clock) return;
        var now = clock.Elapsed;
        var dt = (float)(now - _playLast).TotalSeconds;
        _playLast = now;
        StepPlayOnce(float.IsFinite(dt) && dt >= 0 ? dt : 0);
    }

    private void UpdatePlayUI() => UpdateDataAssetTableChrome();

    private bool RejectWhenPlaying(string action)
    {
        if (!IsPlaying) return false;
        SetFileStatus($"Cannot {action} while playing. Stop first.", true);
        return true;
    }
}
