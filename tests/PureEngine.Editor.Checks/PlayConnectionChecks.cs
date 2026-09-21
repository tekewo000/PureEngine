using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PureEngine.Core;
using PureEngine.Core.Attributes;
using PureEngine.Editor;
using PureEngine.Runtime;

static class PlayConnectionChecks
{
    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static object? Call(MainWindow window, string method, params object?[] args) =>
        typeof(MainWindow).GetMethod(method, AnyInstance)!.Invoke(window, args);
    private static T Field<T>(MainWindow window, string name) =>
        (T)typeof(MainWindow).GetField(name, AnyInstance)!.GetValue(window)!;
    private static T Control<T>(MainWindow window, string name) where T : Control =>
        window.FindControl<T>(name)!;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static void Run()
    {
        PlayStartUpdateStop();
        RePlayKeepsAuthoring();
        InspectorErrorBlocksPlay();
        StartFailureCleansUp();
        UpdateFailureAutoStops();
        CloseDuringPlayCleansUp();
        CleanupErrorsRemainVisible(false);
        CleanupErrorsRemainVisible(true);
        Console.WriteLine("PASS: editor Play/Stop wiring, replay separation, guards, and cleanup.");
    }

    private static void EnsureChecks(MainWindow editor)
    {
        // A4: 各ウィンドウの所有者に明示登録する。
        var components = Field<ProjectComponents>(editor, "_components");
        components.Registry.Register<PlayCounter>("checks.play-counter");
        components.Registry.Register<PlayBadLifecycle>("checks.play-bad");
        components.Registry.Register<PlayFailUpdate>("checks.play-fail-update");
        components.Registry.Register<PlayFailCleanup>("checks.play-fail-cleanup");
    }

    private static MainWindow CreateEditor()
    {
        var editor = new MainWindow();
        EnsureChecks(editor);
        editor.Show();
        Dispatcher.UIThread.RunJobs();
        return editor;
    }

