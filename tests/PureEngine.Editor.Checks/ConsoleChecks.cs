using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PureEngine.Core;
using PureEngine.Core.Attributes;
using PureEngine.Editor;

static class ConsoleChecks
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
        ComponentAssets.Registry.Register<ConsoleProbe>("checks.console-probe");
        ComponentAssets.Registry.Register<ConsoleFailUpdate>("checks.console-fail-update");
        ComponentAssets.Registry.Register<ConsoleFailCleanup>("checks.console-fail-cleanup");
        ComponentAssets.Registry.Register<ConsoleFailCleanupB>("checks.console-fail-cleanup-b");

        BasicDisplay();
        FiltersSearchAndSelection();
        EngineSourceFilter();
        ScrollPositionSurvivesIntake();
        SearchDoesNotSnapToTail();
        ClearAndClearOnPlay();
        PlayErrorsDedup();
        RemovalErrorsAppearWhilePlaying();
        CloseReopen();
        Console.WriteLine("PASS: console list/detail, filters/search, clear/clear-on-play, play errors dedup, and close/reopen.");
    }

    private static MainWindow CreateEditor()
    {
        _ = Log.Drain();
        var editor = new MainWindow();
        editor.Show();
        Dispatcher.UIThread.RunJobs();
        // Deterministic: stop play timer, drain console manually.
        var playTimer = Field<DispatcherTimer?>(editor, "_playTimer");
        playTimer?.Stop();
        Call(editor, "ClearConsole");
        _ = Log.Drain();
        Dispatcher.UIThread.RunJobs();
        return editor;
    }

    private static void CloseEditor(MainWindow editor)
    {
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
        _ = Log.Drain();
    }

    private static void Drain(MainWindow editor)
    {
        Call(editor, "DrainConsole");
        Dispatcher.UIThread.RunJobs();
    }

    private static IReadOnlyList<ConsoleRow> View(MainWindow editor)
    {
        var list = Control<ListBox>(editor, "ConsoleList");
        return (list.ItemsSource as IEnumerable<ConsoleRow>)?.ToArray() ?? [];
    }

    private static void BasicDisplay()
    {
        var editor = CreateEditor();
        try
        {
            Log.Info("hello-info");
            Log.Warning("hello-warn");
            var failure = new InvalidOperationException("outer", new ArgumentException("inner"));
            try { throw failure; } catch (Exception error) { Log.Error("hello-error", error); }
            Drain(editor);
            var history = Field<List<LogEntry>>(editor, "_consoleHistory");
            Check(history.Count == 3, $"History must hold 3, got {history.Count}.");
            Check(history[0].Level == LogLevel.Info && history[1].Level == LogLevel.Warning && history[2].Level == LogLevel.Error,
                "Levels must be Info/Warning/Error.");
            foreach (var entry in history)
            {
                Check(entry.Timestamp != default, "Time must be kept.");
                Check(!string.IsNullOrEmpty(entry.FilePath) && entry.LineNumber > 0 && !string.IsNullOrEmpty(entry.MemberName),
                    "Caller file/line/member must be kept.");
                Check(entry.MemberName == nameof(BasicDisplay), $"Caller must be {nameof(BasicDisplay)}, got {entry.MemberName}.");
            }
            Check(history[0].ExceptionDetail is null && history[1].ExceptionDetail is null, "Normal logs must not take a stack.");
            var errorDetail = history[2].ExceptionDetail ?? "";
            Check(errorDetail.Contains("outer") && errorDetail.Contains("inner")
                && errorDetail.Contains(nameof(BasicDisplay)), "Exception must keep inner/stack detail.");

            var rows = View(editor);
            Check(rows.Count == 3, $"List must show time/kind/head for 3, got {rows.Count}.");
            Check(rows[0].LevelText == "Info" && rows[2].LevelText == "Error", "Kind column wrong.");
            Check(rows[0].Head.Contains("hello-info"), "Head must show body start.");
            Check(!string.IsNullOrEmpty(rows[0].TimeText), "Time column must show.");

            // Counts per level.
            Check(Control<TextBlock>(editor, "ConsoleInfoCount").Text == "1", "Info count wrong.");
            Check(Control<TextBlock>(editor, "ConsoleWarningCount").Text == "1", "Warning count wrong.");
            Check(Control<TextBlock>(editor, "ConsoleErrorCount").Text == "1", "Error count wrong.");

            // Select a row to see full body, location, and exception detail, then copy (must not throw headless).
            var list = Control<ListBox>(editor, "ConsoleList");
            list.SelectedItem = rows[2];
            Dispatcher.UIThread.RunJobs();
            var detail = Control<TextBox>(editor, "ConsoleDetail").Text ?? "";
            Check(detail.Contains("hello-error"), "Detail must show full body.");
            Check(detail.Contains(history[2].FilePath) && detail.Contains(history[2].LineNumber.ToString()),
                "Detail must show the record location.");
            Check(detail.Contains("outer") && detail.Contains("inner"), "Detail must show exception detail.");
            Call(editor, "OnConsoleCopy", null, new Avalonia.Interactivity.RoutedEventArgs());
            Dispatcher.UIThread.RunJobs();

            // Hidden tab still receives: switch to Project, log, drain, then back.
            Control<TabItem>(editor, "ProjectTab").IsSelected = true;
            Dispatcher.UIThread.RunJobs();
            Log.Info("hidden-tab");
            Drain(editor);
            Check(Field<List<LogEntry>>(editor, "_consoleHistory").Count == 4, "Hidden tab must still receive logs.");
            CloseEditor(editor);
        }
        finally
        {
            if (editor.IsVisible) CloseEditor(editor);
        }
    }

    private static void FiltersSearchAndSelection()
    {
        var editor = CreateEditor();
        try
        {
            Log.Info("alpha one");
            Log.Warning("beta two");
            Log.Error("gamma three");
            Log.Error("alpha error");
            Drain(editor);
            Check(View(editor).Count == 4, "All must show before filtering.");

            // Info off hides Info only, counts stay, re-enabling restores retained logs.
            Control<CheckBox>(editor, "ConsoleInfoFilter").IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            var noInfo = View(editor);
            Check(noInfo.Count == 3 && noInfo.All(row => row.Entry.Level != LogLevel.Info), "Info filter must hide only Info.");
            Check(Control<TextBlock>(editor, "ConsoleInfoCount").Text == "1", "Counts must stay for hidden levels.");
            Control<CheckBox>(editor, "ConsoleInfoFilter").IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Check(View(editor).Count == 4, "Filter toggle must re-show retained logs.");

            // Error off.
            Control<CheckBox>(editor, "ConsoleErrorFilter").IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Check(View(editor).Count == 2, "Error filter must hide errors.");
            Control<CheckBox>(editor, "ConsoleErrorFilter").IsChecked = true;
            Dispatcher.UIThread.RunJobs();

            // Search filters display only.
            Control<TextBox>(editor, "ConsoleSearch").Text = "alpha";
            Dispatcher.UIThread.RunJobs();
            var searched = View(editor);
            Check(searched.Count == 2 && searched.All(row => (row.Entry.Message + row.Entry.ExceptionDetail).Contains("alpha", StringComparison.OrdinalIgnoreCase)),
                "Search must filter the body.");
            Control<TextBox>(editor, "ConsoleSearch").Text = "";
            Dispatcher.UIThread.RunJobs();
            Check(View(editor).Count == 4, "Clearing search must re-show retained logs.");

            // Auto-scroll must not steal the position while reading past logs.
            var list = Control<ListBox>(editor, "ConsoleList");
            var rows = View(editor);
            list.SelectedItem = rows[0];
            Dispatcher.UIThread.RunJobs();
            var first = (list.SelectedItem as ConsoleRow)?.Entry;
            Log.Info("newest-tail");
            Drain(editor);
            Check(ReferenceEquals((list.SelectedItem as ConsoleRow)?.Entry, first), "Older selection must be kept when new logs arrive.");
            Check(Control<TextBox>(editor, "ConsoleDetail").Text?.Contains("alpha one") == true,
                "Detail of the older selection must be kept.");

            // Batch intake: many logs in one drain.
            for (var i = 0; i < 300; i++) Log.Info($"batch-{i}");
            Drain(editor);
            Check(Field<List<LogEntry>>(editor, "_consoleHistory").Count == 4 + 1 + 300, "Batched intake must take all pending at once.");

            // History cap: oldest retained entries are dropped and counted, queue drops are shown separately.
            // 1050 logs at once: queue keeps 1000 (drops 50), then history keeps 1000 newest.
            for (var i = 0; i < MainWindow.ConsoleMaxHistory + 50; i++) Log.Info($"history-cap-{i}");
            Drain(editor);
            var capped = Field<List<LogEntry>>(editor, "_consoleHistory");
            Check(capped.Count == MainWindow.ConsoleMaxHistory, $"History must cap at {MainWindow.ConsoleMaxHistory}, got {capped.Count}.");
            Check(Field<int>(editor, "_consoleHistoryDropped") == 305,
                $"History drops must be 305, got {Field<int>(editor, "_consoleHistoryDropped")}.");
            Check(capped[^1].Message == $"history-cap-{MainWindow.ConsoleMaxHistory + 49}", "History must keep newest.");
            var droppedText = Control<TextBlock>(editor, "ConsoleDropped").Text ?? "";
            Check(droppedText.Contains("履歴") && droppedText.Contains("キュー"), "Dropped counts for queue/history must be shown.");
            CloseEditor(editor);
        }
        finally
        {
            if (editor.IsVisible) CloseEditor(editor);
        }
    }

    private static void EngineSourceFilter()
    {
        var editor = CreateEditor();
        try
        {
            Log.Info("shared game");
            Log.Engine.Info("shared engine");
            Log.Engine.Error("shared engine error", new Exception("engine detail"));
            Drain(editor);
            var rows = View(editor);
            Check(rows.Count == 3 && rows[0].SourceText == "Game" && rows[1].SourceText == "Engine",
                "Rows must show the source separately from severity.");
            Check(rows[1].DetailText.StartsWith("[Engine][Info]")
                && rows[2].DetailText.StartsWith("[Engine][Error]")
                && rows[2].DetailText.Contains("engine detail"), "Detail/copy must include source and exception.");
            var filter = Control<CheckBox>(editor, "ConsoleEngineFilter");
            Check(filter.IsChecked == true, "Engine logs must be visible by default.");
            filter.IsChecked = false;
            Log.Engine.Warning("shared hidden engine");
            Drain(editor);
            Check(View(editor).Count == 1 && View(editor)[0].Entry.Source == LogSource.Game,
                "Engine filter must hide all engine levels, including newly received logs.");
            Check(Field<List<LogEntry>>(editor, "_consoleHistory").Count == 4
                && Control<TextBlock>(editor, "ConsoleErrorCount").Text == "1",
                "Source filtering must retain history and severity totals.");
            filter.IsChecked = true;
            Check(View(editor).Count == 4, "Re-enabling Engine must restore retained entries.");
            Control<CheckBox>(editor, "ConsoleInfoFilter").IsChecked = false;
            Control<TextBox>(editor, "ConsoleSearch").Text = "hidden";
            Dispatcher.UIThread.RunJobs();
            Check(View(editor).Count == 1 && View(editor)[0].Entry.Level == LogLevel.Warning,
                "Source, severity, and search filters must combine.");
        }
        finally { CloseEditor(editor); }
    }

    private static void ScrollPositionSurvivesIntake()
    {
        var editor = CreateEditor();
        try
        {
            Field<DispatcherTimer>(editor, "_consoleTimer").Stop();
            var tab = editor.GetLogicalDescendants().OfType<TabItem>()
                .Single(tab => Equals(tab.Header, "Console"));
            tab.IsSelected = true;
            // Give the list a usable viewport, independent of the user's pane layout.
            tab.GetLogicalAncestors().OfType<Grid>().First(grid => grid.RowDefinitions.Count == 3)
                .RowDefinitions[2].Height = new GridLength(400);
            for (var i = 0; i < 150; i++) Log.Info($"scroll-{i}");
            Drain(editor);
            var list = Control<ListBox>(editor, "ConsoleList");
            var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            Check(scroll.Viewport.Height > 0, "Scroll test requires a visible list viewport.");
            Check(scroll.Extent.Height > scroll.Viewport.Height, "Scroll test requires overflowing content.");
            foreach (var position in new[] { 0d, 200d })
            {
                scroll.Offset = new Vector(0, position);
                Dispatcher.UIThread.RunJobs();
                Check(list.SelectedItem is null, "Scrolling must not require a selection.");
                Log.Info("arrived while reading");
                Drain(editor);
                Check(Math.Abs(scroll.Offset.Y - position) < 1,
                    $"Intake moved the viewport from {position} to {scroll.Offset.Y}.");
            }
            // Following the tail is independent of which entry is selected.
            list.SelectedItem = View(editor)[0];
            Dispatcher.UIThread.RunJobs();
            scroll.ScrollToEnd();
            Dispatcher.UIThread.RunJobs();
            Log.Info("follow the new tail");
            Drain(editor);
            Check(scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 1,
                "Intake must follow the tail when the viewport was at the bottom.");
        }
        finally { CloseEditor(editor); }
    }

    private static void SearchDoesNotSnapToTail()
    {
        var editor = CreateEditor();
        try
        {
            Field<DispatcherTimer>(editor, "_consoleTimer").Stop();
            var tab = editor.GetLogicalDescendants().OfType<TabItem>()
                .Single(tab => Equals(tab.Header, "Console"));
            tab.IsSelected = true;
            tab.GetLogicalAncestors().OfType<Grid>().First(grid => grid.RowDefinitions.Count == 3)
                .RowDefinitions[2].Height = new GridLength(400);
            // Enough entries to overflow the viewport so scrolling is meaningful.
            for (var i = 0; i < 80; i++) Log.Info($"entry-{i:00}");
            Drain(editor);
            var list = Control<ListBox>(editor, "ConsoleList");
            var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            Check(scroll.Viewport.Height > 0, "Search test requires a visible list viewport.");
            Check(scroll.Extent.Height > scroll.Viewport.Height, "Search test requires overflowing content.");
            // Pin near the top: an earlier entry should remain visible while typing a search
            // that filters out the tail. Restoring the offset must not fall through to the tail.
            scroll.Offset = new Vector(0, 0);
            Dispatcher.UIThread.RunJobs();
            var search = Control<TextBox>(editor, "ConsoleSearch");
            search.Text = "entry-00";
            Dispatcher.UIThread.RunJobs();
            Check(View(editor).Count == 1, "Search must leave exactly one entry.");
            Check(scroll.Offset.Y <= 1, "A single search result must be at the top.");
            // Clearing the search restores the full list without jumping away from the top.
            search.Text = "";
            Dispatcher.UIThread.RunJobs();
            Check(View(editor).Count == 80, "Clearing search must restore all entries.");
            Check(scroll.Extent.Height > scroll.Viewport.Height, "Restored list must overflow.");
            Check(scroll.Offset.Y <= 1, $"Clearing search must not scroll; got {scroll.Offset.Y}.");
            // A no-match search and a level filter must also restore the list at the top.
            search.Text = "no-match";
            Dispatcher.UIThread.RunJobs();
            Check(View(editor).Count == 0, "Unmatched search must be empty.");
            search.Text = "";
            Dispatcher.UIThread.RunJobs();
            Check(View(editor).Count == 80 && scroll.Offset.Y <= 1,
                "Clearing an unmatched search must restore entries at the top.");
            var info = Control<CheckBox>(editor, "ConsoleInfoFilter");
            info.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Check(View(editor).Count == 0, "Disabling Info must hide the entries.");
            info.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Check(View(editor).Count == 80 && scroll.Offset.Y <= 1,
                "Re-enabling Info must restore entries at the top.");
            scroll.Offset = new Vector(0, 200);
            Dispatcher.UIThread.RunJobs();
            search.Text = "entry";
            Dispatcher.UIThread.RunJobs();
            Check(View(editor).Count == 80 && Math.Abs(scroll.Offset.Y - 200) < 1,
                $"Search with matching entries must preserve the offset; got {scroll.Offset.Y}.");
            CloseEditor(editor);
        }
        finally { if (editor.IsVisible) CloseEditor(editor); }
    }

    private static void RemovalErrorsAppearWhilePlaying()
    {
        var editor = CreateEditor();
        try
        {
            Field<Scene>(editor, "_scene").AddEmpty().Attach(new ConsoleFailCleanup());
            Call(editor, "StartPlay");
            Field<DispatcherTimer>(editor, "_playTimer").Stop();
            var play = Field<PlaySession>(editor, "_play");
            play.Runtime.Scene.Remove(play.Runtime.Scene.Objects[0]);
            Call(editor, "StepPlayOnce", 1f / 60f);
            Drain(editor);
            Check(play.Runtime.IsRunning, "Removing an object with a cleanup error must allow execution to continue.");
            int Reports() => Field<List<LogEntry>>(editor, "_consoleHistory")
                .Count(entry => entry.ExceptionDetail?.Contains("destroy failure A") == true);
            Check(Reports() == 1, "Removal error must reach Console before Stop.");
            Call(editor, "StepPlayOnce", 1f / 60f);
            Call(editor, "StopPlay");
            Drain(editor);
            Check(Reports() == 1, "Later Step/Stop must not repeat the removal error.");
        }
        finally { CloseEditor(editor); }
    }

    private static void ClearAndClearOnPlay()
    {
        var editor = CreateEditor();
        try
        {
            // Clear button clears history/view.
            Log.Info("to-clear");
            Drain(editor);
            Check(Field<List<LogEntry>>(editor, "_consoleHistory").Count == 1, "Setup log missing.");
            Control<Button>(editor, "ConsoleClear").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Check(Field<List<LogEntry>>(editor, "_consoleHistory").Count == 0 && View(editor).Count == 0, "Clear must empty history/view.");
            Check(Field<int>(editor, "_consoleHistoryDropped") == 0, "Clear must reset history drops.");

            // Clear on Play is ON by default and runs before Start without erasing the start log.
            Check(Control<CheckBox>(editor, "ConsoleClearOnPlay").IsChecked == true, "Clear on Play must default to ON.");
            Log.Info("before-play");
            Drain(editor);
            Check(Field<List<LogEntry>>(editor, "_consoleHistory").Count == 1, "Pre-play log missing.");
            Call(editor, "StartPlay");
            var playTimer = Field<DispatcherTimer?>(editor, "_playTimer");
            playTimer?.Stop();
            Drain(editor);
            var afterStart = Field<List<LogEntry>>(editor, "_consoleHistory");
            Check(afterStart.Count == 1 && afterStart[0].Source == LogSource.Engine
                && afterStart[0].Message.Contains("Playの開始処理が完了"), $"Clear on Play must clear before Start and keep the engine start log, got {afterStart.Count}.");
            Call(editor, "StopPlay");
            Drain(editor);
            // OFF keeps old logs across Play.
            Control<CheckBox>(editor, "ConsoleClearOnPlay").IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Log.Info("keep-me");
            Drain(editor);
            var before = Field<List<LogEntry>>(editor, "_consoleHistory").Count;
            Call(editor, "StartPlay");
            Field<DispatcherTimer?>(editor, "_playTimer")?.Stop();
            Drain(editor);
            Call(editor, "StopPlay");
            Drain(editor);
            var after = Field<List<LogEntry>>(editor, "_consoleHistory");
            Check(after.Count > before && after.Any(entry => entry.Message.Contains("keep-me")),
                "Clear on Play OFF must keep previous logs.");
            CloseEditor(editor);
        }
        finally
        {
            if (editor.IsVisible) CloseEditor(editor);
        }
    }

    private static void PlayErrorsDedup()
    {
        var editor = CreateEditor();
        try
        {
            var scene = Field<Scene>(editor, "_scene");
            var item = scene.AddEmpty();
            item.Rename("Failer");
            item.Attach(new ConsoleFailCleanup());
            var itemB = scene.AddEmpty();
            itemB.Rename("FailerB");
            itemB.Attach(new ConsoleFailCleanupB());
            Dispatcher.UIThread.RunJobs();

            Control<CheckBox>(editor, "ConsoleClearOnPlay").IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Call(editor, "StartPlay");
            Field<DispatcherTimer?>(editor, "_playTimer")?.Stop();
            Drain(editor);
            // Start itself succeeds; failures surface at Stop.
            Check((bool)Call(editor, "get_IsPlaying")!, "Fail-cleanup test requires playing.");
            var statusBefore = Control<TextBlock>(editor, "FileStatus").Text ?? "";
            Check(!statusBefore.Contains("Failer"), "Stop errors must not appear before Stop.");
            Call(editor, "StopPlay");
            Drain(editor);
            Dispatcher.UIThread.RunJobs();

            var history = Field<List<LogEntry>>(editor, "_consoleHistory");
            var runtimeLogs = history.Where(entry => entry.Message.Contains("Failer")).ToArray();
            // Two objects: each Destroy fails (2 runtime errors) plus stop summary.
            Check(runtimeLogs.Length >= 2, $"All termination errors must be taken, got {runtimeLogs.Length}.");
            Check(history.Any(entry => entry.Message.Contains("Failer/ConsoleFailCleanup.End")),
                "Object name/type/lifecycle must remain.");
            Check(history.Any(entry => entry.Message.Contains("FailerB/ConsoleFailCleanupB.End")),
                "Second object's error must also be taken.");
            foreach (var log in runtimeLogs)
                Check(!string.IsNullOrEmpty(log.ExceptionDetail) && log.ExceptionDetail.Contains("destroy failure"),
                    "Runtime location must use the exception side, not the forwarding line.");
            var status = Control<TextBlock>(editor, "FileStatus").Text ?? "";
            Check(status.Contains("Failer"), "Existing status display must remain.");

            // Stop/Dispose must not duplicate the same errors.
            var countAfterStop = history.Count;
            Call(editor, "StopPlay");
            Drain(editor);
            Check(Field<List<LogEntry>>(editor, "_consoleHistory").Count == countAfterStop, "Double stop must not duplicate errors.");

            // Stop後もログを読める.
            var list = Control<ListBox>(editor, "ConsoleList");
            var rows = View(editor);
            Check(rows.Count > 0, "Logs must remain readable after Stop.");
            list.SelectedItem = rows.First(row => row.Entry.Message.Contains("Failer"));
            Dispatcher.UIThread.RunJobs();
            Check(Control<TextBox>(editor, "ConsoleDetail").Text?.Contains("destroy failure") == true,
                "Post-stop detail must show exception.");

            // Re-Play starts from the new session without carrying the old error count.
            scene.Remove(item);
            scene.Remove(itemB);
            var ok = scene.AddEmpty();
            ok.Attach(new ConsoleProbe());
            Dispatcher.UIThread.RunJobs();
            Call(editor, "StartPlay");
            Field<DispatcherTimer?>(editor, "_playTimer")?.Stop();
            Drain(editor);
            Check((bool)Call(editor, "get_IsPlaying")!, "Re-Play must start.");
            Call(editor, "StopPlay");
            Drain(editor);
            Check(!(bool)Call(editor, "get_IsPlaying")!, "Re-Play must stop.");
            CloseEditor(editor);
        }
        finally
        {
            if (editor.IsVisible) CloseEditor(editor);
        }

        // Update failure auto-stop reports once; Step/Stop/Dispose do not duplicate.
        var auto = CreateEditor();
        try
        {
            var scene = Field<Scene>(auto, "_scene");
            var services = Field<GameSession>(auto, "_editSession");
            var item = scene.AddEmpty();
            ComponentAssets.TryAttach(item, typeof(ConsoleFailUpdate), services.Factory);
            Dispatcher.UIThread.RunJobs();
            Control<CheckBox>(auto, "ConsoleClearOnPlay").IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Call(auto, "StartPlay");
            Field<DispatcherTimer?>(auto, "_playTimer")?.Stop();
            Drain(auto);
            Call(auto, "StepPlayOnce", 1f / 60f);
            Drain(auto);
            Dispatcher.UIThread.RunJobs();
            Check(!(bool)Call(auto, "get_IsPlaying")!, "Update failure must auto-stop.");
            var history = Field<List<LogEntry>>(auto, "_consoleHistory");
            var updateLogs = history.Where(entry => entry.Message.Contains("ConsoleFailUpdate")).ToArray();
            Check(updateLogs.Length == 1, $"Update error must appear once, got {updateLogs.Length}.");
            Check(history.Any(entry => entry.Message.Contains("更新に失敗") || entry.Message.Contains("エラーで停止")),
                "Update/auto-stop summary must be in Console.");
            var frozen = history.Count;
            Call(auto, "StopPlay");
            Drain(auto);
            Check(Field<List<LogEntry>>(auto, "_consoleHistory").Count == frozen, "Post-auto-stop Stop must not duplicate.");
            CloseEditor(auto);
        }
        finally
        {
            if (auto.IsVisible) CloseEditor(auto);
        }
    }

    private static void CloseReopen()
    {
        _ = Log.Drain();
        var first = new MainWindow();
        first.Show();
        Dispatcher.UIThread.RunJobs();
        Field<DispatcherTimer?>(first, "_playTimer")?.Stop();
        Log.Info("before-close");
        Call(first, "DrainConsole");
        Dispatcher.UIThread.RunJobs();
        Check(Field<List<LogEntry>>(first, "_consoleHistory").Count == 1, "First window must intake.");
        var timerBefore = Field<DispatcherTimer?>(first, "_consoleTimer");
        Check(timerBefore is not null && timerBefore.IsEnabled, "Intake timer must run while open.");
        typeof(MainWindow).GetField("_sceneDirty", AnyInstance)!.SetValue(first, false);
        first.Close();
        Dispatcher.UIThread.RunJobs();
        Check(Field<DispatcherTimer?>(first, "_consoleTimer") is null, "Close must release the intake timer.");

        // Queued while no window: survives in the bounded queue.
        Log.Info("between-windows");
        var second = new MainWindow();
        second.Show();
        Dispatcher.UIThread.RunJobs();
        Field<DispatcherTimer?>(second, "_playTimer")?.Stop();
        Call(second, "DrainConsole");
        Dispatcher.UIThread.RunJobs();
        var history = Field<List<LogEntry>>(second, "_consoleHistory");
        Check(history.Count == 1 && history[0].Message == "between-windows",
            $"Reopened window must intake once without duplication, got {history.Count}.");
        Log.Info("second-live");
        Call(second, "DrainConsole");
        Dispatcher.UIThread.RunJobs();
        Check(Field<List<LogEntry>>(second, "_consoleHistory").Count == 2, "Second window must continue intake.");
        typeof(MainWindow).GetField("_sceneDirty", AnyInstance)!.SetValue(second, false);
        second.Close();
        Dispatcher.UIThread.RunJobs();
        _ = Log.Drain();
    }

    public sealed class ConsoleProbe
    {
        [Start] private void Begin() { }
    }

    public sealed class ConsoleFailUpdate
    {
        [Update] private void Tick() => throw new ApplicationException("console update boom");
        [Destroy] private void End() { }
    }

    public sealed class ConsoleFailCleanup : IDisposable
    {
        [Start] public void Begin() { }
        [Destroy] public void End() => throw new ApplicationException("destroy failure A");
        public void Dispose() { }
    }

    public sealed class ConsoleFailCleanupB : IDisposable
    {
        [Start] public void Begin() { }
        [Destroy] public void End() => throw new ApplicationException("destroy failure B");
        public void Dispose() { }
    }
}
