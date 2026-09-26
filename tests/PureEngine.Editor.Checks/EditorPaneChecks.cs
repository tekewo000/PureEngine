using PureEngine.Core;
using PureEngine.Editor;

static class EditorPaneChecks
{
    public static void Run(string parent)
    {
        SelectionAndInspector();
        ProjectFiles(parent);
        var model = new ConsoleViewModel();
        model.Clear();
        List<string?> notifications = [];
        model.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        Log.Info("Player moved");
        Log.Engine.Warning("Renderer warning");
        model.Drain();
        Check(model.History.Count == 2 && model.Rows.Count == 2 && model.InfoCount == 1 && model.WarningCount == 1,
            "Console intake must populate history, rows, and counts without a window.");
        var selected = model.Rows[1];
        model.Select(selected);
        var detail = model.Detail;
        model.ShowEngine = false;
        Check(model.Rows.Count == 1 && model.SelectedRow is null && model.Detail == detail && model.WarningCount == 1,
            "Filters must hide rows without deleting history, counts, or the detail being read.");
        model.Search = "MOVED";
        Check(model.Rows.Count == 1, "Search must be case-insensitive.");
        model.ShowInfo = false;
        Check(model.Rows.Count == 0 && notifications.Contains(nameof(ConsoleViewModel.Rows)),
            "Filter changes must notify bound views immediately.");
        model.ClearCommand.Execute(null);
        Check(model.History.Count == 0 && model.Detail.Length == 0 && model.HistoryDropped == 0,
            "Clear must be executable through ICommand and reset retained state.");
        Console.WriteLine("PASS: pane presentation state, filtering, selection, notification, and commands without controls.");
    }

    private static void SelectionAndInspector()
    {
        using var model = new EditorViewModel();
        var documents = model.Documents;
        var parent = documents.Scene.Current.AddEmpty();
        var child = documents.Scene.Current.AddEmpty();
        child.SetParent(parent);
        var value = new MvvmDocumentValue();
        parent.Attach(value);
        model.Hierarchy.RebuildRoots();
        var nodes = model.Hierarchy.EnumerateNodes().ToDictionary(node => node.Ref.Id);
        model.Hierarchy.Select([nodes[child.Id], nodes[parent.Id]], nodes[child.Id]);
        Check(model.Hierarchy.SelectedObjects().SequenceEqual([parent, child]) && model.Hierarchy.Primary!.Ref == child,
            "Multi-selection must use display order while preserving its primary node.");
        model.Hierarchy.ExpandAncestors(child);
        model.Hierarchy.RebuildRoots();
        Check(model.Hierarchy.Primary!.Ref == child && model.Hierarchy.Roots[0].IsExpanded,
            "Rebuilding must preserve expansion and rebind selection to the new nodes.");
        documents.Scene.Current.Remove(child);
        model.Hierarchy.RebuildRoots();
        Check(model.Hierarchy.Primary!.Ref == parent && model.Hierarchy.SelectedObjects().Count == 1,
            "Removing a selected object must drop its selection without losing the surviving one.");

        var inspector = model.Inspector;
        inspector.Select(parent);
        var originalName = parent.Name;
        inspector.Name = " ";
        Check(inspector.HasInputErrors && parent.Name == originalName && !documents.IsDirty,
            "An invalid name must remain editable without changing the scene.");
        inspector.Name = "Renamed";
        Check(!inspector.HasInputErrors && parent.Name == "Renamed" && documents.Scene.IsDirty,
            "A valid name must update the selected object and mark its document dirty without a view subscriber.");
        documents.Scene.MarkClean();
        var member = typeof(MvvmDocumentValue).GetProperty(nameof(MvvmDocumentValue.Value))!;
        Check(inspector.SetNumericText(value, member, "unfinished") is not null && value.Value == 42 && !documents.IsDirty,
            "Invalid numeric text must not overwrite the last valid value.");
        Check(inspector.SetNumericText(value, member, "73") is null && value.Value == 73 && documents.Scene.IsDirty,
            "Valid numeric edits must reach the model and its document.");
        var firstError = Guid.NewGuid();
        var secondError = Guid.NewGuid();
        inspector.SetError(firstError, "First");
        inspector.SetError(secondError, "Second");
        inspector.SetError(firstError, null);
        Check(inspector.InvalidCount == 1 && inspector.IsInvalid(secondError),
            "Removing one editor must preserve errors belonging to other editors.");
        inspector.IsReadOnly = true;
        inspector.SetNumericText(value, member, "99");
        Check(value.Value == 73, "Play must prevent editing through the presentation model itself.");
        model.DataAsset.Adopt(new DataAssetEditState("value.pure.asset.yaml", new MvvmDocumentAsset(), Guid.NewGuid(), "checks.mvvm-asset"));
        Check(!model.DataAsset.IsVisible, "A selected scene object must show its own Inspector even while an asset document remains open.");
        inspector.Select(null);
        Check(model.DataAsset.IsVisible, "Clearing the object selection must restore the open asset Inspector.");
    }

    private static void ProjectFiles(string parent)
    {
        using var session = ProjectSession.Create(parent, "PaneProject");
        var model = new ProjectPaneViewModel { SelectedFile = session.Project.StartupScenePath };
        model.RefreshDirectories(session.Project);
        model.RefreshFiles(session.Project, session.Components, session.Project.StartupScenePath);
        Check(model.SelectedEntry is { IsStartup: true } && model.Files.Count == 1,
            "Project navigation must select the startup scene independently of controls.");
        Directory.CreateDirectory(Path.Combine(session.Project.RootDirectory, "Empty"));
        model.Folder = "Empty";
        model.RefreshFiles(session.Project, session.Components, session.Project.StartupScenePath);
        Check(model.SelectedEntry is null && model.FileCountText == "Empty folder",
            "Changing folders must clear an unavailable selection and update the empty state.");
        // The Explorer shows only editor-handled types; metadata, caches, and general files stay hidden.
        File.WriteAllText(Path.Combine(session.Project.RootDirectory, "notes.txt"), "not shown");
        File.WriteAllText(Path.Combine(session.Project.RootDirectory, "Side.cs"), "public sealed class Side {}");
        File.WriteAllBytes(Path.Combine(session.Project.RootDirectory, "Cover.png"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(session.Project.RootDirectory, "Localization.pure.loc.yaml"), "version: 1\n");
        Directory.CreateDirectory(Path.Combine(session.Project.RootDirectory, "bin"));
        Directory.CreateDirectory(Path.Combine(session.Project.RootDirectory, ".pureengine"));
        model.Folder = "";
        model.RefreshDirectories(session.Project);
        model.RefreshFiles(session.Project, session.Components, session.Project.StartupScenePath);
        Check(model.Directories.Contains("Scenes") && model.Directories.All(directory =>
            !directory.Split('/').Any(part => part.StartsWith('.')
                || part.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || part.Equals("obj", StringComparison.OrdinalIgnoreCase))),
            "Explorer folders must hide caches and hidden directories while keeping content folders.");
        var visible = model.Files.Select(entry => entry.DisplayName).ToArray();
        Check(visible.Contains("Side.cs") && visible.Contains("Cover.png") && visible.Contains("Localization.pure.loc.yaml"),
            "Explorer files must show C#, images, and localization.");
        Check(!visible.Contains("Project.pure.project.yaml") && !visible.Contains("PureEngine.Game.csproj")
            && !visible.Contains("PureEngine.Game.slnx") && !visible.Contains("global.json") && !visible.Contains("notes.txt"),
            "Explorer files must hide project metadata, generated workspaces, and general files.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
