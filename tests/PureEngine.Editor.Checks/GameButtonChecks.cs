using System.Numerics;
using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PureEngine.Core;
using PureEngine.Core.Components;
using PureEngine.Editor;
using PureEngine.Rendering;
using PureEngine.Runtime;
using SkiaSharp;

internal static class GameButtonChecks
{
    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static object? Call(MainWindow window, string method, params object?[] args) =>
        typeof(MainWindow).GetMethod(method, AnyInstance)!.Invoke(window, args);
    private static T Field<T>(MainWindow window, string name) =>
        (T)(typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) is { } field
            ? field.GetValue(window) : typeof(MainWindow).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(window))!;
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
        AddSearchAttachAndInspector();
        SaveCloneRoundTrip();
        GameRenderStates();
        PlayClickFlow();
        OverlapAndParent();
        KeyboardFlow();
        InputBoundaryRegressions();
        HandlerEndingsAndReplay();
        Console.WriteLine("PASS: Button add/search/inspector/save/clone, Game render states, overlap/parent/cancel/disable clicks, keyboard focus/activate, handler endings, replay init, and repeated Play/Stop.");
    }

    private static MainWindow CreateEditor()
    {
        _ = Log.Drain();
        var editor = new MainWindow { WindowState = WindowState.Normal, Width = 1280, Height = 800 };
        Control<Grid>(editor, "SceneViewport").Children.Clear();
        Control<Grid>(editor, "GameViewport").Children.Clear();
        editor.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        _ = Log.Drain();
        return editor;
    }

    private static void CloseEditor(MainWindow editor)
    {
        EditStore(editor).MarkClean();
        editor.Close();
        Dispatcher.UIThread.RunJobs();
        var dialog = editor.OwnedWindows.SingleOrDefault(window => window.Title == "Unsaved Scene");
        if (dialog is not null)
        {
            dialog.GetVisualDescendants().OfType<Avalonia.Controls.Button>()
                .Single(button => Equals(button.Content, "Discard"))
                .RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void StopTimer(MainWindow editor)
    {
        Field<DispatcherTimer?>(editor, "_playTimer")?.Stop();
        Dispatcher.UIThread.RunJobs();
    }

    private static void StartPlayWithCallbacks(MainWindow editor)
    {
        Call(editor, "StartPlay");
        var runtime = Field<PlaySession>(editor, "_play").Runtime;
        foreach (var item in runtime.Scene.Objects)
        {
            if (item.GetComponent<PureEngine.Core.Components.Button>() is not { } button) continue;
            if (item.GetComponent<GameClickCounter>() is { } counter) button.Clicked += counter.OnClick;
            if (item.GetComponent<GameThrower>() is not null) button.Clicked += GameThrower.OnClick;
            if (item.GetComponent<GameRemover>() is not null) button.Clicked += GameRemover.OnClick;
        }
    }

    private static void EnsureHandlers(MainWindow editor)
    {
        var components = Field<ProjectComponents>(editor, "_components");
        foreach (var (type, id) in new[] { (typeof(GameClickCounter), "checks.game-click"), (typeof(GameThrower), "checks.game-throw"),
            (typeof(GameRemover), "checks.game-remove") })
        {
            if (!components.Registry.Ids.Contains(id))
                components.Registry.RegisterType(type, id);
        }
    }

    private static SceneObject AddCard(MainWindow editor, Scene scene, string name, Vector3 position, Vector2 size, int order, bool interactable, bool withImage)
    {
        var owner = Field<ProjectComponents>(editor, "_components");
        var services = Field<GameSession>(editor, "EditSession");
        var item = scene.AddEmpty();
        item.Rename(name);
        Check(owner.TryAttach(item, typeof(Transform), services.Factory), $"Attach Transform to {name}.");
        Check(owner.TryAttach(item, typeof(UiElement), services.Factory), $"Attach UiElement to {name}.");
        item.GetComponent<UiElement>()!.Pivot = Vector2.Zero;
        item.GetComponent<UiElement>()!.SizeDelta = size;
        item.GetComponent<Transform>()!.LocalPosition = position;
        Check(owner.TryAttach(item, typeof(PureEngine.Core.Components.Button), services.Factory), $"Attach Button to {name}.");
        item.GetComponent<PureEngine.Core.Components.Button>()!.Interactable = interactable;
        if (withImage)
        {
            Check(owner.TryAttach(item, typeof(global::Image), services.Factory), $"Attach Image to {name}.");
            item.GetComponent<global::Image>()!.Sprite = new Sprite(Guid.NewGuid());
            item.GetComponent<global::Image>()!.Order = order;
        }
        return item;
    }

    private static void ShowGameTab(MainWindow editor)
    {
        Control<TabControl>(editor, "ViewportTabs").SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
    }

    private static Vector2 GameSize(MainWindow editor)
    {
        var bounds = Control<Grid>(editor, "GameViewport").Bounds.Size;
        return new Vector2((float)bounds.Width, (float)bounds.Height);
    }

    private static void AddSearchAttachAndInspector()
    {
        var editor = CreateEditor();
        try
        {
            var components = Field<ProjectComponents>(editor, "_components");
            var found = ComponentAssets.SearchCandidates(components.Registry, "button");
            Check(found.Any(entry => entry.Type == typeof(PureEngine.Core.Components.Button) && entry.TypeId == "core.button"),
                "Add Component search for 'button' must list the Button component.");
            var scene = EditScene(editor);
            var item = scene.AddEmpty();
            item.Rename("Button card");
            Call(editor, "SyncHierarchyForTest");
            Select(editor, item);
            Dispatcher.UIThread.RunJobs();
            var services = Field<GameSession>(editor, "EditSession");
            Check(components.TryAttach(item, typeof(PureEngine.Core.Components.Button), services.Factory), "Button must attach through the existing factory path.");
            Check(!components.TryAttach(item, typeof(PureEngine.Core.Components.Button), services.Factory), "Duplicate Button must be refused.");
            Select(editor, null);
            Dispatcher.UIThread.RunJobs();
            Select(editor, item);
            Dispatcher.UIThread.RunJobs();
            var toggle = editor.GetVisualDescendants().OfType<CheckBox>()
                .Single(box => Equals(box.GetValue(AutomationProperties.NameProperty) as string, "Button.Interactable"));
            Check(toggle.IsChecked == true, "Interactable must default to true in the Inspector.");
            EditStore(editor).MarkClean();
            toggle.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Check(item.GetComponent<PureEngine.Core.Components.Button>()!.Interactable == false, "Inspector must edit Interactable.");
            Check(EditStore(editor).IsDirty, "Interactable edits must dirty the scene.");
            var warning = editor.GetVisualDescendants().OfType<TextBlock>()
                .Single(block => Equals(block.GetValue(AutomationProperties.NameProperty) as string, "Button.Requirements"));
            Check(warning.IsVisible && warning.Text!.Contains("Transform"), "Button without Transform/UiElement must warn.");
            components.TryAttach(item, typeof(Transform), services.Factory);
            components.TryAttach(item, typeof(UiElement), services.Factory);
            Select(editor, null);
            Dispatcher.UIThread.RunJobs();
            Select(editor, item);
            Dispatcher.UIThread.RunJobs();
            warning = editor.GetVisualDescendants().OfType<TextBlock>()
                .Single(block => Equals(block.GetValue(AutomationProperties.NameProperty) as string, "Button.Requirements"));
            Check(!warning.IsVisible, "Complete Button combinatons must clear the warning.");
            CloseEditor(editor);
        }
        finally
        {
            if (editor.IsVisible) CloseEditor(editor);
        }
    }

    private static void SaveCloneRoundTrip()
    {
        var editor = CreateEditor();
        try
        {
            EnsureHandlers(editor);
            var components = Field<ProjectComponents>(editor, "_components");
            var scene = EditScene(editor);
            var item = AddCard(editor, scene, "Save me", new Vector3(10, 20, 0), new Vector2(100, 40), 3, false, true);
            item.GetComponent<global::Image>()!.Color = new Vector4(0.2f, 0.4f, 0.6f, 1f);
            var serializer = new SceneSerializer(components.Registry);
            var yaml = serializer.Serialize(scene);
            Check(yaml.Contains("core.button") && yaml.Contains("Interactable"), "Save must carry the Button type and Interactable.");
            var restored = serializer.Deserialize(yaml);
            var copy = restored.Objects.First(candidate => candidate.Name == "Save me");
            Check(copy.GetComponent<PureEngine.Core.Components.Button>()!.Interactable == false, "Interactable must survive save/reopen.");
            Check(copy.GetComponent<global::Image>()!.Order == 3, "Order must survive alongside Button state.");
            Check(serializer.Serialize(restored) == yaml, "Button save/load must be stable.");
            var clone = serializer.Clone(scene);
            var cloneButton = clone.Objects.First(candidate => candidate.Name == "Save me").GetComponent<PureEngine.Core.Components.Button>()!;
            Check(!cloneButton.Interactable && !ReferenceEquals(cloneButton, item.GetComponent<PureEngine.Core.Components.Button>()),
                "Clone must separate transient-free Button state.");
            item.GetComponent<PureEngine.Core.Components.Button>()!.Interactable = true;
            Check(!cloneButton.Interactable, "Editing after Clone must not leak into the clone.");
            CloseEditor(editor);
        }
        finally
        {
            if (editor.IsVisible) CloseEditor(editor);
        }
    }

    private static byte[] CreatePng(SKColor? color = null)
    {
        using var bitmap = new SKBitmap(16, 16);
        bitmap.Erase(color ?? SKColors.White);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static SceneObject RenderCard(Scene scene, Guid imageId, string name, Vector3 position, Vector2 size, int order, bool interactable)
    {
        var item = scene.AddEmpty();
        item.Rename(name);
        item.Attach(new Transform { LocalPosition = position });
        item.Attach(new UiElement { Pivot = Vector2.Zero, SizeDelta = size });
        item.Attach(new global::Image { Sprite = new Sprite(imageId), Color = new Vector4(0.2f, 0.4f, 0.6f, 1f), Order = order });
        item.Attach(new PureEngine.Core.Components.Button { Interactable = interactable });
        return item;
    }

    private static void GameRenderStates()
    {
        var imageId = Guid.NewGuid();
        var images = new Dictionary<Guid, byte[]> { [imageId] = CreatePng() };
        var viewport = new Vector2(400, 200);
        using var draw = new DrawList();

        var scene = new Scene();
        var item = RenderCard(scene, imageId, "State", new Vector3(10, 20, 0), new Vector2(100, 40), 0, true);
        var savedColor = item.GetComponent<global::Image>()!.Color;
        Check(GameSceneRenderer.Build(draw, scene, images, viewport).Count == 0 && draw.Vertices.Length == 6,
            "Normal buttons must draw the base image with no overlay.");
        var states = new Dictionary<Guid, GameSceneRenderer.ButtonVisual>
        {
            [item.Id] = new(UiButtonVisualState.Hover, false),
        };
        Check(GameSceneRenderer.Build(draw, scene, images, viewport, states).Count == 0 && draw.Vertices.Length == 12,
            "Hover must add one overlay quad without touching the base order.");
        Check(item.GetComponent<global::Image>()!.Color == savedColor, "State display must never rewrite the saved Image.Color.");
        states[item.Id] = new(UiButtonVisualState.Pressed, false);
        Check(GameSceneRenderer.Build(draw, scene, images, viewport, states).Count == 0 && draw.Vertices.Length == 12
            && draw.Vertices[6].Color != draw.Vertices[0].Color, "Pressed must differ from the base image color.");
        states[item.Id] = new(UiButtonVisualState.Disabled, false);
        GameSceneRenderer.Build(draw, scene, images, viewport, states);
        Check(draw.Vertices.Length == 12, "Disabled must draw its overlay.");
        states[item.Id] = new(UiButtonVisualState.Normal, true);
        GameSceneRenderer.Build(draw, scene, images, viewport, states);
        var hasFocus = false;
        foreach (var vertex in draw.Vertices)
        {
            if (vertex.Color == UiButtonVisuals.FocusOutline)
            {
                hasFocus = true;
                break;
            }
        }
        Check(draw.Vertices.Length > 6 && hasFocus,
            "Keyboard focus must draw the focus outline.");

        var ordered = new Scene();
        var back = RenderCard(ordered, imageId, "Back", new Vector3(10, 20, 0), new Vector2(100, 40), 0, true);
        var front = RenderCard(ordered, imageId, "Front", new Vector3(10, 20, 0), new Vector2(100, 40), 5, true);
        var frontStates = new Dictionary<Guid, GameSceneRenderer.ButtonVisual> { [front.Id] = new(UiButtonVisualState.Hover, false) };
        Check(GameSceneRenderer.Build(draw, ordered, images, viewport, frontStates).Count == 0, "Ordered render must succeed.");
        Check(draw.Vertices[0].Color == back.GetComponent<global::Image>()!.Color
            && draw.Vertices[6].Color == front.GetComponent<global::Image>()!.Color,
            "Game order must follow the shared Order ascending path.");

        var imageless = new Scene();
        var ghost = imageless.AddEmpty();
        ghost.Rename("Ghost");
        ghost.Attach(new Transform { LocalPosition = new Vector3(10, 20, 0) });
        ghost.Attach(new UiElement { Pivot = Vector2.Zero, SizeDelta = new Vector2(100, 40) });
        ghost.Attach(new PureEngine.Core.Components.Button());
        Check(GameSceneRenderer.Build(draw, imageless, images, viewport).Count == 0 && draw.Vertices.IsEmpty,
            "Imageless buttons draw nothing when idle.");
        var ghostStates = new Dictionary<Guid, GameSceneRenderer.ButtonVisual> { [ghost.Id] = new(UiButtonVisualState.Normal, true) };
        Check(GameSceneRenderer.Build(draw, imageless, images, viewport, ghostStates).Count == 0 && !draw.Vertices.IsEmpty,
            "Imageless buttons must still show the keyboard focus outline.");
    }

    private static GameClickCounter RuntimeCounter(MainWindow editor, string name)
    {
        var play = Field<PlaySession?>(editor, "_play")!;
        return play.Runtime.Scene.Objects.First(candidate => candidate.Name == name).GetComponent<GameClickCounter>()!;
    }

    private static void PlayClickFlow()
    {
        GameClickCounter.Reset();
        var editor = CreateEditor();
        try
        {
            EnsureHandlers(editor);
            var scene = EditScene(editor);
            var owner = Field<ProjectComponents>(editor, "_components");
            var services = Field<GameSession>(editor, "EditSession");
            var item = AddCard(editor, scene, "Click me", new Vector3(10, 20, 0), new Vector2(100, 40), 0, true, true);
            Check(owner.TryAttach(item, typeof(GameClickCounter), services.Factory), "Handler must attach beside the Button.");
            Dispatcher.UIThread.RunJobs();
            StartPlayWithCallbacks(editor);
            StopTimer(editor);
            Check((bool)Call(editor, "get_IsPlaying")!, "Play is required for Game input.");
            ShowGameTab(editor);
            Check(GameSize(editor).X >= 10, "Game viewport must be drawable in the Game tab.");
            var before = RuntimeCounter(editor, "Click me");
            Check(before.Calls == 0, "Handler must not run before input.");

            Check((bool)Call(editor, "TryGamePressForTest", new Vector2(50, 40))!, "Press inside must start.");
            Check((bool)Call(editor, "TryGameReleaseForTest", new Vector2(50, 40))!, "Release on the same button must click once.");
            Call(editor, "StepPlayOnce", 1f / 60f);
            Check(RuntimeCounter(editor, "Click me").Calls == 1, "Click must dispatch once at the update boundary.");
            Check(item.GetComponent<GameClickCounter>()!.Calls == 0, "Execution must not leak into authoring data.");

            Check((bool)Call(editor, "TryGamePressForTest", new Vector2(50, 40))!, "Second press must start.");
            Check(!(bool)Call(editor, "TryGameReleaseForTest", new Vector2(500, 500))!, "Release outside must cancel without clicking.");
            Call(editor, "StepPlayOnce", 1f / 60f);
            Check(RuntimeCounter(editor, "Click me").Calls == 1, "Cancelled presses must never notify.");

            Check((bool)Call(editor, "TryGamePressForTest", new Vector2(50, 40))!, "Press before focus loss must start.");
            Call(editor, "CancelGamePress");
            Call(editor, "StepPlayOnce", 1f / 60f);
            Check(RuntimeCounter(editor, "Click me").Calls == 1, "Focus/capture loss must cancel the press.");

            var game = Control<Grid>(editor, "GameViewport");
            var screen = game.TranslatePoint(new Point(50, 40), editor)!.Value;
            editor.MouseDown(screen, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            editor.MouseUp(screen, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Call(editor, "StepPlayOnce", 1f / 60f);
            Check(RuntimeCounter(editor, "Click me").Calls == 2, "Real pointer press/release must click once.");

            Call(editor, "StopPlay");
            Dispatcher.UIThread.RunJobs();
            Check(!(bool)Call(editor, "get_IsPlaying")!, "Stop must end Play.");
            Check(Field<Guid?>(editor, "GameFocusedForTest") is null && Field<Guid?>(editor, "GamePressedForTest") is null,
                "Stop must reset transient Game input.");
            CloseEditor(editor);
        }
        finally
        {
            if (editor.IsVisible) CloseEditor(editor);
        }
    }

    private static void OverlapAndParent()
    {
        GameClickCounter.Reset();
        var editor = CreateEditor();
        try
        {
            EnsureHandlers(editor);
            var scene = EditScene(editor);
            var owner = Field<ProjectComponents>(editor, "_components");
            var services = Field<GameSession>(editor, "EditSession");
            var back = AddCard(editor, scene, "Back", new Vector3(10, 20, 0), new Vector2(100, 40), 0, true, true);
            owner.TryAttach(back, typeof(GameClickCounter), services.Factory);
            var front = AddCard(editor, scene, "Front", new Vector3(10, 20, 0), new Vector2(100, 40), 5, true, true);
            owner.TryAttach(front, typeof(GameClickCounter), services.Factory);
            var parent = scene.AddEmpty();
            parent.Rename("Parent");
            owner.TryAttach(parent, typeof(Transform), services.Factory);
            owner.TryAttach(parent, typeof(UiElement), services.Factory);
            parent.GetComponent<Transform>()!.LocalPosition = new Vector3(200, 100, 0);
            parent.GetComponent<Transform>()!.LocalScale = new Vector3(2, 2, 1);
            parent.GetComponent<UiElement>()!.Pivot = Vector2.Zero;
            parent.GetComponent<UiElement>()!.SizeDelta = new Vector2(200, 100);
            var child = scene.AddEmpty();
            child.Rename("Child");
            child.SetParent(parent);
            owner.TryAttach(child, typeof(Transform), services.Factory);
            owner.TryAttach(child, typeof(UiElement), services.Factory);
            child.GetComponent<Transform>()!.LocalPosition = new Vector3(10, 10, 0);
            child.GetComponent<UiElement>()!.Pivot = Vector2.Zero;
            child.GetComponent<UiElement>()!.SizeDelta = new Vector2(20, 10);
            owner.TryAttach(child, typeof(PureEngine.Core.Components.Button), services.Factory);
            owner.TryAttach(child, typeof(GameClickCounter), services.Factory);
            var ghost = scene.AddEmpty();
            ghost.Rename("Ghost");
            owner.TryAttach(ghost, typeof(Transform), services.Factory);
            owner.TryAttach(ghost, typeof(UiElement), services.Factory);
            ghost.GetComponent<Transform>()!.LocalPosition = new Vector3(300, 50, 0);
            ghost.GetComponent<UiElement>()!.Pivot = Vector2.Zero;
            ghost.GetComponent<UiElement>()!.SizeDelta = new Vector2(40, 20);
            owner.TryAttach(ghost, typeof(PureEngine.Core.Components.Button), services.Factory);
            owner.TryAttach(ghost, typeof(GameClickCounter), services.Factory);
            var cover = scene.AddEmpty();
            cover.Rename("Cover");
            owner.TryAttach(cover, typeof(Transform), services.Factory);
            owner.TryAttach(cover, typeof(UiElement), services.Factory);
            cover.GetComponent<Transform>()!.LocalPosition = new Vector3(300, 50, 0);
            cover.GetComponent<UiElement>()!.Pivot = Vector2.Zero;
            cover.GetComponent<UiElement>()!.SizeDelta = new Vector2(40, 20);
            owner.TryAttach(cover, typeof(global::Image), services.Factory);
            cover.GetComponent<global::Image>()!.Sprite = new Sprite(Guid.NewGuid());
            cover.GetComponent<global::Image>()!.Order = 100;
            Dispatcher.UIThread.RunJobs();

            StartPlayWithCallbacks(editor);
            StopTimer(editor);
            ShowGameTab(editor);
            Check((bool)Call(editor, "TryGamePressForTest", new Vector2(50, 40))!, "Overlap press must start.");
            Check((bool)Call(editor, "TryGameReleaseForTest", new Vector2(50, 40))!, "Overlap release must click.");
            Call(editor, "StepPlayOnce", 1f / 60f);
            Check(RuntimeCounter(editor, "Front").Calls == 1 && RuntimeCounter(editor, "Back").Calls == 0,
                "Only the front overlapping button may receive input.");

            front.GetComponent<PureEngine.Core.Components.Button>()!.Interactable = false;
            Check((bool)Call(editor, "TryGamePressForTest", new Vector2(50, 40))!
                && Field<Guid?>(editor, "GamePressedForTest") == front.Id,
                "Authoring changes must not change the runtime Button.");
            Call(editor, "CancelGamePress");
            var play = Field<PlaySession?>(editor, "_play")!;
            play.Runtime.Scene.Objects.First(candidate => candidate.Name == "Front").GetComponent<PureEngine.Core.Components.Button>()!.Interactable = false;
            Check((bool)Call(editor, "TryGamePressForTest", new Vector2(50, 40))!, "Execution press must start on the back button.");
            Check((bool)Call(editor, "TryGameReleaseForTest", new Vector2(50, 40))!, "Execution release must click.");
            Call(editor, "StepPlayOnce", 1f / 60f);
            Check(RuntimeCounter(editor, "Back").Calls == 1, "Disabling the front button must fall through.");

            var entries = SceneViewMath.EnumerateLayouts(play.Runtime.Scene, GameSize(editor));
            var childEntry = entries.First(entry => entry.Object.Name == "Child");
            var plane = new Matrix3x2(childEntry.WorldScene.M11, childEntry.WorldScene.M12,
                childEntry.WorldScene.M21, childEntry.WorldScene.M22,
                childEntry.WorldScene.M41, childEntry.WorldScene.M42);
            var center = Vector2.Transform(new Vector2(10, 5), plane);
            Check((bool)Call(editor, "TryGamePressForTest", center)!, "Parent-transformed press must start.");
            Check((bool)Call(editor, "TryGameReleaseForTest", center)!, "Parent-transformed release must click.");
            Call(editor, "StepPlayOnce", 1f / 60f);
            Check(RuntimeCounter(editor, "Child").Calls == 1, "Parent rotation/scale must move visuals and hit testing together.");

            Check((bool)Call(editor, "TryGamePressForTest", new Vector2(310, 55))!, "Imageless press must start.");
            Check((bool)Call(editor, "TryGameReleaseForTest", new Vector2(310, 55))!, "Imageless release must click.");
            Call(editor, "StepPlayOnce", 1f / 60f);
            Check(RuntimeCounter(editor, "Ghost").Calls == 1, "Imageless buttons must work and covering Images must not block.");
            Call(editor, "StopPlay");
            CloseEditor(editor);
        }
        finally
        {
            if (editor.IsVisible) CloseEditor(editor);
        }
    }

    private static void RaiseGameKey(MainWindow editor, Key key, KeyModifiers modifiers, bool down)
    {
        var game = Control<Grid>(editor, "GameViewport");
        game.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = down ? InputElement.KeyDownEvent : InputElement.KeyUpEvent,
            Key = key,
            KeyModifiers = modifiers,
        });
        Dispatcher.UIThread.RunJobs();
    }

    private static void KeyboardFlow()
    {
        GameClickCounter.Reset();
        var editor = CreateEditor();
        try
        {
            EnsureHandlers(editor);
            var scene = EditScene(editor);
            var owner = Field<ProjectComponents>(editor, "_components");
            var services = Field<GameSession>(editor, "EditSession");
            var first = AddCard(editor, scene, "First", new Vector3(10, 20, 0), new Vector2(100, 40), 0, true, true);
            owner.TryAttach(first, typeof(GameClickCounter), services.Factory);
            var second = AddCard(editor, scene, "Second", new Vector3(10, 80, 0), new Vector2(100, 40), 5, true, true);
            owner.TryAttach(second, typeof(GameClickCounter), services.Factory);
            var off = AddCard(editor, scene, "Off", new Vector3(10, 140, 0), new Vector2(100, 40), 9, false, true);
            owner.TryAttach(off, typeof(GameClickCounter), services.Factory);
            Dispatcher.UIThread.RunJobs();
            StartPlayWithCallbacks(editor);
            StopTimer(editor);
            ShowGameTab(editor);
            Control<Grid>(editor, "GameViewport").Focus();
            Dispatcher.UIThread.RunJobs();

            RaiseGameKey(editor, Key.Tab, KeyModifiers.None, down: true);
            RaiseGameKey(editor, Key.Tab, KeyModifiers.None, down: false);
            var playSession = Field<PlaySession?>(editor, "_play")!;
            var firstId = playSession.Runtime.Scene.Objects.First(candidate => candidate.Name == "First").Id;
            var secondId = playSession.Runtime.Scene.Objects.First(candidate => candidate.Name == "Second").Id;
            var offId = playSession.Runtime.Scene.Objects.First(candidate => candidate.Name == "Off").Id;
            Check(Field<Guid?>(editor, "GameFocusedForTest") == firstId,
                "Tab must first focus the back-most (lowest Order) button.");
            var focused = Field<Guid?>(editor, "GameFocusedForTest");
            Check(focused is not null, "Tab must establish keyboard focus.");
            RaiseGameKey(editor, Key.Tab, KeyModifiers.None, down: true);
            RaiseGameKey(editor, Key.Tab, KeyModifiers.None, down: false);
            var moved = Field<Guid?>(editor, "GameFocusedForTest");
            Check(moved == secondId, "Tab must move focus in Order.");
            RaiseGameKey(editor, Key.Tab, KeyModifiers.Shift, down: true);
            RaiseGameKey(editor, Key.Tab, KeyModifiers.Shift, down: false);
            Check(Field<Guid?>(editor, "GameFocusedForTest") == focused, "Shift+Tab must move back.");
            Check((bool)Call(editor, "TryGameTabForTest", false)!, "Test-hook Tab must move.");
            var orderFocused = Field<Guid?>(editor, "GameFocusedForTest");
            Check(orderFocused == secondId && orderFocused != offId, "Tab order must skip Interactable=false.");

            RaiseGameKey(editor, Key.Space, KeyModifiers.None, down: true);
            RaiseGameKey(editor, Key.Space, KeyModifiers.None, down: true);
            RaiseGameKey(editor, Key.Space, KeyModifiers.None, down: false);
            Call(editor, "StepPlayOnce", 1f / 60f);
            var total = RuntimeCounter(editor, "First").Calls + RuntimeCounter(editor, "Second").Calls;
            Check(total == 1, $"Key repeat must not duplicate notifications, got {total}.");
            RaiseGameKey(editor, Key.Enter, KeyModifiers.None, down: true);
            RaiseGameKey(editor, Key.Enter, KeyModifiers.None, down: false);
            Call(editor, "StepPlayOnce", 1f / 60f);
            var after = RuntimeCounter(editor, "First").Calls + RuntimeCounter(editor, "Second").Calls;
            Check(after == 2, "Enter must also activate the focused button once.");
            Call(editor, "StopPlay");
            CloseEditor(editor);
        }
        finally
        {
            if (editor.IsVisible) CloseEditor(editor);
        }
    }

    private static void InputBoundaryRegressions()
    {
        var editor = CreateEditor();
        try
        {
            EnsureHandlers(editor);
            var item = AddCard(editor, EditScene(editor), "Input", new Vector3(10, 20, 0), new Vector2(100, 40), 0, true, false);
            item.Attach(new GameClickCounter());
            StartPlayWithCallbacks(editor);
            StopTimer(editor);
            ShowGameTab(editor);
            var game = Control<Grid>(editor, "GameViewport");
            var point = game.TranslatePoint(new Point(50, 40), editor)!.Value;
            var counter = RuntimeCounter(editor, "Input");

            editor.MouseDown(point, MouseButton.Left);
            editor.MouseDown(point, MouseButton.Right, RawInputModifiers.LeftMouseButton);
            editor.MouseUp(point, MouseButton.Right, RawInputModifiers.LeftMouseButton);
            Call(editor, "StepPlayOnce", 0f);
            Check(counter.Calls == 0 && Field<Guid?>(editor, "GamePressedForTest") == item.Id,
                $"Releasing the right button must not complete a left-button click: calls={counter.Calls}, pressed={Field<Guid?>(editor, "GamePressedForTest")}, expected={item.Id}.");
            editor.MouseUp(point, MouseButton.Left);
            Call(editor, "StepPlayOnce", 0f);
            Check(counter.Calls == 1, "Left release must complete the original click once.");

            game.Focus();
            RaiseGameKey(editor, Key.Space, KeyModifiers.None, down: true);
            RaiseGameKey(editor, Key.Enter, KeyModifiers.None, down: true);
            RaiseGameKey(editor, Key.Enter, KeyModifiers.None, down: false);
            RaiseGameKey(editor, Key.Space, KeyModifiers.None, down: true);
            RaiseGameKey(editor, Key.Space, KeyModifiers.None, down: false);
            Call(editor, "StepPlayOnce", 0f);
            Check(counter.Calls == 2, "Releasing a different activation key must not rearm held-key repeats.");
            RaiseGameKey(editor, Key.Space, KeyModifiers.None, down: true);
            RaiseGameKey(editor, Key.Tab, KeyModifiers.None, down: true);
            RaiseGameKey(editor, Key.Space, KeyModifiers.None, down: true);
            RaiseGameKey(editor, Key.Space, KeyModifiers.None, down: false);
            Call(editor, "StepPlayOnce", 0f);
            Check(counter.Calls == 3, "Tab navigation must not rearm a held activation key.");

            editor.MouseDown(point, MouseButton.Left);
            RaiseGameKey(editor, Key.Space, KeyModifiers.None, down: true);
            RaiseGameKey(editor, Key.Space, KeyModifiers.None, down: false);
            editor.MouseUp(point, MouseButton.Left);
            Call(editor, "StepPlayOnce", 0f);
            Check(counter.Calls == 4, "Keyboard activation must not duplicate an active pointer press.");

            var runtimeItem = Field<PlaySession>(editor, "_play").Runtime.Scene.Objects.Single(candidate => candidate.Id == item.Id);
            editor.MouseDown(point, MouseButton.Left);
            runtimeItem.GetComponent<PureEngine.Core.Components.Button>()!.Interactable = false;
            Call(editor, "StepPlayOnce", 0f);
            Check(Field<Guid?>(editor, "GamePressedForTest") is null,
                "Disabling during a press must immediately cancel capture and state.");
            runtimeItem.GetComponent<PureEngine.Core.Components.Button>()!.Interactable = true;
            editor.MouseUp(point, MouseButton.Left);
            Call(editor, "StepPlayOnce", 0f);
            Check(counter.Calls == 4, "Re-enabling must not resurrect a cancelled press.");

            editor.MouseMove(point);
            Check(Field<Guid?>(editor, "GameHoveredForTest") == item.Id, "Pointer should hover the Button.");
            editor.MouseMove(new Point(editor.Bounds.Width - 5, editor.Bounds.Height - 5));
            Check(Field<Guid?>(editor, "GameHoveredForTest") is null, "Leaving Game must clear hover.");

            game.Focus();
            runtimeItem.GetComponent<Transform>()!.LocalScale = new Vector3(0, 1, 1);
            Call(editor, "ResetGameInput");
            RaiseGameKey(editor, Key.Tab, KeyModifiers.None, down: true);
            Check(Field<Guid?>(editor, "GameFocusedForTest") is null,
                "Tab must skip a Button with a non-invertible transform.");
            runtimeItem.GetComponent<Transform>()!.LocalScale = Vector3.One;
            runtimeItem.GetComponent<Transform>()!.LocalPosition = new Vector3(10000, 10000, 0);
            RaiseGameKey(editor, Key.Tab, KeyModifiers.None, down: true);
            Check(Field<Guid?>(editor, "GameFocusedForTest") is null, "Tab must skip fully clipped Buttons.");

            runtimeItem.GetComponent<Transform>()!.LocalPosition = new Vector3(10, 20, 0);
            editor.MouseDown(point, MouseButton.Left);
            Field<PlaySession>(editor, "_play").Runtime.Scene.Remove(runtimeItem);
            Call(editor, "StepPlayOnce", 0f);
            Check(Field<Guid?>(editor, "GamePressedForTest") is null
                && Field<Guid?>(editor, "GameFocusedForTest") is null,
                "Removing a pressed Button must release input state.");
            editor.MouseUp(point, MouseButton.Left);
            Call(editor, "StopPlay");
        }
        finally { CloseEditor(editor); }
    }

    private static void HandlerEndingsAndReplay()
    {
        GameClickCounter.Reset();
        var editor = CreateEditor();
        try
        {
            EnsureHandlers(editor);
            var scene = EditScene(editor);
            var owner = Field<ProjectComponents>(editor, "_components");
            var services = Field<GameSession>(editor, "EditSession");
            var item = AddCard(editor, scene, "Click me", new Vector3(10, 20, 0), new Vector2(100, 40), 0, true, true);
            owner.TryAttach(item, typeof(GameClickCounter), services.Factory);
            Dispatcher.UIThread.RunJobs();

            var total = 0;
            for (var i = 0; i < 20; i++)
            {
                StartPlayWithCallbacks(editor);
                StopTimer(editor);
                ShowGameTab(editor);
                Check(Field<Guid?>(editor, "GameFocusedForTest") is null && Field<Guid?>(editor, "GamePressedForTest") is null,
                    $"Re-Play {i} must start with fresh input state.");
                Check((bool)Call(editor, "TryGamePressForTest", new Vector2(50, 40))!, $"Play {i} press must start.");
                Check((bool)Call(editor, "TryGameReleaseForTest", new Vector2(50, 40))!, $"Play {i} release must click.");
                Call(editor, "StepPlayOnce", 1f / 60f);
                total += RuntimeCounter(editor, "Click me").Calls;
                Check((bool)Call(editor, "StopPlay")!, $"Play {i} must stop cleanly.");
            }
            Check(total == 20, $"20 Play/Stop cycles must notify exactly once each, got {total}.");

            var badScene = EditScene(editor);
            var bad = badScene.AddEmpty();
            bad.Rename("Bad");
            owner.TryAttach(bad, typeof(Transform), services.Factory);
            owner.TryAttach(bad, typeof(UiElement), services.Factory);
            bad.GetComponent<UiElement>()!.Pivot = Vector2.Zero;
            bad.GetComponent<UiElement>()!.SizeDelta = new Vector2(100, 40);
            bad.GetComponent<Transform>()!.LocalPosition = new Vector3(10, 20, 0);
            owner.TryAttach(bad, typeof(PureEngine.Core.Components.Button), services.Factory);
            owner.TryAttach(bad, typeof(GameThrower), services.Factory);
            Dispatcher.UIThread.RunJobs();
            StartPlayWithCallbacks(editor);
            StopTimer(editor);
            ShowGameTab(editor);
            Check((bool)Call(editor, "TryGamePressForTest", new Vector2(50, 40))!, "Error-case press must start.");
            Check((bool)Call(editor, "TryGameReleaseForTest", new Vector2(50, 40))!, "Error-case release must click.");
            Call(editor, "StepPlayOnce", 1f / 60f);
            Dispatcher.UIThread.RunJobs();
            Check(!(bool)Call(editor, "get_IsPlaying")!, "Handler exceptions must stop Play safely.");
            var status = Control<TextBlock>(editor, "FileStatus");
            var tip = Avalonia.Controls.ToolTip.GetTip(status)?.ToString() ?? "";
            Check(status.Text?.Contains("OnClick") == true || tip.Contains("OnClick"),
                "Handler exceptions must be reported through the existing error path.");
            Call(editor, "StopPlay");
            badScene.Remove(bad);
            Dispatcher.UIThread.RunJobs();

            var remover = scene.AddEmpty();
            remover.Rename("Remover");
            owner.TryAttach(remover, typeof(Transform), services.Factory);
            owner.TryAttach(remover, typeof(UiElement), services.Factory);
            remover.GetComponent<UiElement>()!.Pivot = Vector2.Zero;
            remover.GetComponent<UiElement>()!.SizeDelta = new Vector2(100, 40);
            remover.GetComponent<Transform>()!.LocalPosition = new Vector3(10, 100, 0);
            owner.TryAttach(remover, typeof(PureEngine.Core.Components.Button), services.Factory);
            owner.TryAttach(remover, typeof(GameRemover), services.Factory);
            Dispatcher.UIThread.RunJobs();
            StartPlayWithCallbacks(editor);
            StopTimer(editor);
            ShowGameTab(editor);
            Check((bool)Call(editor, "TryGamePressForTest", new Vector2(50, 120))!, "Remover press must start.");
            Check((bool)Call(editor, "TryGameReleaseForTest", new Vector2(50, 120))!, "Remover release must click.");
            Call(editor, "StepPlayOnce", 1f / 60f);
            Check((bool)Call(editor, "get_IsPlaying")!, "Deletion during clicks must not stop the session.");
            Call(editor, "StopPlay");
            CloseEditor(editor);
        }
        finally
        {
            if (editor.IsVisible) CloseEditor(editor);
        }
    }

    public sealed class GameClickCounter
    {
        public static void Reset() { }
        public int Calls;
        public Guid LastId;
        public void OnClick(UiClickContext context)
        {
            Calls++;
            LastId = context.ButtonObject.Id;
            Log.Info($"Clicked {context.ButtonObject.Name} x{Calls}");
        }
    }

    public sealed class GameThrower
    {
        public static void OnClick(UiClickContext _) => throw new ApplicationException("click boom");
    }

    public sealed class GameRemover
    {
        public static void OnClick(UiClickContext context) => context.Scene.Remove(context.ButtonObject);
    }

}
