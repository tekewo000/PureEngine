using System.Numerics;
using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PureEngine.Core;
using PureEngine.Editor;
using PureEngine.Runtime;

static class PrefabEditorChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly string[] PaneNames = ["SceneSurface", "SceneViewPane", "ProjectFiles", "InspectorPane"];

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
            var store = (EditSceneStore)typeof(MainWindow).GetField("_sceneDocument", Instance)!.GetValue(editor)!;
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
            var dirtyBeforeOpen = store.IsDirty;
            Program.Wait((Task)Call(editor, "OpenSelectedExplorerEntry")!);
            Check(store.Current.Objects.Count == before && store.IsDirty == dirtyBeforeOpen
                && !ReferenceEquals(ActiveStore(editor), store)
                && editor.FindControl<TabControl>("ViewportTabs")!.SelectedIndex == 2,
                "Double-clicking a prefab must open an isolated editor without placing or dirtying the main scene.");
            Call(editor, "ClosePrefabEditor");

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
            Call(editor, "ClosePrefabEditor");
            editor.Close();
            Dispatcher.UIThread.RunJobs();
        }
        Console.WriteLine("PASS: prefab save, listing, placement, parents, invalid files, double-click, scene/Inspector drag-drop, and Play guards.");
        CheckEditingWorkflow(root);
        CheckActivationFailure(root);
    }

    private static EditSceneStore ActiveStore(MainWindow editor) =>
        (EditSceneStore)typeof(MainWindow).GetProperty("EditSceneStore", Instance)!.GetValue(editor)!;

    private static void CheckEditingWorkflow(string root)
    {
        using var session = ProjectSession.Create(root, "PrefabEditingWorkflow");
        session.Components.Registry.Register<PrefabEditorPart>("checks.prefab-editor-part");
        session.Components.Registry.Register<MenuAssetFixture>("checks.menu-asset");
        var editor = new MainWindow(session);
        editor.Show();
        Dispatcher.UIThread.RunJobs();
        var main = ActiveStore(editor);
        try
        {
            var source = main.Current.AddEmpty();
            source.Rename("Main source");
            source.Attach(new PrefabEditorPart { Power = 9 });
            var mainOnly = main.Current.AddEmpty();
            mainOnly.Rename("Main only");
            Call(editor, "SyncHierarchyForTest");
            Call(editor, "SelectSceneObjectForTest", mainOnly);
            var prefabPath = (string)Call(editor, "SavePrefabToPath", source, "", "Editable.pure.prefab.yaml")!;
            var original = PrefabFile.Load(prefabPath);
            var scenePath = Path.Combine(session.Project.RootDirectory, "Scenes", "Workflow.pure.scene.yaml");
            main.SetPath(scenePath);
            Program.Wait((Task)Call(editor, "SaveSceneAsync", false)!);
            var sceneBytes = File.ReadAllBytes(scenePath);
            var prefabBytes = File.ReadAllBytes(prefabPath);
            var tabs = editor.FindControl<TabControl>("ViewportTabs")!;
            var pan = typeof(MainWindow).GetField("_scenePan", Instance)!;
            var zoom = typeof(MainWindow).GetField("_sceneZoom", Instance)!;
            pan.SetValue(editor, new Vector2(31, 47));
            zoom.SetValue(editor, 1.5f);
            Control[] panes = [.. PaneNames.Select(name => editor.FindControl<Control>(name)!)];
            StyledElement?[] paneParents = [.. panes.Select(pane => pane.Parent)];
            (int, int)[] panePositions = [.. panes.Select(pane => (Grid.GetRow(pane), Grid.GetColumn(pane)))];

            Program.Wait((Task)Call(editor, "OpenPrefabEditorCore", prefabPath)!);
            Dispatcher.UIThread.RunJobs();
            var prefab = ActiveStore(editor);
            var prefabRoot = prefab.Current.RootObjects.Single();
            Check(!ReferenceEquals(prefab, main) && prefabRoot.Id == source.Id
                && !ReferenceEquals(prefabRoot, source) && main.Current.Objects.Count == 2
                && !main.IsDirty && !prefab.IsDirty, "Opening must isolate objects and preserve clean documents.");
            Check(tabs.SelectedIndex == 2 && editor.Title!.Contains("Editable.pure.prefab.yaml")
                && editor.FindControl<TextBlock>("StuffsContext")!.IsVisible
                && editor.FindControl<TextBlock>("StuffsContext")!.Text!.Contains("Editable.pure.prefab.yaml")
                && Equals(editor.FindControl<MenuItem>("SaveSceneMenu")!.Header, "Save Prefab"),
                "The active prefab must be identified in the tab, title, hierarchy context, and Save command.");
            Check(editor.FindControl<TreeView>("SceneObjects")!.Items.Count == 1
                && ReferenceEquals(Call(editor, "GetSelectedSceneObject"), prefabRoot),
                "Prefab hierarchy must show only its root and select it for the Inspector.");
            Check(panes.Select(pane => pane.Parent).SequenceEqual(paneParents)
                && panes.Select(pane => (Grid.GetRow(pane), Grid.GetColumn(pane))).SequenceEqual(panePositions),
                "Opening a prefab must preserve the existing pane parents and grid positions.");

            editor.FindControl<TextBox>("ObjectName")!.Text = "Edited prefab";
            Dispatcher.UIThread.RunJobs();
            var power = editor.GetVisualDescendants().OfType<TextBox>()
                .Single(box => Equals(AutomationProperties.GetName(box), "PrefabEditorPart.Power"));
            power.Text = "27";
            Dispatcher.UIThread.RunJobs();
            Check(prefabRoot.Name == "Edited prefab" && prefabRoot.GetComponent<PrefabEditorPart>()!.Power == 27
                && source.Name == "Main source" && source.GetComponent<PrefabEditorPart>()!.Power == 9
                && prefab.IsDirty && !main.IsDirty,
                "Inspector name and component edits must dirty only the prefab.");
            Check(Equals(editor.FindControl<TabItem>("PrefabEditorTab")!.Header, "Prefab Editor *"),
                "Unsaved prefab edits must be visible on the tab.");
            pan.SetValue(editor, new Vector2(-12, 88));
            zoom.SetValue(editor, 2f);
            tabs.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            Check(ReferenceEquals(ActiveStore(editor), main)
                && ReferenceEquals(Call(editor, "GetSelectedSceneObject"), mainOnly)
                && Equals(pan.GetValue(editor), new Vector2(31, 47)) && Equals(zoom.GetValue(editor), 1.5f),
                "Scene tab must restore its document, selection, pan, and zoom.");
            editor.FindControl<TextBox>("ObjectName")!.Text = "Changed main only";
            Dispatcher.UIThread.RunJobs();
            Check(main.IsDirty && prefab.IsDirty, "Both documents must retain independent unsaved edits.");
            tabs.SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();
            Check(ReferenceEquals(Call(editor, "GetSelectedSceneObject"), prefabRoot)
                && Equals(pan.GetValue(editor), new Vector2(-12, 88)) && Equals(zoom.GetValue(editor), 2f),
                "Prefab tab must restore its own selection, pan, and zoom.");
            editor.Close();
            AnswerDialog(editor, "Unsaved Prefab", "Discard");
            AnswerDialog(editor, "Unsaved Scene", "Cancel");
            Check(editor.IsVisible && ReferenceEquals(ActiveStore(editor), prefab) && prefab.IsDirty && main.IsDirty
                && prefabRoot.Name == "Edited prefab" && prefabRoot.GetComponent<PrefabEditorPart>()!.Power == 27
                && mainOnly.Name == "Changed main only" && File.ReadAllBytes(prefabPath).SequenceEqual(prefabBytes),
                "Canceling scene close after discarding the prefab must preserve both open documents and their edits.");
            editor.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent, Key = Key.S,
                KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift
            });
            Dispatcher.UIThread.RunJobs();
            Check(prefab.IsDirty && File.ReadAllBytes(prefabPath).SequenceEqual(prefabBytes)
                && ReferenceEquals(ActiveStore(editor), prefab),
                "Ctrl+Shift+S must not silently save a prefab in place or clear its dirty state.");
            Check((bool)Call(editor, "SavePrefabEditor")!, "Prefab Save must succeed.");
            Check(!prefab.IsDirty && main.IsDirty && File.ReadAllBytes(scenePath).SequenceEqual(sceneBytes)
                && !File.ReadAllBytes(prefabPath).SequenceEqual(prefabBytes),
                "Prefab Save must write only the prefab and clear only its dirty state.");
            var saved = PrefabFile.Load(prefabPath);
            Check(saved.Id == original.Id && prefabRoot.Id == source.Id
                && saved.Objects!.SelectMany(item => item.Components!).Select(component => component.Id)
                    .SequenceEqual(original.Objects!.SelectMany(item => item.Components!).Select(component => component.Id)),
                "Prefab Save must preserve the asset, authored object, and component IDs.");
            var reopened = PrefabFile.OpenForEditing(prefabPath, session.Components.Registry, out var savedId, out _);
            Check(savedId == original.Id && reopened.RootObjects.Single().Id == source.Id
                && reopened.RootObjects.Single().Name == "Edited prefab"
                && reopened.RootObjects.Single().GetComponent<PrefabEditorPart>()!.Power == 27,
                "Saved Inspector edits and IDs must survive reload.");
            var savedPrefabBytes = File.ReadAllBytes(prefabPath);
            tabs.SelectedIndex = 0;
            Program.Wait((Task)Call(editor, "SaveSceneAsync", false)!);
            Check(!main.IsDirty && File.ReadAllBytes(prefabPath).SequenceEqual(savedPrefabBytes)
                && !File.ReadAllBytes(scenePath).SequenceEqual(sceneBytes),
                "Scene Save must write only the main scene.");
            tabs.SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();

            Check(!editor.FindControl<MenuItem>("DeleteObjectMenuItem")!.IsEnabled,
                "Prefab root deletion must be disabled.");
            Call(editor, "DeleteSelectedObject");
            Check(prefab.Current.Objects.Count == 1, "Prefab root deletion must also be guarded by the command.");
            Call(editor, "SelectSceneObjectForTest", [null]);
            Call(editor, "OnAddObject", null, new Avalonia.Interactivity.RoutedEventArgs());
            var child = (SceneObject)Call(editor, "GetSelectedSceneObject")!;
            Check(ReferenceEquals(child.Parent, prefabRoot) && prefab.Current.RootObjects.Count == 1,
                "Add Empty without a selection must create a child, never a second prefab root.");
            Check(!(bool)Call(editor, "CanDropInEditingDocument", child.Id, null, HierarchyDropPosition.AsChild)!
                && !(bool)Call(editor, "CanDropInEditingDocument", child.Id, prefabRoot.Id, HierarchyDropPosition.Before)!
                && !(bool)Call(editor, "CanDropInEditingDocument", child.Id, prefabRoot.Id, HierarchyDropPosition.After)!
                && !(bool)Call(editor, "CanDropInEditingDocument", prefabRoot.Id, child.Id, HierarchyDropPosition.AsChild)!,
                "Hierarchy drops must not detach children, create root siblings, or reparent the prefab root.");

            var bad = Path.Combine(session.Project.RootDirectory, "Invalid.pure.prefab.yaml");
            File.WriteAllText(bad, "version: 1\nobjects: []\n");
            try
            {
                Program.Wait((Task)Call(editor, "OpenPrefabEditorCore", bad)!);
                throw new InvalidOperationException("Invalid prefab opening must fail.");
            }
            catch (InvalidDataException) { }
            Check(ReferenceEquals(ActiveStore(editor), prefab) && prefab.IsDirty
                && ReferenceEquals(Call(editor, "GetSelectedSceneObject"), child)
                && !editor.OwnedWindows.Any(window => window.Title == "Unsaved Prefab"),
                "Invalid replacement must preserve the current document and not prompt away its edits.");

            var assetPath = Path.Combine(session.Project.RootDirectory, "Safety.pure.asset.yaml");
            DataAssetFile.Create(assetPath, typeof(MenuAssetFixture), session.Components.Registry);
            Program.Wait((Task)Call(editor, "OpenDataAssetCore", assetPath)!);
            Dispatcher.UIThread.RunJobs();
            var attack = editor.GetVisualDescendants().OfType<TextBox>()
                .Single(box => Equals(AutomationProperties.GetName(box), "MenuAssetFixture.Attack"));
            attack.Text = "41";
            Dispatcher.UIThread.RunJobs();
            var assetBytes = File.ReadAllBytes(assetPath);
            var invalidOpen = (Task)Call(editor, "OpenPrefabEditorCore", bad)!;
            Dispatcher.UIThread.RunJobs();
            Check(invalidOpen.IsCompleted && editor.OwnedWindows.Count == 0,
                "Invalid prefab opening must fail before prompting to discard a dirty data asset.");
            try
            {
                Program.Wait(invalidOpen);
                throw new InvalidOperationException("Invalid prefab opening with a data asset must fail.");
            }
            catch (InvalidDataException) { }
            Check(editor.FindControl<StackPanel>("DataAssetInspector")!.IsVisible && attack.Text == "41"
                && editor.FindControl<TextBlock>("DataAssetTitle")!.Text!.StartsWith("* ")
                && File.ReadAllBytes(assetPath).SequenceEqual(assetBytes),
                "Invalid prefab opening must preserve the dirty data asset and its Inspector.");

            Call(editor, "StartPlay");
            Check(editor.IsPlaying && ReferenceEquals(ActiveStore(editor), main)
                && !editor.FindControl<TabItem>("PrefabEditorTab")!.IsEnabled,
                "Normal Play must switch away from the prefab and disable its editing tab.");
            Check(editor.FindControl<StackPanel>("DataAssetInspector")!.IsVisible
                && Call(editor, "GetSelectedSceneObject") is null,
                "Play from the prefab must preserve the data asset Inspector without restoring a hierarchy selection.");
            var play = (PlaySession)typeof(MainWindow).GetProperty("ActivePlay", Instance)!.GetValue(editor)!;
            var runScene = play.Runtime.Scene;
            Check(runScene.Objects.Count == main.Current.Objects.Count
                && runScene.Objects.Any(item => item.Id == mainOnly.Id)
                && runScene.Objects.All(item => item.Id != child.Id),
                "Play from the prefab tab must run the main scene, not the prefab authoring scene.");
            Call(editor, "StopPlay");
            var beforeAssetSave = File.ReadAllBytes(scenePath);
            editor.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent, Key = Key.S, KeyModifiers = KeyModifiers.Control
            });
            Dispatcher.UIThread.RunJobs();
            Check(DataAssetFile.Load(assetPath, session.Components.Registry).Instance is MenuAssetFixture { Attack: 41 }
                && !editor.FindControl<TextBlock>("DataAssetTitle")!.Text!.StartsWith("* ")
                && File.ReadAllBytes(scenePath).SequenceEqual(beforeAssetSave) && prefab.IsDirty
                && File.ReadAllBytes(prefabPath).SequenceEqual(savedPrefabBytes),
                "After Play, Ctrl+S must still save the open data asset, not either scene document.");
            Call(editor, "CloseDataAssetForEdit");
            tabs.SelectedIndex = 2;
            Check(ReferenceEquals(ActiveStore(editor), prefab) && prefab.IsDirty,
                "Stop must leave prefab edits available and unsaved.");

            Check(!AnswerClose(editor, "Cancel") && ReferenceEquals(ActiveStore(editor), prefab) && prefab.IsDirty,
                "Cancel must keep the dirty prefab open.");
            Check(AnswerClose(editor, "Discard") && ReferenceEquals(ActiveStore(editor), main)
                && File.ReadAllBytes(prefabPath).SequenceEqual(savedPrefabBytes),
                "Discard must close the prefab without writing its changes.");
            Program.Wait((Task)Call(editor, "OpenPrefabEditorCore", prefabPath)!);
            editor.FindControl<TextBox>("ObjectName")!.Text = "Saved by dialog";
            Dispatcher.UIThread.RunJobs();
            Check(AnswerClose(editor, "Save") && ReferenceEquals(ActiveStore(editor), main),
                "Save confirmation must save and close the prefab.");
            var dialogSaved = PrefabFile.OpenForEditing(prefabPath, session.Components.Registry, out _, out _);
            Check(dialogSaved.RootObjects.Single().Name == "Saved by dialog",
                "Save confirmation must persist the Inspector edit.");
        }
        finally
        {
            if (editor.IsPlaying) Call(editor, "StopPlay");
            Window[] dialogs = [.. editor.OwnedWindows];
            foreach (var dialog in dialogs) dialog.Close("discard");
            Call(editor, "CloseDataAssetForEdit");
            Call(editor, "ClosePrefabEditor");
            main.MarkClean();
            editor.Close();
            Dispatcher.UIThread.RunJobs();
        }
        Console.WriteLine("PASS: isolated Prefab Editor context, Inspector, independent saves, stable IDs, tabs, pane positions, child/root guards, Play asset selection/save targeting, invalid-open preservation, Save As rejection, and transactional close dialogs.");
    }

    private static void CheckActivationFailure(string root)
    {
        using var session = ProjectSession.Create(root, "PrefabActivationFailure");
        session.Components.Registry.Register<PrefabActivationFailurePart>("checks.prefab-activation-failure");
        var source = new Scene();
        var sourceRoot = source.AddEmpty();
        sourceRoot.Attach(new PrefabActivationFailurePart());
        var path = Path.Combine(session.Project.RootDirectory, "Throwing.pure.prefab.yaml");
        PrefabFile.Create(path, source, sourceRoot, session.Components.Registry);
        var bytes = File.ReadAllBytes(path);
        var editor = new MainWindow(session);
        editor.Show();
        Dispatcher.UIThread.RunJobs();
        var main = ActiveStore(editor);
        var activated = false;
        var disposals = PrefabActivationFailurePart.Disposals;
        PrefabActivationFailurePart.ShouldThrow = () =>
        {
            activated = !ReferenceEquals(ActiveStore(editor), main);
            return activated;
        };
        try
        {
            var open = (Task)Call(editor, "OpenPrefabEditorCore", path)!;
            try
            {
                Program.Wait(open);
                throw new InvalidOperationException("The throwing Inspector getter must fail activation.");
            }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException
                { Message: "Inspector activation failure." }) { }
            Check(activated && ReferenceEquals(ActiveStore(editor), main)
                && typeof(MainWindow).GetField("_prefabScene", Instance)!.GetValue(editor) is null
                && !editor.FindControl<TabItem>("PrefabEditorTab")!.IsVisible
                && PrefabActivationFailurePart.Disposals == disposals + 1
                && File.ReadAllBytes(path).SequenceEqual(bytes),
                "An Inspector getter failure after candidate adoption must release the candidate and restore the main context.");
        }
        finally
        {
            PrefabActivationFailurePart.ShouldThrow = null;
            Call(editor, "ClosePrefabEditor");
            main.MarkClean();
            editor.Close();
            Dispatcher.UIThread.RunJobs();
        }
        Console.WriteLine("PASS: throwing prefab Inspector activation releases the candidate and restores the main context.");
    }

    private static bool AnswerClose(MainWindow editor, string answer)
    {
        var close = (Task<bool>)Call(editor, "ConfirmClosePrefabEditor")!;
        Dispatcher.UIThread.RunJobs();
        var dialog = editor.OwnedWindows.Single(window => window.Title == "Unsaved Prefab");
        var button = dialog.GetVisualDescendants().OfType<Avalonia.Controls.Button>()
            .Single(item => Equals(item.Content, answer));
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Program.Wait(close);
        return close.GetAwaiter().GetResult();
    }

    private static void AnswerDialog(MainWindow editor, string title, string answer)
    {
        Program.Until(() => editor.OwnedWindows.Any(window => window.Title == title));
        var dialog = editor.OwnedWindows.Single(window => window.Title == title);
        dialog.GetVisualDescendants().OfType<Avalonia.Controls.Button>()
            .Single(button => Equals(button.Content, answer))
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
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

public sealed class PrefabActivationFailurePart : IDisposable
{
    public static Func<bool>? ShouldThrow { get; set; }
    public static int Disposals { get; private set; }

    [Inspector]
    public int Value
    {
        get => ShouldThrow?.Invoke() == true
            ? throw new InvalidOperationException("Inspector activation failure.") : field;
        set;
    }

    public void Dispose() => Disposals++;
}
