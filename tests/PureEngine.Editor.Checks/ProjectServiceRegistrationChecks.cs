using PureEngine.Runtime;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using PureEngine.Core;
using PureEngine.Editor;

static class ProjectServiceRegistrationChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static object? Call(MainWindow window, string name, params object?[] args)
    {
        var result = typeof(MainWindow).GetMethod(name, Instance)!.Invoke(window, args);
        if (result is Task task) Program.Wait(task);
        return result;
    }
    private static T Field<T>(MainWindow window, string name) =>
        (T)(typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) is { } field
            ? field.GetValue(window) : typeof(MainWindow).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(window))!;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static string BasicCode() => """
        using System;
        using Microsoft.Extensions.DependencyInjection;
        using PureEngine.Core;

        public sealed class QuestLog : IDisposable
        {
            public int Visits;
            public bool IsDisposed;
            public int DisposeCalls;
            public void Visit() => Visits++;
            public void Dispose()
            {
                if (IsDisposed) throw new InvalidOperationException("QuestLog must be disposed once.");
                IsDisposed = true;
                DisposeCalls++;
            }
        }

        public static class GameSetup
        {
            public static void ConfigureGameServices(IServiceCollection services)
            {
                services.AddScoped<QuestLog>();
            }
        }

        public sealed class QuestBoard : IDisposable
        {
            public readonly QuestLog Log;
            public QuestBoard(QuestLog log) { Log = log ?? throw new ArgumentNullException(nameof(log)); }
            [Inspector] public int Score = 10;
            public int Starts;
            [Start] private void Begin() { Starts++; Log.Visit(); }
            public int Disposes;
            public bool DisposedWithLiveServices;
            public void Dispose() { Disposes++; DisposedWithLiveServices = !Log.IsDisposed; }
        }
        """;

    private static string ExpandedCode() => """
        using System;
        using Microsoft.Extensions.DependencyInjection;
        using PureEngine.Core;

        public sealed class QuestLog : IDisposable
        {
            public int Visits;
            public bool IsDisposed;
            public int DisposeCalls;
            public void Visit() => Visits++;
            public void Dispose()
            {
                if (IsDisposed) throw new InvalidOperationException("QuestLog must be disposed once.");
                IsDisposed = true;
                DisposeCalls++;
            }
        }

        public sealed class BonusService
        {
            public int Calls;
        }

        public static class GameSetup
        {
            public static void ConfigureGameServices(IServiceCollection services)
            {
                services.AddScoped<QuestLog>();
                services.AddScoped<BonusService>();
            }
        }

        public sealed class QuestBoard : IDisposable
        {
            public readonly QuestLog Log;
            public readonly BonusService Bonus;
            public QuestBoard(QuestLog log, BonusService bonus)
            {
                Log = log ?? throw new ArgumentNullException(nameof(log));
                Bonus = bonus ?? throw new ArgumentNullException(nameof(bonus));
            }
            [Inspector] public int Score = 10;
            public int Starts;
            [Start] private void Begin() { Starts++; Log.Visit(); Bonus.Calls++; }
            public int Disposes;
            public bool DisposedWithLiveServices;
            public void Dispose() { Disposes++; DisposedWithLiveServices = !Log.IsDisposed; }
        }
        """;

    public static void Run(string parent)
    {
        NoRegistrarCompatibility(parent);
        EditPlaySharingAndIsolation(parent);
        ReloadServiceChange(parent);
        ReloadFailuresKeepOldState(parent);
        TerminationOrder(parent);
        Console.WriteLine("PASS: project service registration, edit/play sharing and isolation, reload adoption and failure retention, termination order, and compatibility.");
    }

    private static void NoRegistrarCompatibility(string parent)
    {
        var project = ProjectSession.Create(parent, "A1Compat").Project;
        var file = Path.Combine(project.RootDirectory, "Plain.cs");
        File.WriteAllText(file, """
            using PureEngine.Core;
            public class Plain
            {
                [Inspector] public int Value = 7;
            }
            """);
        using var opened = ProjectSession.Open(project.ManifestPath);
        try
        {
            Check(opened.Components.UserTypes.Count == 1, "Plain project must load without a registrar.");
            using var edit = GameSession.Create(GameServices.ForProject(opened.Components));
            var target = new SceneObject("Target");
            var type = opened.Components.GetTypesForFile(file).Single();
            Check(opened.Components.TryAttach(target, type, edit.Factory), "Plain attach must work without project services.");
            var source = new Scene();
            source.AddEmpty().Attach(new PureEngine.Editor.Samples.InjectedPlayer(
                new PureEngine.Editor.Samples.RandomService(), new PureEngine.Editor.Samples.BattleSession()));
            using var play = PlaySession.Prepare(source, opened.Components.Registry, GameServices.ForProject(opened.Components));
            play.Start();
            Check(play.Runtime.IsRunning, "Built-in services must still run without a project registrar.");
            play.Stop();
        }
        finally
        {
            opened.EditServices.Dispose();
            }
    }

    private static void EditPlaySharingAndIsolation(string parent)
    {
        var project = ProjectSession.Create(parent, "A1Sharing").Project;
        var file = Path.Combine(project.RootDirectory, "Game.cs");
        File.WriteAllText(file, BasicCode());
        using var opened = ProjectSession.Open(project.ManifestPath);
        var session = opened;
        var editServices = opened.EditServices;
        try
        {
            var boardType = opened.Components.GetTypesForFile(file).Single(t => t.Name == "QuestBoard");
            var logType = opened.Components.GetTypesForFile(file).SingleOrDefault(t => t.Name == "QuestLog")
                ?? boardType.Assembly.GetType("QuestLog")!;
            // 編集での注入成功と同一セッション内の共有。
            var first = session.Scene.AddEmpty();
            first.Rename("First");
            Check(opened.Components.TryAttach(first, boardType, editServices.Factory), "Edit attach must inject project services.");
            var second = session.Scene.AddEmpty();
            second.Rename("Second");
            Check(opened.Components.TryAttach(second, boardType, editServices.Factory), "Second edit attach must inject.");
            dynamic firstBoard = first.Components.Single();
            dynamic secondBoard = second.Components.Single();
            Check(firstBoard.Log is not null && ReferenceEquals((object)firstBoard.Log, (object)secondBoard.Log),
                "Scoped services must be shared within the edit session.");
            firstBoard.Score = 37;

            // Play での注入成功と編集・Play 間の分離。
            var serializer = new SceneSerializer(opened.Components.Registry);
            var yaml = serializer.Serialize(session.Scene);
            SceneFile.Write(project.StartupScenePath, yaml);
            using (var play = PlaySession.Prepare(session.Scene, opened.Components.Registry, GameServices.ForProject(opened.Components)))
            {
                var copies = play.Runtime.Scene.Objects.Select(o => o.Components.Single()).ToArray();
                dynamic playFirst = copies[0];
                dynamic playSecond = copies[1];
                Check((int)playFirst.Score == 37, "Play must restore Inspector values.");
                Check(ReferenceEquals((object)playFirst.Log, (object)playSecond.Log),
                    "Scoped services must be shared within one Play.");
                Check(!ReferenceEquals((object)playFirst.Log, (object)firstBoard.Log),
                    "Play must not share services with edit, including Singletons of that scope.");
                object? playLog = playFirst.Log;
                play.Start();
                Check((int)playFirst.Starts == 1 && (int)playLog!.GetType().GetField("Visits")!.GetValue(playLog)! == 2,
                    "Play Start must use live project services.");
                play.Step(0);
                play.Stop();
                Check((int)playFirst.Disposes == 1 && (bool)playFirst.DisposedWithLiveServices,
                    "Play components must be released before services.");
                Check((bool)playLog.GetType().GetField("IsDisposed")!.GetValue(playLog)!,
                    "Play services must end with the Scope.");
            }

            // 再Play 間の分離。
            object? previousLog;
            using (var replay = PlaySession.Prepare(session.Scene, opened.Components.Registry, GameServices.ForProject(opened.Components)))
            {
                dynamic replayFirst = replay.Runtime.Scene.Objects[0].Components.Single();
                previousLog = replayFirst.Log;
                Check((int)replayFirst.Score == 37, "Replay must restore the same authoring values.");
                replay.Start();
                replay.Stop();
            }
            using (var replay2 = PlaySession.Prepare(session.Scene, opened.Components.Registry, GameServices.ForProject(opened.Components)))
            {
                dynamic replayFirst2 = replay2.Runtime.Scene.Objects[0].Components.Single();
                Check(!ReferenceEquals((object)replayFirst2.Log, (object)previousLog!),
                    "Replay must not carry previous Play state.");
                replay2.Start();
                replay2.Stop();
            }
            Check((int)firstBoard.Starts == 0, "Authoring instances must gain no execution state.");
            foreach (var component in session.Scene.Objects.SelectMany(item => item.Components).Reverse())
                if (component is IDisposable disposable) disposable.Dispose();
        }
        finally
        {
            editServices.Dispose();
            }
    }

    private static void ReloadServiceChange(string parent)
    {
        var project = ProjectSession.Create(parent, "A1Reload").Project;
        var file = Path.Combine(project.RootDirectory, "Game.cs");
        File.WriteAllText(file, BasicCode());
        using var opened = ProjectSession.Open(project.ManifestPath);
        var editor = new MainWindow(opened);
        editor.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var scene = Field<EditSceneStore>(editor, "_editScene").Current;
            var item = scene.AddEmpty();
            item.Rename("Board");
            var services = Field<GameSession>(editor, "EditSession");
            var boardType = opened.Components.GetTypesForFile(file).Single(t => t.Name == "QuestBoard");
            Check(opened.Components.TryAttach(item, boardType, services.Factory), "Reload test requires an attached board.");
            dynamic board = item.Components.Single();
            board.Score = 55;
            item.SetStartPriority(board, -4);
            var objectId = item.Id;
            editor.FindControl<ListBox>("SceneObjects")!.SelectedItem = item;
            Call(editor, "MarkSceneChanged");
            Dispatcher.UIThread.RunJobs();
            var oldBoard = (object)board;
            var oldServices = Field<GameSession>(editor, "EditSession");
            dynamic oldLog = board.Log;
            var oldLogObject = (object)oldLog;

            File.WriteAllText(file, ExpandedCode());
            Call(editor, "ReloadUserCode");
            Dispatcher.UIThread.RunJobs();

            var current = Field<EditSceneStore>(editor, "_editScene").Current.Objects.Single(o => o.Id == objectId);
            dynamic renewed = current.Components.Single();
            Check(!ReferenceEquals((object)renewed, oldBoard), "Reload must create new instances.");
            Check((int)renewed.Score == 55, "Reload must preserve unsaved Inspector values.");
            Check(current.GetStartPriority((object)renewed) == -4, "Reload must preserve Priority.");
            Check(editor.Title!.StartsWith("* "), "Reload must preserve dirty state.");
            Check(ReferenceEquals(editor.FindControl<ListBox>("SceneObjects")!.SelectedItem, current),
                "Reload must preserve selection.");
            Check(renewed.Bonus is not null && !ReferenceEquals((object)renewed.Log, oldLogObject),
                "Reload must resolve new services from the new registration.");
            var currentServices = Field<GameSession>(editor, "EditSession");
            Check(!ReferenceEquals(currentServices, oldServices), "Reload must swap to the new service group.");
            dynamic oldBoardDynamic = oldBoard;
            Check((int)oldBoardDynamic.Disposes == 1 && (bool)oldBoardDynamic.DisposedWithLiveServices,
                "Old components must be released before old services.");
            Check((bool)oldLogObject.GetType().GetField("IsDisposed")!.GetValue(oldLogObject)!,
                "Old services must end after old components.");

            // 新登録で Play が動く。
            Call(editor, "StartPlay");
            Check(editor.IsPlaying, "Play must run with the new registration.");
            Call(editor, "StepPlayOnce", 1f / 60f);
            Call(editor, "StopPlay");
            Check(!(bool)Call(editor, "get_IsPlaying")!, "Stop must leave playing state.");
            Dispatcher.UIThread.RunJobs();
            Field<EditSceneStore>(editor, "_editScene").MarkClean();
        }
        finally
        {
            Field<EditSceneStore>(editor, "_editScene").MarkClean();
            editor.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void ReloadFailuresKeepOldState(string parent)
    {
        var project = ProjectSession.Create(parent, "A1Failures").Project;
        var file = Path.Combine(project.RootDirectory, "Game.cs");
        File.WriteAllText(file, BasicCode());
        using var opened = ProjectSession.Open(project.ManifestPath);
        var editor = new MainWindow(opened);
        editor.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var scene = Field<EditSceneStore>(editor, "_editScene").Current;
            var item = scene.AddEmpty();
            var services = Field<GameSession>(editor, "EditSession");
            var boardType = opened.Components.GetTypesForFile(file).Single(t => t.Name == "QuestBoard");
            Check(opened.Components.TryAttach(item, boardType, services.Factory), "Failure tests require an attached board.");
            dynamic good = item.Components.Single();
            good.Score = 21;
            var goodObject = (object)good;
            var goodServices = Field<GameSession>(editor, "EditSession");
            var goodType = goodObject.GetType();
            var status = editor.FindControl<TextBlock>("FileStatus")!;

            foreach (var (label, broken, expected) in new[]
            {
                ("registration-throws",
                    BasicCode().Replace("services.AddScoped<QuestLog>();",
                        "services.AddScoped<QuestLog>(); throw new System.InvalidOperationException(\"reg-boom\");"),
                    "reg-boom"),
                ("ambiguous",
                    BasicCode() + "\npublic static class ExtraSetup { public static void ConfigureGameServices(Microsoft.Extensions.DependencyInjection.IServiceCollection services) { } }",
                    "Multiple"),
                ("invalid-signature",
                    BasicCode().Replace("public static void ConfigureGameServices(IServiceCollection services)",
                        "public static int ConfigureGameServices(IServiceCollection services)")
                        .Replace("services.AddScoped<QuestLog>();", "return 0;"),
                    "Invalid"),
                ("instance-registrar",
                    BasicCode().Replace("public static class GameSetup", "public class GameSetup")
                        .Replace("public static void ConfigureGameServices", "public void ConfigureGameServices"),
                    "Invalid"),
                ("async-registrar",
                    BasicCode().Replace("public static void ConfigureGameServices", "public static async void ConfigureGameServices")
                        .Replace("services.AddScoped<QuestLog>();", "await System.Threading.Tasks.Task.Yield(); services.AddScoped<QuestLog>();"),
                    "Invalid"),
                ("missing-dependency",
                    BasicCode().Replace("public QuestBoard(QuestLog log)",
                        "public QuestBoard(QuestLog log, IMissing missing)")
                        .Replace("Log = log ?? throw new System.ArgumentNullException(nameof(log));",
                        "Log = log ?? throw new System.ArgumentNullException(nameof(log)); _ = missing;")
                    + "\npublic interface IMissing { }\n",
                    "Cannot apply"),
                ("scene-migration",
                    BasicCode().Replace("[Inspector] public int Score = 10;", "[Inspector] public float Score = 10;"),
                    "Cannot apply"),
            })
            {
                File.WriteAllText(file, broken);
                Call(editor, "ReloadUserCode");
                Dispatcher.UIThread.RunJobs();
                Check(ReferenceEquals(item.Components.Single(), goodObject),
                    $"{label} must preserve the exact old instances.");
                Check(ReferenceEquals(Field<GameSession>(editor, "EditSession"), goodServices),
                    $"{label} must preserve the old service group.");
                Check(opened.Components.Registry.GetType(opened.Components.Registry.GetId(goodType)) == goodType,
                    $"{label} must preserve the old registration.");
                Check((int)goodObject.GetType().GetField("Score")!.GetValue(goodObject)! == 21,
                    $"{label} must preserve unsaved values.");
                Check(status.Text!.Contains(expected),
                    $"{label} must report the reason (expected '{expected}', got '{status.Text}').");
            }

            File.WriteAllText(file, BasicCode());
            Call(editor, "ReloadUserCode");
            Dispatcher.UIThread.RunJobs();
            Check(status.Text!.Contains("Applied C# changes"), "Recovery after failures must work.");
            Field<EditSceneStore>(editor, "_editScene").MarkClean();
        }
        finally
        {
            Field<EditSceneStore>(editor, "_editScene").MarkClean();
            editor.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void TerminationOrder(string parent)
    {
        var project = ProjectSession.Create(parent, "A1Termination").Project;
        var file = Path.Combine(project.RootDirectory, "Game.cs");
        File.WriteAllText(file, BasicCode());
        using var opened = ProjectSession.Open(project.ManifestPath);
        try
        {
            var boardType = opened.Components.GetTypesForFile(file).Single(t => t.Name == "QuestBoard");
            var source = new Scene();
            var item = source.AddEmpty();
            item.Rename("Board");
            // Play 準備は編集用サービス群とは別の独立したサービス群で行う。
            using (var play = PlaySession.Prepare(source, opened.Components.Registry, GameServices.ForProject(opened.Components)))
            {
                // 空シーンの正常終了でもサービス群は単発で解放される。
                play.Start();
                play.Stop();
            }

            source = new Scene();
            item = source.AddEmpty();
            item.Rename("Board");
            // 直接アタッチした authoring 値は Play 側で復元される。ここでは factory 経由の生成順を検証する。
            using var edit = GameSession.Create(GameServices.ForProject(opened.Components));
            Check(opened.Components.TryAttach(item, boardType, edit.Factory), "Termination test requires an attached board.");
            dynamic authoring = item.Components.Single();
            authoring.Score = 9;
            using (var play = PlaySession.Prepare(source, opened.Components.Registry, GameServices.ForProject(opened.Components)))
            {
                dynamic copy = play.Runtime.Scene.Objects[0].Components.Single();
                object? log = copy.Log;
                play.Start();
                play.Step(0);
                Check((int)copy.Starts == 1 && (int)copy.Disposes == 0, "Termination must not run before Stop.");
                play.Stop();
                Check((int)copy.Disposes == 1 && (bool)copy.DisposedWithLiveServices,
                    "Normal end must release components before services.");
                Check((int)log!.GetType().GetField("DisposeCalls")!.GetValue(log)! == 1,
                    "Services must be released once by the Scope.");
                Check((int)copy.Disposes == 1, "Component must not double-dispose the injected service.");
                play.Stop();
                play.Dispose();
                Check((int)copy.Disposes == 1 && (int)log.GetType().GetField("DisposeCalls")!.GetValue(log)! == 1,
                    "Repeated termination must be a no-op.");
            }

            // 準備失敗時も生成済み資源を解放する。不足依存の Play 準備は旧状態を作らない。
            GoodProbe.Disposed.Clear();
            GoodProbe.LastSession = null;
            var badRegistry = new ComponentRegistry();
            badRegistry.Register<NeedMissing>("a1.need-missing");
            badRegistry.Register<GoodProbe>("a1.good");
            var badSource = new Scene();
            var first = badSource.AddEmpty();
            first.Rename("First");
            first.Attach(new GoodProbe(new PureEngine.Editor.Samples.BattleSession()) { Tag = "good" });
            var secondBad = badSource.AddEmpty();
            secondBad.Rename("Second");
            secondBad.Attach(new NeedMissing(new UnregisteredService()));
            PlaySession? failed = null;
            try { failed = PlaySession.Prepare(badSource, badRegistry, GameServices.Configure); }
            catch (InvalidOperationException error)
            {
                Check(error.Message.Contains("a1.need-missing") && error.InnerException is not null,
                    "Missing dependencies must fail before Start with the target identified.");
            }
            Check(failed is null, "Failed preparation must not produce a session.");
            Check(GoodProbe.Disposed.Count == 1 && GoodProbe.Disposed[0].Starts == 0,
                "Preparation failure must dispose created components without Start.");
            Check(GoodProbe.LastSession?.IsDisposed == true, "Preparation failure must end the Play Scope.");
            foreach (var component in source.Objects.SelectMany(o => o.Components).Reverse())
                if (component is IDisposable disposable) try { disposable.Dispose(); } catch { }
            foreach (var component in badSource.Objects.SelectMany(o => o.Components).Reverse())
                if (component is IDisposable disposable) try { disposable.Dispose(); } catch { }
            edit.Dispose();
        }
        finally
        {
            opened.EditServices.Dispose();
            }
    }

    private sealed class UnregisteredService
    {
    }

    private sealed class NeedMissing
    {
        public NeedMissing(UnregisteredService service) => _ = service;
#pragma warning disable CA1822 // Reflection tests require these lifecycle/Inspector members to remain instance members.
        [PureEngine.Core.Start] private void Begin() { }
#pragma warning restore CA1822
    }

    private sealed class GoodProbe(PureEngine.Editor.Samples.BattleSession session) : IDisposable
    {
        public static readonly List<GoodProbe> Disposed = [];
        public static PureEngine.Editor.Samples.BattleSession? LastSession;
        public readonly PureEngine.Editor.Samples.BattleSession Session = session;
        [PureEngine.Core.Inspector] public string Tag = "";
        public int Starts;
        private bool _disposed;

        [PureEngine.Core.Start] private void Begin() => Starts++;
        public void Dispose()
        {
            if (_disposed) throw new InvalidOperationException("Dispose must run once.");
            _disposed = true;
            LastSession = Session;
            Disposed.Add(this);
        }
    }
}
