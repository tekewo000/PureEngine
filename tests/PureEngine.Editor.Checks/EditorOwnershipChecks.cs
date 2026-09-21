using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PureEngine.Core;
using PureEngine.Core.Attributes;
using PureEngine.Editor;
using PureEngine.Editor.Samples;
using PureEngine.Runtime;

static class EditorOwnershipChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Call(MainWindow window, string method, params object?[] args) =>
        typeof(MainWindow).GetMethod(method, Private)!.Invoke(window, args);
    private static T Field<T>(MainWindow window, string name) =>
        (T)(typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) is { } field
            ? field.GetValue(window) : typeof(MainWindow).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(window))!;
    private static EditSceneStore EditStore(MainWindow window) =>
        (EditSceneStore)typeof(MainWindow).GetField("_editScene", Private)!.GetValue(window)!;
    private static Scene EditScene(MainWindow window) => EditStore(window).Current;
    private static void Dirty(MainWindow window, bool value)
    {
        var store = EditStore(window);
        if (value) store.MarkChanged();
        else store.MarkClean();
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static void Run(string root)
    {
        var editor = new MainWindow();
        var owner = Field<ProjectComponents>(editor, "_components");
        owner.Registry.Register<OwnershipProbe>("checks.ownership");
        editor.Show();
        var scene = EditScene(editor);
        var services = Field<GameSession>(editor, "_editSession");
        var item = scene.AddEmpty();
        owner.TryAttach(item, typeof(OwnershipProbe), services.Factory);
        var original = item.GetComponent<OwnershipProbe>()!;
        var path = Path.Combine(root, "ownership.pure.scene.yaml");
        File.WriteAllText(path, new SceneSerializer(owner.Registry).Serialize(scene));
        OwnershipProbe.Created.Clear();

        ((Task)Call(editor, "OpenScenePathAsync", path)!).GetAwaiter().GetResult();
        Check(original.Disposes == 1 && original.DisposedWithLiveServices, "Replaced edit scene must be released.");
        Check(OwnershipProbe.Created.Count == 2 && OwnershipProbe.Created[0].Disposes == 1
            && OwnershipProbe.Created[1].Disposes == 0, "Validation copy must be released; adopted copy must stay alive.");
        var current = OwnershipProbe.Created[1];

        Dirty(editor, true);
        var pending = (Task)Call(editor, "OpenScenePathAsync", path)!;
        Dispatcher.UIThread.RunJobs();
        var dialog = editor.OwnedWindows.Single(window => window.Title == "Unsaved Scene");
        dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Cancel"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        pending.GetAwaiter().GetResult();
        Check(OwnershipProbe.Created.Count == 3 && OwnershipProbe.Created[2].Disposes == 1
            && current.Disposes == 0, "Cancelled read must release its temporary copy and keep the current scene.");

        var objects = editor.FindControl<ListBox>("SceneObjects")!;
        objects.SelectedIndex = 0;
        Call(editor, "DeleteSelectedObject");
        Check(current.Disposes == 1 && current.DisposedWithLiveServices, "Deleting an edit object must release it.");
        Dirty(editor, false);
        ((Task)Call(editor, "OpenScenePathAsync", path)!).GetAwaiter().GetResult();
        var replaced = OwnershipProbe.Created[^1];
        Call(editor, "SetCurrentScene", new Scene(), null);
        Check(replaced.Disposes == 1, "Creating a new scene must release the previous scene.");
        ((Task)Call(editor, "OpenScenePathAsync", path)!).GetAwaiter().GetResult();
        var closing = OwnershipProbe.Created[^1];
        editor.Close();
        Dispatcher.UIThread.RunJobs();
        Check(closing.Disposes == 1 && closing.DisposedWithLiveServices && closing.Session.IsDisposed,
            "Editor close must release components before services.");
        Check(OwnershipProbe.Created.All(probe => probe.Disposes == 1 && probe.Destroys == 0),
            "Every editing copy must be released once without game Destroy callbacks.");
        var failingEditor = new MainWindow();
        var failingOwner = Field<ProjectComponents>(failingEditor, "_components");
        failingOwner.Registry.Register<OwnershipProbe>("checks.ownership");
        failingEditor.Show();
        var failingServices = Field<GameSession>(failingEditor, "_editSession");
        var failingScene = EditScene(failingEditor);
        var probes = Enumerable.Range(0, 2).Select(_ =>
        {
            var target = failingScene.AddEmpty();
            failingOwner.TryAttach(target, typeof(OwnershipProbe), failingServices.Factory);
            return target.GetComponent<OwnershipProbe>()!;
        }).ToArray();
        var cleanupFailure = new ApplicationException("editor cleanup failed");
        probes[1].Failure = cleanupFailure;
        Exception? reported = null;
        try { Call(failingEditor, "CloseEditSession"); }
        catch (TargetInvocationException error) { reported = error.InnerException; }
        Check(reported is AggregateException aggregate && aggregate.Flatten().InnerExceptions.Contains(cleanupFailure),
            "Editor cleanup must retain disposal errors.");
        Check(probes.All(probe => probe.Disposes == 1 && probe.DisposedWithLiveServices && probe.Session.IsDisposed),
            "One failed component must not prevent remaining components or services from being released.");
        failingEditor.Close();
        Console.WriteLine("PASS: editor component ownership on reload, cancelled read, deletion, and close.");
    }

    public sealed class OwnershipProbe : IDisposable
    {
        public static readonly List<OwnershipProbe> Created = [];
        public BattleSession Session { get; }
        public int Disposes, Destroys;
        public bool DisposedWithLiveServices;
        public Exception? Failure;
        public OwnershipProbe(BattleSession session) { Session = session; Created.Add(this); }
        [Destroy] private void End() => Destroys++;
        public void Dispose()
        {
            Disposes++;
            DisposedWithLiveServices = !Session.IsDisposed;
            if (Failure is not null) throw Failure;
        }
    }
}
