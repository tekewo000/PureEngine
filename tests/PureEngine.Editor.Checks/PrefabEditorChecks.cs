using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PureEngine.Core;
using PureEngine.Editor;

static class PrefabEditorChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;

    public static void Run(string root)
    {
        using var session = ProjectSession.Create(root, "PrefabEditor");
        session.Components.Registry.Register<PrefabEditorPart>("checks.prefab-editor-part");
        session.Components.Registry.Register<PrefabEditorHolder>("checks.prefab-editor-holder");
        session.Components.Registry.Register<PrefabDropTarget>("checks.prefab-drop-target");
        var editor = new MainWindow(session);
        editor.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var store = (EditSceneStore)typeof(MainWindow).GetField("_editScene", Instance)!.GetValue(editor)!;
            var tower = store.Current.AddEmpty();
            tower.Rename("Tower");
            var cannon = store.Current.AddEmpty();
            cannon.Rename("Cannon");
            cannon.SetParent(tower);
            var part = new PrefabEditorPart { Power = 9 };
            cannon.Attach(part);
            tower.Attach(new PureEngine.Core.Text { Content = "Prefab text" });
            tower.Attach(new PrefabEditorHolder { Target = part, Title = "base", Extras = [part] });
            Call(editor, "SyncHierarchyForTest");
            Call(editor, "SelectSceneObjectForTest", tower);
            Dispatcher.UIThread.RunJobs();
            Check(editor.FindControl<MenuItem>("SavePrefabMenuItem")!.IsEnabled, "Save as Prefab must enable on selection.");

            var path = (string)Call(editor, "SavePrefabToPath", tower, "", "Tower.pure.prefab.yaml")!;
            Check(File.Exists(path), "Save as Prefab must write the prefab file.");
            Check(!store.IsDirty && store.Current.Objects.Count == 2, "Saving a prefab must not change the scene.");
            var files = editor.FindControl<ListBox>("ProjectFiles")!;
            Check(files.ItemsSource!.Cast<ProjectExplorerEntry>().Any(entry => entry.IsPrefab && entry.FullPath == path),
                "The saved prefab must appear in the Project pane.");
            RejectThrow(() => Call(editor, "SavePrefabToPath", tower, "", "Tower.pure.prefab.yaml"),
                "Saving over an existing prefab must be rejected.");
            var prefabId = PrefabFile.Load(path).Id;
            var catalog = (PrefabCatalog)Call(editor, "BuildPrefabCatalog")!;
            Check(catalog.Find(prefabId) is not null, "Play catalogs must include saved prefabs.");

            Call(editor, "SelectSceneObjectForTest", [null]);
            var placed = (SceneObject)Call(editor, "PlacePrefabForTest", path)!;
            Check(store.Current.Objects.Count == 4 && placed.Name == "Tower" && placed.Parent is null,
                "Placement without a parent must append a root copy.");
            var placedHolder = placed.GetComponent<PrefabEditorHolder>()!;
            var placedCannon = placed.Children.Single();
            var placedPart = placedCannon.GetComponent<PrefabEditorPart>()!;
            Check(placedPart.Power == 9 && ReferenceEquals(placedHolder.Target, placedPart)
                && placedHolder.Extras.Count == 1 && ReferenceEquals(placedHolder.Extras[0], placedPart),
                "Placed values and internal references must follow the copy.");
            Check(placed.Id != tower.Id && placedCannon.Id != cannon.Id, "Placed objects need fresh IDs.");
            Check(store.IsDirty, "Placement must mark the scene dirty.");

            Call(editor, "SelectSceneObjectForTest", cannon);
            var nested = (SceneObject)Call(editor, "PlacePrefabForTest", path)!;
            Check(ReferenceEquals(nested.Parent, cannon), "Placement with a selection must land under it.");

            var bad = Path.Combine(session.Project.RootDirectory, "Broken.pure.prefab.yaml");
            File.WriteAllText(bad, "version: 1\nobjects: []\n");
            var before = store.Current.Objects.Count;
            RejectThrow(() => Call(editor, "PlacePrefabForTest", bad), "Invalid prefabs must fail placement.");
            Check(store.Current.Objects.Count == before, "Failed placement must leave the scene untouched.");

            files.SelectedItem = files.ItemsSource!.Cast<ProjectExplorerEntry>().Single(entry => entry.FullPath == path);
            Program.Wait((Task)Call(editor, "OpenSelectedExplorerEntry")!);
            Check(store.Current.Objects.Count == before + 2, "Double-clicking a prefab must place it.");

            var surface = editor.FindControl<Grid>("SceneSurface")!;
            var prefabFormat = (DataFormat<string>)typeof(MainWindow).GetField("PrefabPathFormat", Static)!.GetValue(null)!;
            using (var unrelated = new DataTransfer())
            {
                var ignored = new DragEventArgs(DragDrop.DragOverEvent, unrelated, surface, default, KeyModifiers.None);
                surface.RaiseEvent(ignored);
                Check(!ignored.Handled, "Stuffs DragOver must ignore unrelated payloads.");
            }
            using (var dragData = new DataTransfer())
            {
                dragData.Add(DataTransferItem.Create(prefabFormat, path));
                var dragOver = new DragEventArgs(DragDrop.DragOverEvent, dragData, surface, default, KeyModifiers.None);
                surface.RaiseEvent(dragOver);
                Check(dragOver.Handled && dragOver.DragEffects == DragDropEffects.Copy, "Prefab DragOver must offer placement.");
                var dropCount = store.Current.Objects.Count;
                surface.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, dragData, surface, default, KeyModifiers.None));
                Dispatcher.UIThread.RunJobs();
                Check(store.Current.Objects.Count == dropCount + 2, "Routed prefab Drop must place the subtree.");
            }

            Call(editor, "StartPlay");
            Check(editor.IsPlaying, "Play must start for the guard test.");
            RejectThrow(() => Call(editor, "SavePrefabToPath", tower, "", "Other.pure.prefab.yaml"),
                "Saving prefabs during Play must be rejected.");
            RejectThrow(() => Call(editor, "PlacePrefabForTest", path), "Placing prefabs during Play must be rejected.");
            using (var guardedDrag = new DataTransfer())
            {
                guardedDrag.Add(DataTransferItem.Create(
                    (DataFormat<string>)typeof(MainWindow).GetField("PrefabPathFormat", Static)!.GetValue(null)!, path));
                var guardedCount = store.Current.Objects.Count;
                surface.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, guardedDrag, surface, default, KeyModifiers.None));
                Dispatcher.UIThread.RunJobs();
                Check(store.Current.Objects.Count == guardedCount, "Prefab Drop during Play must be rejected.");
            }
            Check(Directory.GetFiles(session.Project.RootDirectory, "*.pure.prefab.yaml", SearchOption.AllDirectories).Length == 2,
                "Guarded saves must not write prefab files during Play.");
            Call(editor, "StopPlay");
            Check(!editor.IsPlaying, "Stop must end Play.");
            Call(editor, "SelectSceneObjectForTest", [null]);
            Check(Call(editor, "PlacePrefabForTest", path) is not null, "Placement must work again after Stop.");
            var dropTarget = store.Current.AddEmpty();
            dropTarget.Rename("DropTarget");
            var dropHolder = new PrefabDropTarget();
            dropTarget.Attach(dropHolder);
            Call(editor, "SyncHierarchyForTest");
            Call(editor, "SelectSceneObjectForTest", dropTarget);
            Dispatcher.UIThread.RunJobs();
            var dropFormat = (DataFormat<string>)typeof(MainWindow).GetField("PrefabPathFormat", Static)!.GetValue(null)!;
            var referenceCount = store.Current.Objects.Count;
            var partCombo = editor.GetVisualDescendants().OfType<ComboBox>()
                .Single(combo => Equals(AutomationProperties.GetName(combo), "PrefabDropTarget.Part"));
            using (var dragData = new DataTransfer())
            {
                dragData.Add(DataTransferItem.Create(dropFormat, path));
                var dragOver = new DragEventArgs(DragDrop.DragOverEvent, dragData, partCombo, default, KeyModifiers.None);
                partCombo.RaiseEvent(dragOver);
                Check(dragOver.Handled && dragOver.DragEffects == DragDropEffects.Copy, "Prefab must be accepted by a component reference field.");
                partCombo.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, dragData, partCombo, default, KeyModifiers.None));
            }
            Dispatcher.UIThread.RunJobs();
            var assignedPartObject = store.Current.Objects.FirstOrDefault(item =>
                item.Components.Any(candidate => ReferenceEquals(candidate, dropHolder.Part)));
            Check(assignedPartObject is null && dropHolder.Part is { Power: 9 } && store.Current.Objects.Count == referenceCount,
                "Prefab component drop must assign a template without placing objects.");
            var textCombo = editor.GetVisualDescendants().OfType<ComboBox>()
                .Single(combo => Equals(AutomationProperties.GetName(combo), "PrefabDropTarget.Text"));
            var textRow = textCombo.GetVisualAncestors().OfType<Grid>().First();
            using (var dragData = new DataTransfer())
            {
                dragData.Add(DataTransferItem.Create(dropFormat, path));
                var dragOver = new DragEventArgs(DragDrop.DragOverEvent, dragData, textRow, default, KeyModifiers.None);
                textRow.RaiseEvent(dragOver);
                Check(dragOver.Handled && dragOver.DragEffects == DragDropEffects.Copy,
                    "Prefab Text row must accept a drop on the field row.");
                textRow.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, dragData, textRow, default, KeyModifiers.None));
            }
            Dispatcher.UIThread.RunJobs();
            var assignedTextObject = store.Current.Objects.FirstOrDefault(item =>
                item.Components.Any(candidate => ReferenceEquals(candidate, dropHolder.Text)));
            Check(assignedTextObject is null && dropHolder.Text is { Content: "Prefab text" } && store.Current.Objects.Count == referenceCount, "Prefab Text row drop must assign an inactive template only.");
            var rootCombo = editor.GetVisualDescendants().OfType<ComboBox>()
                .Single(combo => Equals(AutomationProperties.GetName(combo), "PrefabDropTarget.Root"));
            using (var dragData = new DataTransfer())
            {
                dragData.Add(DataTransferItem.Create(dropFormat, path));
                rootCombo.RaiseEvent(new DragEventArgs(DragDrop.DragOverEvent, dragData, rootCombo, default, KeyModifiers.None));
                rootCombo.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, dragData, rootCombo, default, KeyModifiers.None));
            }
            Dispatcher.UIThread.RunJobs();
            Check(dropHolder.Root is not null && dropTarget.Children.Count == 0 && store.Current.Objects.Count == referenceCount,
                "Prefab SceneObject drop must assign the template root without parenting or duplication.");
            var serializer = new SceneSerializer(session.Components.Registry, prefabs: catalog);
            var saved = serializer.Serialize(store.Current);
            var restored = serializer.Deserialize(saved);
            var restoredHolder = restored.Objects.Single(item => item.Id == dropTarget.Id).GetComponent<PrefabDropTarget>()!;
            Check(restored.Objects.Count == referenceCount && restoredHolder.Part is { Power: 9 }
                && restoredHolder.Root?.Children.Single().GetComponent<PrefabEditorPart>() == restoredHolder.Part,
                "Saved class references must resolve to one shared prefab template outside the hierarchy.");
            var runScene = serializer.Clone(store.Current);
            var runHolder = runScene.Objects.Single(item => item.Id == dropTarget.Id).GetComponent<PrefabDropTarget>()!;
            Check(!ReferenceEquals(runHolder.Part, dropHolder.Part), "Play must receive independent prefab template instances.");
            var spawner = new PrefabSpawner();
            spawner.Bind(runScene, session.Components.Registry, null, catalog);
            var spawned = spawner.Instantiate(runHolder.Part!);
            var spawnedOwner = runScene.Objects.Single(item => item.Components.Contains(spawned));
            Check(spawned.Power == 9 && spawnedOwner.Parent?.Parent is null && runScene.Objects.Count == referenceCount + 2,
                "Instantiate(component) must create exactly one subtree and return its matching component.");
            Check(runScene.Remove(spawnedOwner.Parent!) && runScene.Objects.Count == referenceCount,
                "The instantiated prefab must be removable without affecting its template.");
            foreach (var fieldName in new[] { "Part", "Text", "Root" })
            {
                var clear = editor.GetVisualDescendants().OfType<Avalonia.Controls.Button>()
                    .Single(button => Equals(AutomationProperties.GetName(button), $"PrefabDropTarget.{fieldName}.Clear"));
                clear.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
            }
            Check(dropHolder.Part is null && dropHolder.Text is null && dropHolder.Root is null
                && store.Current.Objects.Count == referenceCount, "Clear must remove all prefab references without leaving generated objects.");
            var cleared = serializer.Deserialize(serializer.Serialize(store.Current)).Objects.Single(item => item.Id == dropTarget.Id).GetComponent<PrefabDropTarget>()!;
            Check(cleared.Part is null && cleared.Root is null, "Cleared class references must remain cleared after reload.");
            var row = new Grid();
            var field = new StackPanel();
            var sourceControl = new Border();
            row.Children.Add(field);
            field.Children.Add(sourceControl);
            var assigned = 0;
            void assignOnce(object? _)
            {
                assigned++;
                field.Children.Clear();
            }
            Call(editor, "AttachReferenceDropHandlers", field, typeof(SceneObject), (Action<object?>)assignOnce, null);
            Call(editor, "AttachReferenceDropHandlers", row, typeof(SceneObject), (Action<object?>)assignOnce, field);
            using (var transfer = new DataTransfer())
            {
                var sceneFormat = (DataFormat<string>)typeof(MainWindow).GetField("SceneObjectIdFormat", Static)!.GetValue(null)!;
                transfer.Add(DataTransferItem.Create(sceneFormat, dropTarget.Id.ToString("D")));
                sourceControl.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, transfer, sourceControl, default, KeyModifiers.None));
            }
            Check(assigned == 1, "A source detached during Drop must not trigger the parent row assignment again.");
            store.Current.Remove(dropTarget);
            typeof(EditSceneStore).GetMethod("SetDirtyForTest", Instance)!.Invoke(store, [false]);
        }
        finally
        {
            if (editor.IsPlaying) Call(editor, "StopPlay");
            editor.Close();
            Dispatcher.UIThread.RunJobs();
        }
        Console.WriteLine("PASS: prefab save, listing, placement, parents, invalid files, double-click, scene/Inspector drag-drop, and Play guards.");
    }

    private static object? Call(MainWindow editor, string method, params object?[] args) =>
        typeof(MainWindow).GetMethod(method, Instance)!.Invoke(editor, args);

    private static void RejectThrow(Func<object?> call, string message)
    {
        try { call(); }
        catch (TargetInvocationException error)
            when (error.InnerException is InvalidDataException or ArgumentException or InvalidOperationException or IOException)
        { return; }
        throw new Exception(message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

public sealed class PrefabEditorPart
{
    [Inspector] public int Power { get; set; }
}

public sealed class PrefabEditorHolder
{
    [Inspector] public PrefabEditorPart? Target { get; set; }
    [Inspector] public string Title { get; set; } = "";
    [Inspector] public List<PrefabEditorPart?> Extras { get; set; } = [];
}

public sealed class PrefabDropTarget
{
    [Inspector] public PrefabEditorPart? Part { get; set; }
    [Inspector] public PureEngine.Core.Text? Text { get; set; }
    [Inspector] public SceneObject? Root { get; set; }
}
