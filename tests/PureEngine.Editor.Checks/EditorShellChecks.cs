using PureEngine.Core;
using PureEngine.Editor;

static class EditorShellChecks
{
    public static void Run(string parent)
    {
        PlayWithoutWindow();
        ReloadWithoutWindow(parent);
        Console.WriteLine("PASS: editor composition, Play commands, code adoption, view-model rebinding, and shutdown without a window.");
    }

    private static void PlayWithoutWindow()
    {
        using var model = new EditorViewModel();
        model.Components.Registry.Register<MvvmPlayCounter>("checks.mvvm-counter");
        var original = new MvvmPlayCounter();
        model.Documents.Scene.Current.AddEmpty().Attach(original);
        model.FileBusy = true;
        model.StartPlayCommand.Execute(null);
        Check(!model.Play.IsPlaying && model.StatusIsError, "File operations must block Play at the model boundary.");
        model.FileBusy = false;
        var field = Guid.NewGuid();
        model.Inspector.SetError(field, "Invalid input");
        model.StartPlayCommand.Execute(null);
        Check(!model.Play.IsPlaying && model.Status.Contains("Inspector", StringComparison.Ordinal),
            "Invalid input must block the command even without disabled controls.");
        model.Inspector.SetError(field, null);
        model.StartPlayCommand.Execute(null);
        Check(model.Play.IsPlaying && model.Inspector.IsReadOnly && !model.StartPlayCommand.CanExecute(null)
            && model.StopPlayCommand.CanExecute(null), "Play must publish editing and command availability.");
        var session = model.Play.Session!;
        model.Play.Step(0.016f);
        var running = session.Runtime.Scene.Objects.Single().GetComponent<MvvmPlayCounter>()!;
        Check(running.Updates == 1 && original.Updates == 0, "The model must run the isolated Play scene.");
        model.StopPlayCommand.Execute(null);
        Check(!model.Play.IsPlaying && model.CanEdit && !model.Inspector.IsReadOnly,
            "Stop must release Play and restore edit availability without a native view.");
        model.Dispose();
        Check(!model.StartPlayCommand.CanExecute(null) && model.Documents.Scene.Current.Objects.Count == 0,
            "Shutdown must release documents and disable commands even when the root model is retained.");
    }

    private static void ReloadWithoutWindow(string parent)
    {
        using var created = ProjectSession.Create(parent, "ShellReload");
        var project = created.Project;
        created.Dispose();
        var path = Path.Combine(project.RootDirectory, "Counter.cs");
        static string Source(int version) => $$"""
            using PureEngine.Core;
            public sealed class Counter
            {
                [Inspector] public int Value { get; set; } = 10;
                public int Version => {{version}};
            }
            """;
        File.WriteAllText(path, Source(1));
        using var opened = ProjectSession.Open(project.ManifestPath);
        using var model = new EditorViewModel();
        model.AdoptProject(opened);
        var type = model.Components.UserTypes.Single();
        var item = model.Documents.Scene.Current.AddEmpty();
        model.Components.TryAttach(item, type, model.Documents.Scene.Services.Factory);
        type.GetProperty("Value")!.SetValue(item.Components.Single(), 73);
        model.Documents.Scene.MarkChanged();
        model.Hierarchy.RebuildRoots();
        model.Hierarchy.Select(model.Hierarchy.Roots, model.Hierarchy.Roots[0]);
        model.Inspector.Select(item);
        File.WriteAllText(path, Source(2));
        model.Compilation.Reload().GetAwaiter().GetResult();
        var migrated = model.Inspector.Components.Single();
        Check((int)migrated.GetType().GetProperty("Version")!.GetValue(migrated)! == 2
            && (int)migrated.GetType().GetProperty("Value")!.GetValue(migrated)! == 73
            && model.Hierarchy.Primary!.Ref.Id == item.Id,
            "Code adoption must refresh pane models and preserve selection and values without a window subscriber.");
        File.WriteAllText(path, Source(3).Replace("int Value", "string Value").Replace("= 10", "= \"bad\""));
        model.Compilation.Reload().GetAwaiter().GetResult();
        Check(ReferenceEquals(model.Inspector.Components.Single(), migrated) && model.StatusIsError,
            "An incompatible reload must keep the exact current model and report the error.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

public sealed class MvvmPlayCounter
{
    [Inspector] public int Updates { get; set; }
    [Update] private void Update() => Updates++;
}
