using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PureEngine.Core;
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
        var services = Field<GameSession>(editor, "EditSession");
        var item = scene.AddEmpty();
        owner.TryAttach(item, typeof(OwnershipProbe), services.Factory);
        var original = item.GetComponent<OwnershipProbe>()!;
        var path = Path.Combine(root, "ownership.pure.scene.yaml");
        var originalYaml = new SceneSerializer(owner.Registry).Serialize(scene);
        File.WriteAllText(path, originalYaml);
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

        var objects = editor.FindControl<TreeView>("SceneObjects")!;
        var target = EditScene(editor).Objects[0];
        var sibling = new PlayerStats();
        target.Attach(sibling);
        Call(editor, "SyncHierarchyForTest");
        Call(editor, "SelectSceneObjectForTest", target);
        Call(editor, "RefreshComponents");
        Dispatcher.UIThread.RunJobs();
        var cards = editor.FindControl<StackPanel>("ComponentEditors")!;
        var removedCard = (Border)cards.Children[0];
        var remove = removedCard.ContextMenu!.Items.OfType<MenuItem>().Single();
        Check(Equals(remove.Header, "Remove"), "Component context menu must contain only Remove.");
        Call(editor, "StartPlay");
        Check(editor.IsPlaying, "Removal guard check requires Play.");
        var playCopy = OwnershipProbe.Created[^1];
        remove.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(target.Components.Count == 2 && current.Disposes == 0, "Remove must be blocked during Play.");
        Call(editor, "StopPlay");
        OwnershipProbe.Created.Remove(playCopy);
        var removedInput = removedCard.GetVisualDescendants().OfType<TextBox>().Single();
        var siblingInput = cards.Children[1].GetVisualDescendants().OfType<TextBox>()
            .Single(box => Equals(box.GetValue(Avalonia.Automation.AutomationProperties.NameProperty), "PlayerStats.Hp"));
        removedInput.Text = "invalid";
        siblingInput.Text = "unfinished";
        Dispatcher.UIThread.RunJobs();
        Dirty(editor, false);
        remove.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Check(target.Components.Count == 1 && ReferenceEquals(target.Components[0], sibling)
            && ReferenceEquals(Call(editor, "GetSelectedSceneObject"), target), "Remove must preserve the object, selection, and sibling components.");
        Check(current.Disposes == 1 && current.Destroys == 0 && current.DisposedWithLiveServices,
            "Removing an editing component must dispose once without Destroy.");
        Check(EditStore(editor).IsDirty && cards.Children.Count == 1
            && editor.FindControl<TextBlock>("ComponentsHeader")!.Text == "Components (1)",
            "Remove must update the Inspector and dirty state.");
        Check(siblingInput.Text == "unfinished" && Field<HashSet<TextBox>>(editor, "_invalidFields").SetEquals([siblingInput]),
            "Remove must clear only its own input errors and preserve sibling edits.");
        siblingInput.Text = "123";
        Dispatcher.UIThread.RunJobs();
        var saved = (Task<bool>)Call(editor, "SaveSceneAsync", false)!;
        Check(saved.GetAwaiter().GetResult(), "Scene must save after removal and input correction.");
        var restored = new SceneSerializer(owner.Registry).Deserialize(File.ReadAllText(path));
        Check(restored.Objects[0].GetComponent<OwnershipProbe>() is null
            && restored.Objects[0].GetComponent<PlayerStats>()?.Hp == 123, "Saved scenes must exclude removed components.");
        remove.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(current.Disposes == 1 && !EditStore(editor).IsDirty, "Stale Remove must not dispose twice or dirty the scene.");
        Check(owner.TryAttach(target, typeof(OwnershipProbe), services.Factory), "Removed types must be attachable again.");
        var reattached = target.GetComponent<OwnershipProbe>()!;
        reattached.Failure = new ApplicationException("remove cleanup failed");
        Call(editor, "RefreshComponents");
        var failingCard = (Border)cards.Children[1];
        failingCard.ContextMenu!.Items.OfType<MenuItem>().Single().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(reattached.Disposes == 1 && target.GetComponent<OwnershipProbe>() is null
            && editor.FindControl<TextBlock>("FileStatus")!.Text!.Contains("remove cleanup failed"),
            "Cleanup failure must be reported after detaching without retrying disposal.");
        ((Border)cards.Children[0]).ContextMenu!.Items.OfType<MenuItem>().Single()
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(target.Components.Count == 0 && cards.Children.Count == 0
            && editor.FindControl<Button>("AddComponentButton")!.IsEffectivelyVisible
            && editor.FindControl<TextBlock>("NoComponentsHint")!.IsVisible, "The last component must be removable and show the empty hint.");
        Call(editor, "DeleteSelectedObject");
        Check(current.Disposes == 1 && current.DisposedWithLiveServices, "Deleting an edit object must release it.");
        File.WriteAllText(path, originalYaml);
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
        var failingServices = Field<GameSession>(failingEditor, "EditSession");
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
