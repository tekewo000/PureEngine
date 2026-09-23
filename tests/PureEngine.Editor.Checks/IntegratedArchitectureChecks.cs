using System.Reflection;
using System.Runtime.Loader;
using Avalonia.Controls;
using Avalonia.Threading;
using PureEngine.Core;
using PureEngine.Editor;

static class IntegratedArchitectureChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(MainWindow editor, string name) =>
        (T)typeof(MainWindow).GetField(name, Private)!.GetValue(editor)!;
    private static object? Call(MainWindow editor, string name, params object[] args) =>
        typeof(MainWindow).GetMethod(name, Private)!.Invoke(editor, args);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private static string Source(int version) => $$"""
        using System;
        using Microsoft.Extensions.DependencyInjection;
        using PureEngine.Core;
        public sealed class Rules
        {
            public int Version => {{version}};
        }
        public static class Setup
        {
            public static void ConfigureGameServices(IServiceCollection services) => services.AddScoped<Rules>();
        }
        public sealed class Player : IDisposable
        {
            public readonly Rules Rules;
            public readonly int ConstructorThread = Environment.CurrentManagedThreadId;
            public int DisposeCount;
            [Inspector] public int Health = 10;
            public Player(Rules rules) => Rules = rules;
            [Start] private void Start() { }
            public void Dispose() => DisposeCount++;
        }
        """;

    private static UserCodeCompileResult Compile(ProjectFile project, int version)
    {
        File.WriteAllText(Path.Combine(project.RootDirectory, "Game.cs"), Source(version));
        var result = UserCodeCompiler.CompileProject(project.RootDirectory);
        Check(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        return result;
    }
    private static AssemblyLoadContext Context(UserCodeCompileResult result) =>
        (AssemblyLoadContext)typeof(UserCodeCompileResult).GetProperty("LoadContext", Private)!.GetValue(result)!;
    private static int Version(object component)
    {
        var rules = component.GetType().GetField("Rules")!.GetValue(component)!;
        return (int)rules.GetType().GetProperty("Version")!.GetValue(rules)!;
    }
    private static object Player(MainWindow editor) => Field<EditSceneStore>(editor, "_editScene").Current.Objects.Single().Components.Single();
    private static TreeView SceneObjects(MainWindow editor) => editor.FindControl<TreeView>("SceneObjects")!;
    private static void Select(MainWindow editor, SceneObject item) =>
        Call(editor, "SelectSceneObjectForTest", [item]);
    private static SceneObject? Selected(MainWindow editor) =>
        (SceneObject?)Call(editor, "GetSelectedSceneObject", []);

    public static void Run(string parent)
    {
        using var created = ProjectSession.Create(parent, "IntegratedAsync");
        var project = created.Project;
        UserCodeCompileTracker.Release(Compile(project, 1));
        using (var authoring = ProjectSession.Open(project.ManifestPath))
        {
            var item = authoring.Scene.AddEmpty();
            item.Attach(authoring.EditServices.Factory(authoring.Components.UserTypes.Single(t => t.Name == "Player")));
            item.SetStartPriority(item.Components.Single(), -12);
            File.WriteAllText(project.StartupScenePath, new SceneSerializer(authoring.Components.Registry).Serialize(authoring.Scene));
        }
        EditorPipeline(project);
        StartupPipeline(project);
        Console.WriteLine("PASS: integrated project registration/runtime/ownership with async Editor adoption, UI responsiveness, live edits, stale results, guards, shutdown and startup cancellation.");
    }

    private static void EditorPipeline(ProjectFile project)
    {
        using var opened = ProjectSession.Open(project.ManifestPath);
        var editor = new MainWindow(opened);
        editor.Show();
        // File watcher delivery is covered separately; control compilation completions deterministically here.
        Field<UserCodeWatcher>(editor, "_userCodeWatcher").Dispose();
        typeof(MainWindow).GetField("_userCodeWatcher", Private)!.SetValue(editor, null);
        Field<UserCodeCompileTracker>(editor, "_compileTracker").Dispose();
        var gates = Enumerable.Range(0, 6).Select(_ =>
            new TaskCompletionSource<UserCodeCompileResult>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var calls = 0;
        var uiThread = Environment.CurrentManagedThreadId;
        var workerThread = 0;
        using var tracker = new UserCodeCompileTracker(project.RootDirectory, (_, _) =>
        {
            workerThread = Environment.CurrentManagedThreadId;
            return gates[Interlocked.Increment(ref calls) - 1].Task;
        });
        typeof(MainWindow).GetField("_compileTracker", Private)!.SetValue(editor, tracker);
        var store = Field<EditSceneStore>(editor, "_editScene");
        Task Reload() => (Task)Call(editor, "ReloadUserCode")!;
        try
        {
            var old = Compile(project, 2);
            var latest = Compile(project, 3);
            var staleUnloaded = 0;
            Context(old).Unloading += _ => staleUnloaded++;
            var first = Reload();
            Program.Until(() => Volatile.Read(ref calls) == 1);
            var second = Reload();
            Program.Until(() => Volatile.Read(ref calls) == 2);
            var uiRan = false;
            Dispatcher.UIThread.Post(() => uiRan = true);
            Program.Until(() => uiRan);
            Check(!second.IsCompleted && workerThread != uiThread, "The actual Editor compile path must leave UI work responsive.");
            var oldPlayer = Player(editor);
            oldPlayer.GetType().GetField("Health")!.SetValue(oldPlayer, 81);
            store.MarkChanged();
            Select(editor, store.Current.Objects.Single());
            gates[1].SetResult(latest);
            Program.Wait(second);
            var current = Player(editor);
            Check(Version(current) == 3 && (int)current.GetType().GetField("Health")!.GetValue(current)! == 81 && store.IsDirty,
                "The latest code must migrate current edits, not a request-time snapshot.");
            Check((int)current.GetType().GetField("ConstructorThread")!.GetValue(current)! == uiThread,
                "Component construction and migration must stay on the UI thread.");
            Check((int)oldPlayer.GetType().GetField("DisposeCount")!.GetValue(oldPlayer)! == 1,
                "Adoption must release old components once.");
            Check(store.Current.Objects.Single().GetStartPriority(current) == -12
                && ReferenceEquals(Selected(editor), store.Current.Objects.Single()),
                "Priority and selection must survive async adoption.");
            gates[0].SetResult(old);
            Program.Wait(first);
            Check(staleUnloaded == 1 && ReferenceEquals(Player(editor), current), "Late results must unload without replacing newer code.");

            var whilePlaying = Compile(project, 4);
            var third = Reload();
            Program.Until(() => Volatile.Read(ref calls) == 3);
            Call(editor, "StartPlay");
            Check(editor.IsPlaying, "Integrated runtime must accept project DI registration.");
            gates[2].SetResult(whilePlaying);
            Program.Wait(third);
            Check(Version(Player(editor)) == 3, "A compile finishing during Play must defer adoption.");
            Call(editor, "StopPlay");
            Check(Version(Player(editor)) == 4, "Stop must adopt the completed pending code.");

            var whileBusy = Compile(project, 5);
            var fourth = Reload();
            Program.Until(() => Volatile.Read(ref calls) == 4);
            typeof(MainWindow).GetField("_fileBusy", Private)!.SetValue(editor, true);
            gates[3].SetResult(whileBusy);
            Program.Wait(fourth);
            Check(Version(Player(editor)) == 4, "File operations must defer completed results.");
            typeof(MainWindow).GetField("_fileBusy", Private)!.SetValue(editor, false);
            Call(editor, "FlushPendingUserCodeReload");
            Check(Version(Player(editor)) == 5, "Ending a file operation must allow adoption.");

            var whileInvalid = Compile(project, 6);
            var fifth = Reload();
            Program.Until(() => Volatile.Read(ref calls) == 5);
            editor.FindControl<TextBox>("ObjectName")!.Text = "";
            Program.Until(() => editor.FindControl<TextBlock>("NameError")!.IsVisible);
            gates[4].SetResult(whileInvalid);
            Program.Wait(fifth);
            Check(Version(Player(editor)) == 5, "Input errors must defer completed results.");
            editor.FindControl<TextBox>("ObjectName")!.Text = "Player";
            Program.Until(() => Version(Player(editor)) == 6);
            Check(Version(Player(editor)) == 6, "Correcting input must allow adoption.");

            var afterClose = Compile(project, 7);
            var closedUnloaded = 0;
            Context(afterClose).Unloading += _ => closedUnloaded++;
            var sixth = Reload();
            Program.Until(() => Volatile.Read(ref calls) == 6);
            store.MarkClean();
            editor.Close();
            gates[5].SetResult(afterClose);
            Program.Wait(sixth);
            Check(closedUnloaded == 1 && store.Current.Objects.Count == 0,
                "Completion after close must release code and never repopulate the closed scene.");
        }
        finally
        {
            foreach (var gate in gates) gate.TrySetCanceled();
            store.MarkClean();
            editor.Close();
        }
    }

    private static void StartupPipeline(ProjectFile project)
    {
        var compiled = Compile(project, 8);
        var gate = new TaskCompletionSource<UserCodeCompileResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var uiThread = Environment.CurrentManagedThreadId;
        var workerThread = 0;
        var task = ProjectSession.OpenAsync(project.ManifestPath, compileAsync: (_, _) =>
        {
            Interlocked.Exchange(ref workerThread, Environment.CurrentManagedThreadId);
            return gate.Task;
        });
        Program.Until(() => Volatile.Read(ref workerThread) != 0);
        var responsive = false;
        Dispatcher.UIThread.Post(() => responsive = true);
        Program.Until(() => responsive);
        Check(workerThread != uiThread && !task.IsCompleted, "Project startup compilation must run off the UI thread.");
        gate.SetResult(compiled);
        Program.Wait(task);
        using var session = task.Result;
        var player = session.Scene.Objects.Single().Components.Single();
        Check(Version(player) == 8 && (int)player.GetType().GetField("ConstructorThread")!.GetValue(player)! == uiThread,
            "Async project startup must restore on the UI thread using project services.");

        var canceledCode = Compile(project, 9);
        var unloads = 0;
        Context(canceledCode).Unloading += _ => unloads++;
        using var cancellation = new CancellationTokenSource();
        var cancelGate = new TaskCompletionSource<UserCodeCompileResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var canceled = ProjectSession.OpenAsync(project.ManifestPath, cancellationToken: cancellation.Token, compileAsync: (_, _) =>
        {
            Interlocked.Exchange(ref started, 1);
            return cancelGate.Task;
        });
        Program.Until(() => Volatile.Read(ref started) == 1);
        cancellation.Cancel();
        cancelGate.SetResult(canceledCode);
        Program.Until(() => canceled.IsCompleted);
        try { canceled.GetAwaiter().GetResult(); throw new Exception("Canceled startup must not create a session."); }
        catch (OperationCanceledException) { }
        Check(unloads == 1 && Version(player) == 8, "Canceled startup must release code without changing another session.");
    }
}