    private static void CloseEditor(MainWindow editor)
    {
        // Inspector検証で付けたDirtyを落として閉じる。確認ダイアログが出たらDiscardする。
        typeof(MainWindow).GetField("_sceneDirty", AnyInstance)!.SetValue(editor, false);
        editor.Close();
        Dispatcher.UIThread.RunJobs();
        var dialog = editor.OwnedWindows.SingleOrDefault(window => window.Title == "Unsaved Scene");
        if (dialog is not null)
        {
            dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Discard"))
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void StopTimer(MainWindow editor)
    {
        // タイマーの実Tickを止め、手動Stepで決定的に検証する。配線自体はIsEnabledで確認する。
        var timer = Field<DispatcherTimer?>(editor, "_playTimer");
        timer?.Stop();
        Dispatcher.UIThread.RunJobs();
    }

    private static void PlayStartUpdateStop()
    {
        PlayCounter.Reset();
        var editor = CreateEditor();
        try
        {
            var scene = Field<Scene>(editor, "_scene");
            var services = Field<GameSession>(editor, "_editSession");
            var owner = Field<ProjectComponents>(editor, "_components");
            var item = scene.AddEmpty();
            item.Rename("Player");
            owner.TryAttach(item, typeof(PlayCounter), services.Factory);
            var edit = item.GetComponent<PlayCounter>()!;
            edit.Value = 10;
            Dispatcher.UIThread.RunJobs();

            var playButton = Control<Button>(editor, "PlayButton");
            var stopButton = Control<Button>(editor, "StopButton");
            var status = Control<TextBlock>(editor, "FileStatus");
            Check(playButton.IsEnabled && !stopButton.IsEnabled, "Idle must enable Play and disable Stop.");

            Call(editor, "StartPlay");
            Dispatcher.UIThread.RunJobs();
            Check((bool)Call(editor, "get_IsPlaying")!, "StartPlay must enter playing state.");
            Check(!playButton.IsEnabled && stopButton.IsEnabled, "Playing must enable Stop and disable Play.");
            Check(!Control<ListBox>(editor, "SceneObjects").IsEnabled, "Playing must disable scene editing.");
            Check(!Control<TreeView>(editor, "ProjectTree").IsEnabled, "Playing must disable scene switching.");
            Check(status.Text?.Contains("Playを開始") == true, "Play start must report status.");
            var timer = Field<DispatcherTimer?>(editor, "_playTimer");
            Check(timer is not null && timer.IsEnabled, "Play must drive Step on a timer.");
            StopTimer(editor);

            var play = Field<PlaySession?>(editor, "_play")!;
            Check(play is not null && play.Runtime.IsRunning, "Play runtime must be running.");
            Check(PlayCounter.Starts == 1, $"Start must run once, got {PlayCounter.Starts}.");
            Call(editor, "StepPlayOnce", 1f / 60f);
            Call(editor, "StepPlayOnce", 1f / 60f);
            Call(editor, "StepPlayOnce", 1f / 60f);
            Check(PlayCounter.Updates == 3, $"Update must continue, got {PlayCounter.Updates}.");
            Check(edit.Value == 10, "Running must not mutate the authoring scene.");

            Call(editor, "StopPlay");
            Dispatcher.UIThread.RunJobs();
            Check(!(bool)Call(editor, "get_IsPlaying")!, "StopPlay must leave playing state.");
            Check(PlayCounter.Destroys == 1, $"Stop must destroy once, got {PlayCounter.Destroys}.");
            Check(playButton.IsEnabled && !stopButton.IsEnabled, "Stop must restore Play/Stop buttons.");
            Check(Control<ListBox>(editor, "SceneObjects").IsEnabled, "Stop must restore scene editing.");
            Check(status.Text?.Contains("Playを停止") == true, "Stop must report status.");
            var frozen = PlayCounter.Updates;
            Dispatcher.UIThread.RunJobs();
            Check(PlayCounter.Updates == frozen, "Update must stop after Stop.");

            // 二重停止はno-opで操作可能な状態を保つ。
            Call(editor, "StopPlay");
            Dispatcher.UIThread.RunJobs();
            Check(PlayCounter.Destroys == 1, "Double stop must not destroy twice.");
            Check(playButton.IsEnabled, "Double stop must stay operable.");
            CloseEditor(editor);
        }
        finally
        {
            if (editor.IsVisible) CloseEditor(editor);
        }
    }

    private static void RePlayKeepsAuthoring()
    {
        PlayCounter.Reset();
        var editor = CreateEditor();
        try
        {
            var scene = Field<Scene>(editor, "_scene");
            var services = Field<GameSession>(editor, "_editSession");
            var item = scene.AddEmpty();
            Field<ProjectComponents>(editor, "_components").TryAttach(item, typeof(PlayCounter), services.Factory);
            var edit = item.GetComponent<PlayCounter>()!;
            edit.Value = 41;
            var countBefore = scene.Objects.Count;
            Dispatcher.UIThread.RunJobs();

            Call(editor, "StartPlay");
            StopTimer(editor);
            var first = Field<PlaySession?>(editor, "_play")!;
            Call(editor, "StepPlayOnce", 1f / 60f);
            Call(editor, "StopPlay");
            Check(PlayCounter.Starts == 1 && PlayCounter.Destroys == 1, "First play must start and destroy once.");
            Check(edit.Value == 41 && scene.Objects.Count == countBefore, "Authoring scene must keep pre-play state.");

            Call(editor, "StartPlay");
            StopTimer(editor);
            var second = Field<PlaySession?>(editor, "_play")!;
            Check(!ReferenceEquals(first, second) && !ReferenceEquals(first.Runtime, second.Runtime),
                "Replay must create a new SceneRuntime.");
            Call(editor, "StepPlayOnce", 1f / 60f);
            Check(PlayCounter.Starts == 2, "Replay must start from the beginning on a new instance.");
            Check(edit.Value == 41, "Replay must still not mutate the authoring scene.");
            Call(editor, "StopPlay");
            Check(PlayCounter.Destroys == 2, "Replay stop must destroy its own instance once.");
            CloseEditor(editor);
        }
        finally
        {
            if (editor.IsVisible) CloseEditor(editor);
        }
    }

    private static void InspectorErrorBlocksPlay()
    {
        PlayCounter.Reset();
        var editor = CreateEditor();
        try
        {
            var scene = Field<Scene>(editor, "_scene");
            var services = Field<GameSession>(editor, "_editSession");
            var item = scene.AddEmpty();
            Field<ProjectComponents>(editor, "_components").TryAttach(item, typeof(PlayCounter), services.Factory);
            Dispatcher.UIThread.RunJobs();
            editor.FindControl<ListBox>("SceneObjects")!.SelectedItem = item;
            Dispatcher.UIThread.RunJobs();
            var box = editor.GetVisualDescendants().OfType<TextBox>()
                .First(b => Equals(b.GetValue(Avalonia.Automation.AutomationProperties.NameProperty) as string,
                    "PlayCounter.Value"));
            box.Text = "abc";
            Dispatcher.UIThread.RunJobs();

            var status = Control<TextBlock>(editor, "FileStatus");
            Call(editor, "StartPlay");
            Dispatcher.UIThread.RunJobs();
            Check(!(bool)Call(editor, "get_IsPlaying")!, "Play must not start with Inspector errors.");
            Check(status.Text?.Contains("Inspector") == true, "Blocked Play must explain the reason.");
            Check(PlayCounter.Starts == 0, "Blocked Play must not call Start.");
            Check(Control<Button>(editor, "PlayButton").IsEnabled, "Blocked Play must stay operable.");

            box.Text = "7";
            Dispatcher.UIThread.RunJobs();
            Call(editor, "StartPlay");
            StopTimer(editor);
            Check((bool)Call(editor, "get_IsPlaying")!, "Fixed input must allow Play.");
            Call(editor, "StopPlay");
            CloseEditor(editor);
        }
        finally
        {
            if (editor.IsVisible) CloseEditor(editor);
        }
    }

    private static void StartFailureCleansUp()
    {
        PlayCounter.Reset();
        var editor = CreateEditor();
        try
        {
            var scene = Field<Scene>(editor, "_scene");
            var item = scene.AddEmpty();
            // 不正なライフサイクル宣言で準備失敗させる。編集時のAttach自体は通る。
            item.Attach(new PlayBadLifecycle());
            Dispatcher.UIThread.RunJobs();

            var status = Control<TextBlock>(editor, "FileStatus");
            Call(editor, "StartPlay");
            Dispatcher.UIThread.RunJobs();
            Check(!(bool)Call(editor, "get_IsPlaying")!, "Failed start must not stay playing.");
            Check(Field<PlaySession?>(editor, "_play") is null, "Failed start must release the session.");
            Check(status.Text?.Contains("Playを開始できません") == true, "Failed start must report the reason.");
            Check(Control<Button>(editor, "PlayButton").IsEnabled, "Failed start must restore buttons.");
            Check(Control<ListBox>(editor, "SceneObjects").IsEnabled, "Failed start must restore editing.");
            Check(PlayCounter.Starts == 0, "Failed start must not run other components.");
            CloseEditor(editor);
        }
        finally
        {
            if (editor.IsVisible) CloseEditor(editor);
        }
    }

    private static void UpdateFailureAutoStops()
    {
        PlayFailUpdate.Reset();
        var editor = CreateEditor();
        try
        {
            var scene = Field<Scene>(editor, "_scene");
            var services = Field<GameSession>(editor, "_editSession");
            var item = scene.AddEmpty();
            Field<ProjectComponents>(editor, "_components").TryAttach(item, typeof(PlayFailUpdate), services.Factory);
            Dispatcher.UIThread.RunJobs();

            Call(editor, "StartPlay");
            StopTimer(editor);
            Check((bool)Call(editor, "get_IsPlaying")!, "Update-failure test requires a running play.");
            Call(editor, "StepPlayOnce", 1f / 60f);
            Dispatcher.UIThread.RunJobs();
            Check(!(bool)Call(editor, "get_IsPlaying")!, "Update failure must auto-stop.");
            Check(Field<PlaySession?>(editor, "_play") is null, "Auto-stop must release the session.");
            var status = Control<TextBlock>(editor, "FileStatus");
            Check(status.Text?.Contains("boom") == true, "Update failure must report runtime errors.");
            Check(PlayFailUpdate.Destroys == 1, "Auto-stop must destroy once.");
            Check(Control<Button>(editor, "PlayButton").IsEnabled, "Auto-stop must restore buttons.");
            Check(Control<ListBox>(editor, "SceneObjects").IsEnabled, "Auto-stop must restore editing.");
            CloseEditor(editor);
        }
        finally
        {
            if (editor.IsVisible) CloseEditor(editor);
        }
    }

    private static void CloseDuringPlayCleansUp()
    {
        PlayCounter.Reset();
        var editor = CreateEditor();
        var scene = Field<Scene>(editor, "_scene");
        var services = Field<GameSession>(editor, "_editSession");
        var item = scene.AddEmpty();
        Field<ProjectComponents>(editor, "_components").TryAttach(item, typeof(PlayCounter), services.Factory);
        Dispatcher.UIThread.RunJobs();

        Call(editor, "StartPlay");
        StopTimer(editor);
        Call(editor, "StepPlayOnce", 1f / 60f);
        Check((bool)Call(editor, "get_IsPlaying")!, "Close test requires a running play.");
        editor.Close();
        Dispatcher.UIThread.RunJobs();
        Check(!editor.IsVisible, "Editor must close during play.");
        Check(!(bool)Call(editor, "get_IsPlaying")!, "Close must terminate the running play.");
        Check(PlayCounter.Destroys == 1, "Close during play must destroy once.");
        Check(PlayCounter.Updates == 1, "Close during play must stop updates.");
    }

    private static void CleanupErrorsRemainVisible(bool close)
    {
        PlayFailCleanup.Disposals = 0;
        var editor = CreateEditor();
        try
        {
            var scene = Field<Scene>(editor, "_scene");
            scene.AddEmpty().Attach(new PlayFailCleanup());
            Call(editor, "StartPlay");
            StopTimer(editor);
            if (close) editor.Close();
            else Call(editor, "StopPlay");
            Dispatcher.UIThread.RunJobs();

            Check(editor.IsVisible, "Cleanup errors must remain visible, including on window close.");
            Check(!editor.IsPlaying && Control<Button>(editor, "PlayButton").IsEnabled,
                "Cleanup failure must still stop Play and restore controls.");
            var status = Control<TextBlock>(editor, "FileStatus");
            var detail = ToolTip.GetTip(status)?.ToString() ?? "";
            Check(detail.Contains("destroy failure") && detail.Contains("dispose failure"),
                "Both termination errors must be available in the status tooltip.");
            Check(detail.Contains(nameof(PlayFailCleanup.End)) && detail.Contains("Dispose"),
                "Termination errors must include their source methods.");
            Check(PlayFailCleanup.Disposals == 1, "Runtime cleanup must run once.");
            editor.Close();
            Dispatcher.UIThread.RunJobs();
            Check(!editor.IsVisible, "Closing again after inspecting the errors must succeed.");
        }
        finally
        {
            if (editor.IsVisible) CloseEditor(editor);
        }
    }

    public sealed class PlayFailCleanup : IDisposable
    {
        public static int Disposals;
        private bool _started;
        [Start] public void Begin() => _started = true;
        [Destroy] public void End() => throw new ApplicationException("destroy failure");
        public void Dispose()
        {
            if (!_started) return;
            Disposals++;
            throw new ApplicationException("dispose failure");
        }
    }

    public sealed class PlayCounter
    {
        public static int Starts, Updates, Destroys;
        public static void Reset() => Starts = Updates = Destroys = 0;
        [Inspector] public int Value { get; set; } = 10;
        [Start] private void Begin() => Starts++;
        [Update] private void Tick(float dt) { Updates++; Value++; }
        [Destroy] private void End() => Destroys++;
    }

    public sealed class PlayBadLifecycle
    {
        [Update] public static void Tick() { }
    }

    public sealed class PlayFailUpdate
    {
        public static int Destroys;
        public static void Reset() => Destroys = 0;
        [Update] private void Tick() => throw new ApplicationException("update boom");
        [Destroy] private void End() => Destroys++;
    }
}
