using System.Numerics;
using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PureEngine.Core;
using PureEngine.Editor;
using PureEngine.Rendering;
using SkiaSharp;
using Button = Avalonia.Controls.Button;

internal static class SceneViewEditorChecks
{
    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static object? Call(MainWindow window, string method, params object?[] args) =>
        typeof(MainWindow).GetMethod(method, AnyInstance)!.Invoke(window, args);

    private static T Field<T>(MainWindow window, string name) =>
        (T)(typeof(MainWindow).GetField(name, AnyInstance)!.GetValue(window)!);

    private static EditSceneStore EditStore(MainWindow window) =>
        (EditSceneStore)typeof(MainWindow).GetField("_editScene", AnyInstance)!.GetValue(window)!;

    private static Scene EditScene(MainWindow window) => EditStore(window).Current;

    private static T Control<T>(MainWindow window, string name) where T : Control =>
        window.FindControl<T>(name)!;

    private static void Select(MainWindow window, SceneObject? item) =>
        Call(window, "SelectSceneObjectForTest", item);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static void Run()
    {
        ActualPointerEvents();
        PanZoomPreserveDirty();
        MoveConfirmCancelAndZ();
        InspectorLiveSync();
        SceneReplaceCancels();
        PlayCancelsAndBlocks();
        DeleteDrops();
        InputSeparation();
        SaveClonePreserveZ();
        HierarchyTreeAndDrop();
        HierarchyRoutedDrag();
        BareParentGizmo();
        Console.WriteLine("PASS: scene pan/zoom, gizmo confirm/cancel, Z preservation, Order save/clone/front pick, inspector sync, scene replacement, play guard, delete safety, input separation, hierarchy tree/drop, bare-parent gizmo, and save/clone.");
    }

    private static MainWindow CreateEditor()
    {
        _ = Log.Drain();
        var editor = new MainWindow { WindowState = WindowState.Normal, Width = 1280, Height = 800 };
        // Headless has no GPU interop, and VulkanViewport async failure logs would leak into later tests.
        // Logic verification does not need GPU rendering, so detach the viewport before showing to avoid the failure itself.
        Control<Grid>(editor, "SceneViewport").Children.Clear();
        editor.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        _ = Log.Drain();
        return editor;
    }

    private static void ActualPointerEvents()
    {
        var editor = CreateEditor();
        var savePath = Path.Combine(Path.GetTempPath(), $"PureEngine-MoveCancel-{Guid.NewGuid():N}.pure.scene.yaml");
        try
        {
            var viewport = Control<Grid>(editor, "SceneViewport");
            var item = AddCard(editor, EditScene(editor), new(80, 70, 7));
            var transform = item.GetComponent<Transform>()!;
            var element = item.GetComponent<UiElement>()!;
            Select(editor, item);
            Dispatcher.UIThread.RunJobs();
            var store = EditStore(editor);
            store.MarkClean();
            IPointer? pointer = null;
            viewport.AddHandler(InputElement.PointerPressedEvent, (_, e) => pointer = e.Pointer,
                Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
            Point At(float x, float y) => viewport.TranslatePoint(new Point(x, y), editor)!.Value;
            void Down(float x = 80, float y = 70)
            {
                editor.MouseDown(At(x, y), MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
            }
            void Move(float x, float y)
            {
                editor.MouseMove(At(x, y), RawInputModifiers.LeftMouseButton);
                Dispatcher.UIThread.RunJobs();
            }
            void Up(float x = 80, float y = 70)
            {
                editor.MouseUp(At(x, y), MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
            }
            void Key(Key key)
            {
                editor.KeyPress(key, RawInputModifiers.None, default, null);
                editor.KeyRelease(key, RawInputModifiers.None, default, null);
                Dispatcher.UIThread.RunJobs();
            }
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            var hit = editor.InputHitTest(At(80, 70));
            Check(hit is Visual visual && (visual == viewport || visual.GetVisualAncestors().Contains(viewport)),
                $"The blank composition host must receive mouse input: hit={hit}, bounds={viewport.Bounds}, visible={viewport.IsEffectivelyVisible}.");
            Down();
            Move(90, 85);
            Check(transform.LocalPosition == new Vector3(90, 85, 7) && !store.IsDirty && pointer?.Captured == viewport,
                "Actual pointer drag must update XY without dirtying yet and capture the pointer.");
            var pan = Field<Vector2>(editor, "_scenePan");
            var zoom = Field<float>(editor, "_sceneZoom");
            Key(Avalonia.Input.Key.F);
            Check(Field<Vector2>(editor, "_scenePan") == pan && Field<float>(editor, "_sceneZoom") == zoom, "F must not change the view during a drag.");
            Key(Avalonia.Input.Key.Escape);
            Check(transform.LocalPosition == new Vector3(80, 70, 7) && pointer?.Captured is null && !store.IsDirty,
                "Escape must restore position and immediately release capture.");
            Up();
            Check(!store.IsDirty, "Release after Escape must not confirm a cancelled drag.");

            Down();
            Move(90, 80);
            editor.MouseDown(At(90, 80), MouseButton.Right, RawInputModifiers.LeftMouseButton);
            editor.MouseUp(At(90, 80), MouseButton.Right, RawInputModifiers.LeftMouseButton);
            Check(Field<SceneViewMath.GizmoKind>(editor, "_sceneMoveKind") != SceneViewMath.GizmoKind.None && !store.IsDirty,
                "Releasing a second button must not commit the original drag.");
            Up(90, 80);
            Check(transform.LocalPosition == new Vector3(90, 80, 7) && store.IsDirty && pointer?.Captured is null,
                "Actual left release must commit once, retain Z and free capture.");
            transform.LocalPosition = new(80, 70, 7);
            Down();
            Move(95, 85);
            Control<TextBox>(editor, "ObjectName").Focus();
            Dispatcher.UIThread.RunJobs();
            Check(transform.LocalPosition == new Vector3(80, 70, 7) && pointer?.Captured is null && store.IsDirty,
                "Moving keyboard focus to Inspector must cancel while preserving pre-existing dirty changes.");
            Up();

            store.MarkClean();
            Down(); Move(90, 80);
            pointer!.Capture(null);
            Check(transform.LocalPosition == new Vector3(80, 70, 7) && !store.IsDirty, "Capture loss must cancel without committing.");
            Up();
            Down(); Move(90, 80);
            element.AnchorMin = element.AnchorMax = new(.25f, .25f);
            Move(95, 85);
            Check(transform.LocalPosition == new Vector3(80, 70, 7) && pointer!.Captured is null,
                "Changed anchor conditions must cancel instead of using the previous layout.");
            Up();
            element.AnchorMin = element.AnchorMax = Vector2.Zero;

            var parent = AddCard(editor, EditScene(editor), Vector3.Zero);
            parent.GetComponent<UiElement>()!.SizeDelta = new(300, 180);
            item.SetParent(parent);
            Call(editor, "SyncHierarchyForTest");
            Down(); Move(90, 80);
            parent.GetComponent<Transform>()!.LocalScale = new(2, 2, 1);
            Move(95, 85);
            Check(transform.LocalPosition == new Vector3(80, 70, 7) && pointer!.Captured is null && !store.IsDirty,
                "A changed parent transform must restore the drag start.");
            Up();
            parent.GetComponent<Transform>()!.LocalScale = Vector3.One;
            item.SetParent(null);
            Call(editor, "SyncHierarchyForTest");

            Down(); Move(90, 80);
            store.MarkSaved(savePath);
            var saving = (Task<bool>)Call(editor, "SaveSceneAsync", false)!;
            Program.Wait(saving);
            Check(saving.Result && pointer!.Captured is null && transform.LocalPosition == new Vector3(80, 70, 7), "Save must cancel before serializing.");
            var serializer = new SceneSerializer(Field<ProjectComponents>(editor, "_components").Registry);
            Check(serializer.Deserialize(File.ReadAllText(savePath)).Objects.First(o => o.Id == item.Id)
                .GetComponent<Transform>()!.LocalPosition == new Vector3(80, 70, 7), "Saved position must exclude the unconfirmed drag and preserve Z.");
            Up();

            editor.MouseDown(At(220, 100), MouseButton.Middle);
            editor.MouseMove(At(230, 110), RawInputModifiers.MiddleMouseButton);
            Check(Field<Vector2>(editor, "_scenePan") == pan + new Vector2(10, 10) && !store.IsDirty, "Actual middle-button pan must only change the view.");
            Key(Avalonia.Input.Key.Escape);
            Check(pointer!.Captured is null && !Field<bool>(editor, "_scenePanning"), "Escape must release a pan capture too.");
            editor.MouseUp(At(230, 110), MouseButton.Middle);
        }
        finally
        {
            CloseEditor(editor);
            if (File.Exists(savePath)) File.Delete(savePath);
        }
    }

    private static void CloseEditor(MainWindow editor)
    {
        EditStore(editor).MarkClean();
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

    private static SceneObject AddCard(MainWindow editor, Scene scene, Vector3 position)
    {
        var item = scene.AddEmpty();
        item.Rename("Card");
        item.Attach(new Transform { LocalPosition = position });
        item.Attach(new UiElement { Pivot = Vector2.Zero, SizeDelta = new Vector2(100, 40) });
        Call(editor, "SyncHierarchyForTest");
        return item;
    }

    private static SceneObject AddCard(Scene scene, Vector3 position)
    {
        var item = scene.AddEmpty();
        item.Rename("Card");
        item.Attach(new Transform { LocalPosition = position });
        item.Attach(new UiElement { Pivot = Vector2.Zero, SizeDelta = new Vector2(100, 40) });
        return item;
    }

    private static void PanZoomPreserveDirty()
    {
        var editor = CreateEditor();
        try
        {
            var scene = EditScene(editor);
            AddCard(scene, new Vector3(10, 20, 0));
            EditStore(editor).MarkClean();
            var panBefore = Field<Vector2>(editor, "_scenePan");
            Check((bool)Call(editor, "TryPanForTest", new Vector2(30, -15))!, "Pan must start.");
            Check(Field<Vector2>(editor, "_scenePan") == panBefore + new Vector2(30, -15), "Pan must translate the view.");
            Check(!EditStore(editor).IsDirty, "Pan must not dirty the scene.");
            var zoomBefore = Field<float>(editor, "_sceneZoom");
            Check((bool)Call(editor, "TryZoomForTest", new Vector2(200, 100), 2f)!, "Zoom must start.");
            Check(Field<float>(editor, "_sceneZoom") == zoomBefore * 2f, "Zoom must scale around the cursor.");
            Check(!EditStore(editor).IsDirty, "Zoom must not dirty the scene.");
        }
        finally
        {
            CloseEditor(editor);
        }
    }

    private static void MoveConfirmCancelAndZ()
    {
        var editor = CreateEditor();
        try
        {
            var scene = EditScene(editor);
            var item = AddCard(editor, scene, new Vector3(10, 20, 5));
            Select(editor, item);
            Dispatcher.UIThread.RunJobs();
            EditStore(editor).MarkClean();
            var press = new Vector2(200, 100);
            Check((bool)Call(editor, "TryBeginMoveForTest", item, SceneViewMath.GizmoKind.XY, press)!, "Move must begin.");
            var movedView = press + new Vector2(10, 15);
            Check((bool)Call(editor, "TryUpdateMoveForTest", movedView)!, "Move must stay active.");
            var transform = item.GetComponent<Transform>()!;
            Check(transform.LocalPosition == new Vector3(20, 35, 5), $"XY move must preserve Z, got {transform.LocalPosition}.");
            Check(!EditStore(editor).IsDirty, "Dragging must not dirty before confirm.");
            Check((bool)Call(editor, "TryConfirmMoveForTest", movedView)!, "Changed confirm must report a change.");
            Check(EditStore(editor).IsDirty, "Confirmed moves must dirty the scene.");
            Check(transform.LocalPosition == new Vector3(20, 35, 5), "Confirm must keep the dragged position.");
            EditStore(editor).MarkClean();
            Check((bool)Call(editor, "TryBeginMoveForTest", item, SceneViewMath.GizmoKind.XY, press)!, "Second move must begin.");
            Check((bool)Call(editor, "TryUpdateMoveForTest", press)!, "Zero delta must stay active.");
            Check(!(bool)Call(editor, "TryConfirmMoveForTest", press)!, "Click without movement must not count as changed.");
            Check(!EditStore(editor).IsDirty, "Click without movement must not dirty.");
            var committed = transform.LocalPosition;
            Check((bool)Call(editor, "TryBeginMoveForTest", item, SceneViewMath.GizmoKind.X, press)!, "X move must begin.");
            Call(editor, "TryUpdateMoveForTest", press + new Vector2(7, 9));
            Check(transform.LocalPosition == new Vector3(committed.X + 7, committed.Y, committed.Z),
                $"X-only move must keep Y/Z, got {transform.LocalPosition}.");
            Call(editor, "CancelSceneViewDrag");
            Check(transform.LocalPosition == committed, "Cancel must restore the start position.");
            Check(!EditStore(editor).IsDirty, "Cancel must not dirty.");
            EditStore(editor).MarkChanged();
            Check((bool)Call(editor, "TryBeginMoveForTest", item, SceneViewMath.GizmoKind.Y, press)!, "Y move must begin.");
            Call(editor, "TryUpdateMoveForTest", press + new Vector2(7, 9));
            Call(editor, "CancelSceneViewDrag");
            Check(transform.LocalPosition == committed, "Cancel must restore the start value.");
            Check(EditStore(editor).IsDirty, "Cancel must keep pre-existing dirty edits as unsaved.");
        }
        finally
        {
            CloseEditor(editor);
        }
    }

    private static void InspectorLiveSync()
    {
        var editor = CreateEditor();
        try
        {
            var scene = EditScene(editor);
            var item = AddCard(editor, scene, new Vector3(4, 6, 2));
            Select(editor, item);
            Dispatcher.UIThread.RunJobs();
            EditStore(editor).MarkClean();
            var transform = item.GetComponent<Transform>()!;
            var press = new Vector2(200, 100);
            Check((bool)Call(editor, "TryBeginMoveForTest", item, SceneViewMath.GizmoKind.XY, press)!, "Sync move must begin.");
            Call(editor, "TryUpdateMoveForTest", press + new Vector2(12, -4));
            Dispatcher.UIThread.RunJobs();
            Check(transform.LocalPosition == new Vector3(16, 2, 2), $"Live move failed, got {transform.LocalPosition}.");
            var card = editor.GetVisualDescendants().OfType<Border>()
                .Single(candidate => ReferenceEquals(candidate.Tag, transform));
            var boxes = card.GetVisualDescendants().OfType<TextBox>().ToList();
            Check(boxes.Count >= 3 && boxes[0].Text == "16" && boxes[1].Text == "2" && boxes[2].Text == "2",
                $"Inspector must reflect the drag, got '{boxes[0].Text}','{boxes[1].Text}','{boxes[2].Text}'.");
            Check(!EditStore(editor).IsDirty, "Live inspector sync must not dirty before confirm.");
            Call(editor, "TryConfirmMoveForTest", press + new Vector2(12, -4));
            Check(EditStore(editor).IsDirty, "Confirm after live sync must dirty.");
        }
        finally
        {
            CloseEditor(editor);
        }
    }

    private static void SceneReplaceCancels()
    {
        var editor = CreateEditor();
        try
        {
            var scene = EditScene(editor);
            var item = AddCard(editor, scene, new Vector3(10, 20, 5));
            Select(editor, item);
            Dispatcher.UIThread.RunJobs();
            EditStore(editor).MarkClean();
            var press = new Vector2(200, 100);
            Check((bool)Call(editor, "TryBeginMoveForTest", item, SceneViewMath.GizmoKind.XY, press)!, "Replace move must begin.");
            Call(editor, "TryUpdateMoveForTest", press + new Vector2(30, 30));
            Check(item.GetComponent<Transform>()!.LocalPosition == new Vector3(40, 50, 5), "Drag must move before replacement.");
            Call(editor, "SetCurrentScene", new Scene(), null);
            Dispatcher.UIThread.RunJobs();
            Check(Field<SceneViewMath.GizmoKind>(editor, "_sceneMoveKind") == SceneViewMath.GizmoKind.None,
                "Scene replacement must clear the drag.");
            Check(EditScene(editor).Objects.Count == 0, "Replacement scene must be adopted.");
            Check(item.GetComponent<Transform>()!.LocalPosition == new Vector3(10, 20, 5),
                "Replacement must revert the old object instead of leaving a partial drag.");
            Check(!(bool)Call(editor, "TryUpdateMoveForTest", press)!, "Updates after replacement must drop safely.");
        }
        finally
        {
            CloseEditor(editor);
        }
    }

    private static void PlayCancelsAndBlocks()
    {
        var editor = CreateEditor();
        try
        {
            var scene = EditScene(editor);
            var item = AddCard(editor, scene, new Vector3(10, 20, 5));
            Select(editor, item);
            Dispatcher.UIThread.RunJobs();
            EditStore(editor).MarkClean();
            var press = new Vector2(200, 100);
            Check((bool)Call(editor, "TryBeginMoveForTest", item, SceneViewMath.GizmoKind.XY, press)!, "Play move must begin.");
            Call(editor, "TryUpdateMoveForTest", press + new Vector2(10, 10));
            Call(editor, "StartPlay");
            Dispatcher.UIThread.RunJobs();
            Check(editor.IsPlaying, "Play must start after cancelling the drag.");
            Check(item.GetComponent<Transform>()!.LocalPosition == new Vector3(10, 20, 5),
                "Play start must revert the unconfirmed drag.");
            Check(!(bool)Call(editor, "TryBeginMoveForTest", item, SceneViewMath.GizmoKind.XY, press)!,
                "Placement editing must stay blocked while playing.");
            Call(editor, "StopPlay");
            Dispatcher.UIThread.RunJobs();
            Check(!editor.IsPlaying, "Play must stop.");
        }
        finally
        {
            CloseEditor(editor);
        }
    }

    private static void DeleteDrops()
    {
        var editor = CreateEditor();
        try
        {
            var scene = EditScene(editor);
            var item = AddCard(editor, scene, new Vector3(10, 20, 5));
            EditStore(editor).MarkClean();
            var press = new Vector2(200, 100);
            Check((bool)Call(editor, "TryBeginMoveForTest", item, SceneViewMath.GizmoKind.XY, press)!, "Delete move must begin.");
            scene.Remove(item);
            Check(!(bool)Call(editor, "TryUpdateMoveForTest", press + new Vector2(5, 5))!,
                "Updates to deleted objects must drop without writing back.");
            Check(!(bool)Call(editor, "TryConfirmMoveForTest", press + new Vector2(5, 5))!,
                "Confirm after deletion must not dirty.");
            Check(!EditStore(editor).IsDirty, "Delete safety path must not dirty through the drag.");
        }
        finally
        {
            CloseEditor(editor);
        }
    }

    private static void InputSeparation()
    {
        var editor = CreateEditor();
        try
        {
            var scene = EditScene(editor);
            var item = AddCard(editor, scene, new Vector3(10, 20, 0));
            Select(editor, item);
            Dispatcher.UIThread.RunJobs();
            var viewport = Control<Grid>(editor, "SceneViewport");
            viewport.Focus();
            Dispatcher.UIThread.RunJobs();
            var panBefore = Field<Vector2>(editor, "_scenePan");
            var zoomBefore = Field<float>(editor, "_sceneZoom");
            var inspectorBox = editor.GetVisualDescendants().OfType<TextBox>()
                .First(box => Equals(box.GetValue(AutomationProperties.NameProperty) as string, "Transform.LocalPosition.X"));
            inspectorBox.Focus();
            Dispatcher.UIThread.RunJobs();
            inspectorBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F });
            Dispatcher.UIThread.RunJobs();
            Check(Field<Vector2>(editor, "_scenePan") == panBefore && Field<float>(editor, "_sceneZoom") == zoomBefore,
                "F from Inspector text input must not steal the Scene View fit.");
            viewport.Focus();
            Dispatcher.UIThread.RunJobs();
            viewport.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F });
            Dispatcher.UIThread.RunJobs();
            var fitted = Field<Vector2>(editor, "_scenePan") != panBefore || Field<float>(editor, "_sceneZoom") != zoomBefore;
            Check(fitted, "F with Scene View focus must fit the selection.");
            Select(editor, null);
            Dispatcher.UIThread.RunJobs();
            var panAfterFit = Field<Vector2>(editor, "_scenePan");
            var zoomAfterFit = Field<float>(editor, "_sceneZoom");
            viewport.Focus();
            Dispatcher.UIThread.RunJobs();
            viewport.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F });
            Dispatcher.UIThread.RunJobs();
            Check(Field<Vector2>(editor, "_scenePan") == panAfterFit && Field<float>(editor, "_sceneZoom") == zoomAfterFit,
                "F without selection must be a safe no-op.");
        }
        finally
        {
            CloseEditor(editor);
        }
    }

    private static void HierarchyTreeAndDrop()
    {
        var editor = CreateEditor();
        try
        {
            var scene = EditScene(editor);
            var parent = scene.AddEmpty();
            parent.Rename("Parent");
            var child = scene.AddEmpty();
            child.Rename("Child");
            child.SetParent(parent);
            var sibling = scene.AddEmpty();
            sibling.Rename("Sibling");
            Call(editor, "SyncHierarchyForTest");
            Dispatcher.UIThread.RunJobs();
            var tree = Control<TreeView>(editor, "SceneObjects");
            var roots = tree.Items.OfType<HierarchyNode>().ToList();
            Check(roots.Count == 2, $"Tree must show roots only, got {roots.Count}.");
            var parentNode = roots.Single(node => ReferenceEquals(node.Ref, parent));
            Check(parentNode.Children.Count == 1 && ReferenceEquals(parentNode.Children[0].Ref, child),
                "Tree must nest children under their parent.");
            Select(editor, child);
            Dispatcher.UIThread.RunJobs();
            Check(ReferenceEquals(Call(editor, "GetSelectedSceneObject"), child), "Tree selection must resolve the SceneObject.");
            Check(parentNode.IsExpanded, "Selecting a child must expand its ancestors.");
            var parentRow = tree.GetVisualDescendants().OfType<TreeViewItem>().Single(row => row.DataContext == parentNode);
            Check(parentRow.IsExpanded && parentRow.GetVisualDescendants().OfType<TreeViewItem>().Any(row => row.IsVisible),
                "Selecting a child must expand its actual row and show the child.");
            Select(editor, parent);
            parentRow.SetCurrentValue(TreeViewItem.IsExpandedProperty, false);
            Check(!parentNode.IsExpanded, "Collapsing the actual row must update the node.");
            parentRow.SetCurrentValue(TreeViewItem.IsExpandedProperty, true);
            Call(editor, "RefreshHierarchy", child.Id, null);
            Dispatcher.UIThread.RunJobs();
            Check(ReferenceEquals(Call(editor, "GetSelectedSceneObject"), child), "Refresh must preserve selection by id.");

            HierarchyDrop.Execute(scene, child.Id, null, HierarchyDropPosition.AsChild);
            Call(editor, "SyncHierarchyForTest");
            Dispatcher.UIThread.RunJobs();
            roots = [.. tree.Items.OfType<HierarchyNode>()];
            Check(roots.Count == 3, "Unparented child must return to roots.");

            var first = roots[0].Ref;
            HierarchyDrop.Execute(scene, sibling.Id, first.Id, HierarchyDropPosition.Before);
            Call(editor, "SyncHierarchyForTest");
            Dispatcher.UIThread.RunJobs();
            roots = [.. tree.Items.OfType<HierarchyNode>()];
            Check(ReferenceEquals(roots[0].Ref, sibling), "Before-drop must reorder roots in the tree.");

            child.Rename("Renamed");
            Dispatcher.UIThread.RunJobs();
            var renamed = tree.Items.OfType<HierarchyNode>().SelectMany(Enumerate).Single(node => ReferenceEquals(node.Ref, child));
            Check(tree.GetVisualDescendants().OfType<TextBlock>().Any(text => text.DataContext == renamed && text.Text == "Renamed"),
                "Visible tree labels must follow renames.");
        }
        finally
        {
            CloseEditor(editor);
        }

        static IEnumerable<HierarchyNode> Enumerate(HierarchyNode node)
        {
            yield return node;
            foreach (var nested in node.Children.SelectMany(Enumerate))
                yield return nested;
        }
    }

    private static void HierarchyRoutedDrag()
    {
        var editor = CreateEditor();
        try
        {
            var scene = EditScene(editor);
            var parent = scene.AddEmpty();
            var child = scene.AddEmpty();
            child.SetParent(parent);
            var dragged = scene.AddEmpty();
            Call(editor, "SyncHierarchyForTest");
            Select(editor, child);
            Dispatcher.UIThread.RunJobs();
            var tree = Control<TreeView>(editor, "SceneObjects");
            var surface = Control<Grid>(editor, "SceneSurface");
            TreeViewItem row(SceneObject item) => tree.GetVisualDescendants().OfType<TreeViewItem>()
                .Single(control => control.DataContext is HierarchyNode node && node.Ref == item);
            static Border header(TreeViewItem item) => item.GetTemplateDescendants().OfType<Border>()
                .Single(control => control.Name == "PART_LayoutRoot");
            static DragEventArgs drag(Interactive target, Point point, RoutedEvent<DragEventArgs> routedEvent, IDataTransfer data)
            {
                var e = new DragEventArgs(routedEvent, data, target, point, KeyModifiers.None);
                target.RaiseEvent(e);
                Dispatcher.UIThread.RunJobs();
                return e;
            }
            DragEventArgs dropOn(SceneObject target, double fraction, RoutedEvent<DragEventArgs> routedEvent, IDataTransfer data)
            {
                var targetHeader = header(row(target));
                return drag(targetHeader, new Point(targetHeader.Bounds.Width / 2, targetHeader.Bounds.Height * fraction), routedEvent, data);
            }
            using var data = new DataTransfer();
            var format = (DataFormat<string>)typeof(MainWindow).GetField("SceneObjectIdFormat", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            data.Add(DataTransferItem.Create(format, dragged.Id.ToString("D")));
            var parentRow = row(parent);
            var parentHeader = header(parentRow);
            var height = parentHeader.Bounds.Height;
            Check(parentRow.Bounds.Height > height, "Drop geometry check must include expanded descendants.");
            Check(dropOn(parent, 0.5, DragDrop.DragEnterEvent, data).DragEffects == DragDropEffects.Move
                && parentRow.Classes.Contains("drop-as-child"), "Routed enter on an expanded header center must allow child drop.");
            Check(dropOn(parent, 0.1, DragDrop.DragOverEvent, data).DragEffects == DragDropEffects.Move
                && parentRow.Classes.Contains("drop-before"), "Parent handlers must preserve a hierarchy Move.");
            Check(parentHeader.BoxShadow.Count == 1 && parentHeader.BoxShadow[0].OffsetY == 2
                && parentHeader.Bounds.Height == height, "Before indicator must render above the header without resizing it.");
            _ = dropOn(parent, 0.9, DragDrop.DragOverEvent, data);
            Check(parentRow.Classes.Contains("drop-after") && parentHeader.BoxShadow.Count == 1
                && parentHeader.BoxShadow[0].OffsetY == -2 && parentHeader.Bounds.Height == height,
                "After indicator must render below the header without resizing it.");
            // Drop recomputes its position instead of using the previous After hover.
            Check(dropOn(parent, 0.5, DragDrop.DropEvent, data).DragEffects == DragDropEffects.Move
                && dragged.Parent == parent, "Routed drop must parent the object and keep Move through ancestor handlers.");
            Check(row(parent).IsExpanded && row(dragged).IsVisible, "Dropped child must be visible and its parent expanded.");
            Check(ReferenceEquals(Call(editor, "GetSelectedSceneObject"), dragged), "Drop must select the moved object.");

            Check(dropOn(dragged, 0.5, DragDrop.DragOverEvent, data).DragEffects == DragDropEffects.None,
                "Self drop must be rejected through the routed path.");
            using var parentData = new DataTransfer();
            parentData.Add(DataTransferItem.Create(format, parent.Id.ToString("D")));
            Check(dropOn(child, 0.5, DragDrop.DropEvent, parentData).DragEffects == DragDropEffects.None && parent.Parent is null,
                "Descendant drop must be rejected without changing the hierarchy.");
            var empty = new Point(2, surface.Bounds.Height - 2);
            Check(drag(surface, empty, DragDrop.DropEvent, data).DragEffects == DragDropEffects.Move && dragged.Parent is null,
                "The outer empty margin of Stuffs must accept root drops.");
            Check(dropOn(parent, 0.1, DragDrop.DropEvent, data).DragEffects == DragDropEffects.Move && scene.RootObjects[0] == dragged,
                "Routed before drop must reorder roots.");
            Check(dropOn(parent, 0.9, DragDrop.DropEvent, data).DragEffects == DragDropEffects.Move && scene.RootObjects[^1] == dragged,
                "Routed after drop must reorder roots.");

            parentRow = row(parent);
            parentRow.SetCurrentValue(TreeViewItem.IsExpandedProperty, false);
            Dispatcher.UIThread.RunJobs();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 900 && !parentRow.IsExpanded)
            {
                _ = dropOn(parent, 0.5, DragDrop.DragOverEvent, data);
                using var slice = new CancellationTokenSource(80);
                Dispatcher.UIThread.MainLoop(slice.Token);
            }
            Check(parentRow.IsExpanded, "Continuous hover events must expand the actual row after 500ms.");
            parentRow.SetCurrentValue(TreeViewItem.IsExpandedProperty, false);
            _ = drag(surface, empty, DragDrop.DragOverEvent, data);
            _ = dropOn(parent, 0.5, DragDrop.DragOverEvent, data);
            Check(Field<DispatcherTimer>(editor, "_hierarchyExpandTimer").IsEnabled, "Collapsed parent must start hover timer.");
            _ = drag(surface, new Point(-10, -10), DragDrop.DragLeaveEvent, data);
            Check(!Field<DispatcherTimer>(editor, "_hierarchyExpandTimer").IsEnabled
                && !parentRow.Classes.Contains("drop-as-child"), "Leaving Stuffs must clear the timer and highlight.");

            using var componentData = new DataTransfer();
            var componentFormat = (DataFormat<Type>)typeof(MainWindow).GetField("ComponentFormat", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            componentData.Add(DataTransferItem.Create(componentFormat, typeof(Transform)));
            Check(dropOn(parent, 0.5, DragDrop.DragOverEvent, componentData).DragEffects == DragDropEffects.Copy,
                "Component drags must keep their existing Copy behavior.");
            Check(dropOn(parent, 0.5, DragDrop.DropEvent, componentData).DragEffects == DragDropEffects.Copy
                && parent.GetComponent<Transform>() is not null, "Component drop must still attach to the hierarchy row.");
            Call(editor, "StartPlay");
            Check(dropOn(parent, 0.5, DragDrop.DropEvent, data).DragEffects == DragDropEffects.None && dragged.Parent is null,
                "Play must reject a routed hierarchy drop.");
            Call(editor, "StopPlay");
        }
        finally
        {
            CloseEditor(editor);
        }

        var retainedScene = new Scene();
        _ = retainedScene.AddEmpty();
        var oldNode = buildWeakNode(retainedScene);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Check(!oldNode.IsAlive, "A retained scene must not keep discarded hierarchy nodes alive through event subscriptions.");
        GC.KeepAlive(retainedScene);

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static WeakReference buildWeakNode(Scene scene) => new(StuffsHierarchy.Build(scene)[0]);
    }

    private static void BareParentGizmo()
    {
        var editor = CreateEditor();
        try
        {
            var scene = EditScene(editor);
            var parent = scene.AddEmpty();
            parent.Rename("Group");
            parent.Attach(new Transform { LocalPosition = new Vector3(100, 50, 3) });
            var child = AddCard(editor, scene, new Vector3(10, 20, 0));
            child.SetParent(parent);
            Call(editor, "SyncHierarchyForTest");
            Select(editor, parent);
            Dispatcher.UIThread.RunJobs();
            EditStore(editor).MarkClean();

            // Stuffs selection of a Transform-only parent must expose a gizmo frame (no rect, pivot + axes).
            var tryFrame = typeof(MainWindow).GetMethod("TrySceneFrame", AnyInstance)!;
            var viewport = new Vector2(400, 200);
            object?[] parentFrame = [parent, viewport, null, Vector2.Zero, Vector2.UnitX, Vector2.UnitY, false];
            Check((bool)tryFrame.Invoke(editor, parentFrame)! && (bool)parentFrame[6]!
                && ((Vector2[])parentFrame[2]!).Length == 0 && (Vector2)parentFrame[3]! == new Vector2(100, 50),
                "Transform-only parent must expose a gizmo pivot without a selection rect.");
            object?[] childFrame = [child, viewport, null, Vector2.Zero, Vector2.UnitX, Vector2.UnitY, false];
            Check((bool)tryFrame.Invoke(editor, childFrame)! && ((Vector2[])childFrame[2]!).Length == 4,
                "UI child must keep its four-corner selection frame.");

            // The gizmo drag path must move the parent and carry the child layout along.
            var press = new Vector2(200, 100);
            Check((bool)Call(editor, "TryBeginMoveForTest", parent, SceneViewMath.GizmoKind.XY, press)!, "Transform-only parent must begin a gizmo move.");
            var movedView = press + new Vector2(10, 15);
            Check((bool)Call(editor, "TryUpdateMoveForTest", movedView)!, "Bare parent move must stay active.");
            var transform = parent.GetComponent<Transform>()!;
            Check(transform.LocalPosition == new Vector3(110, 65, 3),
                $"Bare XY move must preserve Z, got {transform.LocalPosition}.");
            Check((bool)Call(editor, "TryConfirmMoveForTest", movedView)!, "Bare confirm must report a change.");
            Check(EditStore(editor).IsDirty, "Confirmed bare moves must dirty the scene.");
            var entries = SceneViewMath.EnumerateLayouts(scene, viewport);
            var childEntry = entries.Single(entry => ReferenceEquals(entry.Object, child));
            Check(SceneViewMath.TryGetSceneCorners(childEntry, out var corners) && corners[0] == new Vector2(120, 85),
                $"Child layout must follow the moved parent, got {corners[0]}.");

            // Ancestor edits mid-drag cancel instead of landing on a stale layout.
            EditStore(editor).MarkClean();
            Check((bool)Call(editor, "TryBeginMoveForTest", parent, SceneViewMath.GizmoKind.X, press)!, "Second bare move must begin.");
            Call(editor, "TryUpdateMoveForTest", press + new Vector2(7, 9));
            transform.LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4);
            Check(!(bool)Call(editor, "TryUpdateMoveForTest", press + new Vector2(8, 9))!,
                "Rotation edits mid-drag must cancel the bare move.");
            Check(transform.LocalPosition == new Vector3(110, 65, 3), "Cancelled bare move must restore the start position.");
            Check(!EditStore(editor).IsDirty, "Cancelled bare move must not dirty.");
            Call(editor, "CancelSceneViewDrag");
        }
        finally
        {
            CloseEditor(editor);
        }
    }

    private static byte[] CreatePng(SKColor color)
    {
        using var bitmap = new SKBitmap(8, 8);
        bitmap.Erase(color);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void SaveClonePreserveZ()
    {
        using var components = new ProjectComponents();
        var serializer = new SceneSerializer(components.Registry);
        var scene = new Scene();
        var parent = scene.AddEmpty();
        parent.Rename("Parent");
        parent.Attach(new Transform { LocalPosition = new Vector3(11, 22, 3) });
        parent.Attach(new UiElement { Pivot = Vector2.Zero, SizeDelta = new Vector2(120, 60) });
        var imageId = Guid.NewGuid();
        parent.Attach(new PureEngine.Core.Image { Sprite = new Sprite(imageId), Order = 2 });
        var child = scene.AddEmpty();
        child.Rename("Child");
        child.SetParent(parent);
        child.Attach(new Transform { LocalPosition = new Vector3(5, 6, 7) });
        child.Attach(new UiElement { Pivot = Vector2.Zero, SizeDelta = new Vector2(20, 10) });
        child.Attach(new PureEngine.Core.Image { Sprite = new Sprite(imageId), Order = -4 });
        var yaml = serializer.Serialize(scene);
        var restored = serializer.Deserialize(yaml);
        Check(restored.Objects.First(item => item.Name == "Parent").GetComponent<Transform>()!.LocalPosition.Z == 3,
            "Save roundtrip must preserve a non-zero parent Z.");
        Check(restored.Objects.First(item => item.Name == "Child").GetComponent<Transform>()!.LocalPosition == new Vector3(5, 6, 7),
            "Save roundtrip must preserve a non-zero child Z.");
        Check(restored.Objects.First(item => item.Name == "Parent").GetComponent<PureEngine.Core.Image>()!.Order == 2
            && restored.Objects.First(item => item.Name == "Child").GetComponent<PureEngine.Core.Image>()!.Order == -4,
            "Save roundtrip must preserve render Order including negatives.");
        Check(serializer.Serialize(restored) == yaml, "Order save/load must be stable.");
        var clone = serializer.Clone(scene);
        var cloneChild = clone.Objects.First(item => item.Name == "Child");
        Check(cloneChild.GetComponent<Transform>()!.LocalPosition.Z == 7, "Clone must preserve a non-zero Z.");
        Check(clone.Objects.First(item => item.Name == "Parent").GetComponent<PureEngine.Core.Image>()!.Order == 2
            && cloneChild.GetComponent<PureEngine.Core.Image>()!.Order == -4,
            "Clone must preserve render Order.");
        Check(!ReferenceEquals(cloneChild, child)
            && !ReferenceEquals(cloneChild.GetComponent<Transform>(), child.GetComponent<Transform>()),
            "Clone must separate Transform instances.");
        using var draw = new DrawList();
        var images = new Dictionary<Guid, byte[]> { [imageId] = CreatePng(SKColors.White) };
        var viewport = new Vector2(400, 200);
        var diagnostics = EditSceneRenderer.Build(draw, restored, images, viewport, SceneViewMath.ViewMatrix(Vector2.Zero, 1f));
        Check(diagnostics.Count == 0, "View-aware drawing must accept the restored scene.");
        var restoredEntries = SceneViewMath.EnumerateLayouts(restored, viewport);
        var restoredOrdered = SceneViewMath.SortForRender(restoredEntries);
        Check(restoredOrdered.Count == 2
            && ReferenceEquals(restoredOrdered[0].Object, restored.Objects.First(item => item.Name == "Child"))
            && ReferenceEquals(restoredOrdered[1].Object, restored.Objects.First(item => item.Name == "Parent")),
            "Restored Order must still sort the child behind its parent.");
        static bool IsRestoredImage(SceneObject item) => item.GetComponent<PureEngine.Core.Image>() is { Sprite: not null };
        Check(ReferenceEquals(SceneViewMath.HitTest(restoredEntries, viewport, Vector2.Zero, 1f,
            new Vector2(20, 30), IsRestoredImage), restored.Objects.First(item => item.Name == "Parent")),
            "Restored overlap click must pick the frontmost Order.");
        var moved = restored.Objects.First(item => item.Name == "Child").GetComponent<Transform>()!;
        moved.LocalPosition = new Vector3(50, 60, 7);
        var movedYaml = serializer.Serialize(restored);
        var movedBack = serializer.Deserialize(movedYaml);
        Check(movedBack.Objects.First(item => item.Name == "Child").GetComponent<Transform>()!.LocalPosition == new Vector3(50, 60, 7),
            "Moved positions must survive save/reopen with Z intact.");

        // Real file path: reproduce Z and view rendering via project creation, image import, placement, save, and reopen.
        var root = Path.Combine(Path.GetTempPath(), "PureEngine-SceneView-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var parentDir = Path.Combine(root, "proj-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(parentDir);
            var project = ProjectFile.Create(parentDir, "SceneView", serializer.Serialize(new Scene()));
            var source = Path.Combine(root, "card.png");
            File.WriteAllBytes(source, CreatePng(SKColors.CornflowerBlue));
            var imported = ProjectAssets.ImportImage(project.RootDirectory, source);
            var assets = ProjectAssets.Scan(project.RootDirectory);
            Check(assets.Images.ContainsKey(imported.Id), "Scene view asset import must resolve.");
            var projectImages = assets.LoadImageBytes();
            var projectScene = new Scene();
            var card = projectScene.AddEmpty();
            card.Rename("Card");
            card.Attach(new Transform { LocalPosition = new Vector3(20, 30, 4) });
            card.Attach(new UiElement { Pivot = Vector2.Zero, SizeDelta = new Vector2(120, 60) });
            card.Attach(new PureEngine.Core.Image { Sprite = new Sprite(imported.Id), Order = 1 });
            var badge = projectScene.AddEmpty();
            badge.Rename("Badge");
            badge.SetParent(card);
            badge.Attach(new Transform { LocalPosition = new Vector3(10, 10, 6) });
            badge.Attach(new UiElement { Pivot = Vector2.Zero, SizeDelta = new Vector2(24, 24) });
            badge.Attach(new PureEngine.Core.Image { Sprite = new Sprite(imported.Id), Order = -2 });
            var view = SceneViewMath.ViewMatrix(new Vector2(30, -15), 2f);
            using var beforeDraw = new DrawList();
            Check(EditSceneRenderer.Build(beforeDraw, projectScene, projectImages, viewport, view).Count == 0
                && beforeDraw.Vertices.Length == 12, "Panned/zoomed preview must draw parent and child.");
            var beforeVertices = beforeDraw.Vertices.ToArray();
            SceneFile.Write(project.StartupScenePath, serializer.Serialize(projectScene));
            using var session = ProjectSession.Open(project.ManifestPath);
            var reopenedCard = session.Scene.Objects.First(item => item.Name == "Card");
            Check(reopenedCard.GetComponent<Transform>()!.LocalPosition == new Vector3(20, 30, 4),
                "Project reopen must restore the card position with Z intact.");
            Check(session.Scene.Objects.First(item => item.Name == "Badge").GetComponent<Transform>()!.LocalPosition.Z == 6,
                "Project reopen must restore a non-zero child Z.");
            Check(reopenedCard.GetComponent<PureEngine.Core.Image>()!.Order == 1
                && session.Scene.Objects.First(item => item.Name == "Badge").GetComponent<PureEngine.Core.Image>()!.Order == -2,
                "Project reopen must restore render Order.");
            var reopenedAssets = ProjectAssets.Scan(project.RootDirectory);
            using var afterDraw = new DrawList();
            Check(EditSceneRenderer.Build(afterDraw, session.Scene, reopenedAssets.LoadImageBytes(), viewport, view).Count == 0
                && afterDraw.Vertices.Length == beforeVertices.Length, "Reopen must draw the same panned/zoomed quads.");
            for (var i = 0; i < beforeVertices.Length; i++)
                Check(afterDraw.Vertices[i].Position == beforeVertices[i].Position
                    && afterDraw.Vertices[i].Color == beforeVertices[i].Color,
                    $"Reopen display differs at vertex {i} under pan/zoom.");
        }
        finally
        {
            if (Path.GetFileName(root).StartsWith("PureEngine-SceneView-", StringComparison.Ordinal))
                Directory.Delete(root, recursive: true);
        }
    }
}
