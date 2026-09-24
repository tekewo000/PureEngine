using System.Numerics;
using PureEngine.Core;
using PureEngine.Rendering;
using SkiaSharp;

internal static class UiTextEditorChecks
{
    public static void Run()
    {
        TextDrawsWithColorAndPlacement();
        EmptyContentDrawsNothing();
        InvalidFontSizeReportsDiagnostic();
        GameDrawsText();
        SameObjectDrawsImageBeforeText();
        RequirementsWarningTracksMissingComponents();
        FailedImageKeepsTextAndFocusWithoutTint();
        WidthWrapsAndHeightDoesNotClip();
        Console.WriteLine("PASS: Text edit/Game drawing, color/placement, empty/invalid handling, and Image-before-Text order.");
    }

    private static void RequirementsWarningTracksMissingComponents()
    {
        var editor = new PureEngine.Editor.MainWindow();
        try
        {
            var item = new SceneObject("Label");
            var text = new Text();
            item.Attach(text);
            var update = typeof(PureEngine.Editor.MainWindow).GetMethod("UpdateUiWarning",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var warning = new Avalonia.Controls.TextBlock();
            update.Invoke(editor, [warning, item, text]);
            Check(warning.IsVisible && warning.Text == "Requires: Transform, UiElement",
                "The Text Inspector must display both missing requirements.");
            item.Attach(new Transform());
            update.Invoke(editor, [warning, item, text]);
            Check(warning.IsVisible && warning.Text == "Requires: UiElement",
                "The Text Inspector must update its missing requirements.");
            item.Attach(new UiElement());
            update.Invoke(editor, [warning, item, text]);
            Check(!warning.IsVisible, "The Text Inspector must clear satisfied requirements.");
        }
        finally
        {
            editor.Close();
        }
    }

    private static void FailedImageKeepsTextAndFocusWithoutTint()
    {
        var scene = new Scene();
        var item = AddText(scene, "Broken image", Vector3.Zero, new Vector2(100, 60), "Hi");
        item.Attach(new PureEngine.Core.Image { Sprite = new Sprite(Guid.NewGuid()) });
        item.Attach(new Button());
        using var draw = new DrawList();
        var viewport = new Vector2(400, 200);
        var images = new Dictionary<Guid, byte[]>();
        var diagnostics = EditSceneRenderer.Build(draw, scene, images, viewport);
        var textVertexCount = draw.Vertices.Length;
        Check(diagnostics.Count == 1 && diagnostics[0].ObjectId == item.Id && textVertexCount > 0,
            "An Image failure must not suppress Text in Scene View.");
        var states = new Dictionary<Guid, GameSceneRenderer.ButtonVisual>
        {
            [item.Id] = new(UiButtonVisualState.Hover, true),
        };
        var gameDiagnostics = GameSceneRenderer.Build(draw, scene, images, viewport, states);
        Check(gameDiagnostics.Count == 1 && gameDiagnostics[0].ObjectId == item.Id
            && draw.Vertices.Length == textVertexCount + 24,
            "An Image failure must retain Text and the four focus edges without a hover tint.");
        item.GetComponent<Text>()!.Content = "";
        gameDiagnostics = GameSceneRenderer.Build(draw, scene, images, viewport, states);
        Check(gameDiagnostics.Count == 1 && draw.Vertices.Length == 24,
            "Focus rendering after an Image failure must not depend on visible Text.");
    }

    private static void WidthWrapsAndHeightDoesNotClip()
    {
        var scene = new Scene();
        var item = AddText(scene, "Wrap", Vector3.Zero, new Vector2(200, 1), "ABCD");
        using var draw = new DrawList();
        var viewport = new Vector2(400, 200);
        var images = new Dictionary<Guid, byte[]>();
        Check(EditSceneRenderer.Build(draw, scene, images, viewport).Count == 0
            && draw.Vertices.Length == 6, "Wide Text must fit on one line even with a short height.");
        item.GetComponent<UiElement>()!.SizeDelta = new Vector2(1, 1);
        Check(EditSceneRenderer.Build(draw, scene, images, viewport).Count == 0
            && draw.Vertices.Length == 24, "Narrow Text must wrap each text element without height clipping.");
        var before = draw.Vertices[6].Position.Y;
        item.GetComponent<Text>()!.LineSpacing = 2.2f;
        Check(EditSceneRenderer.Build(draw, scene, images, viewport).Count == 0
            && Math.Abs(draw.Vertices[6].Position.Y - before - 20) < 0.001f,
            "LineSpacing edits must move the second line by the font-size-scaled difference.");
        foreach (var vertex in draw.Vertices)
            Check(vertex.Clip == new Vector4(0, 0, 400, 200), "Text must use the viewport clip.");
    }

    private static SceneObject AddText(Scene scene, string name, Vector3 position, Vector2 size, string? content)
    {
        var item = scene.AddEmpty();
        item.Rename(name);
        item.Attach(new Transform { LocalPosition = position });
        item.Attach(new UiElement { Pivot = Vector2.Zero, SizeDelta = size });
        item.Attach(new Text { Content = content!, Color = new Color(1, 0.5f, 0.25f, 0.75f), FontSize = 20f });
        return item;
    }

    private static void TextDrawsWithColorAndPlacement()
    {
        var scene = new Scene();
        var item = AddText(scene, "Label", new Vector3(10, 20, 0), new Vector2(200, 60), "Hi");
        using var draw = new DrawList();
        var viewport = new Vector2(400, 200);
        var diagnostics = EditSceneRenderer.Build(draw, scene, new Dictionary<Guid, byte[]>(), viewport);
        var expected = new Vector4(1, 0.5f, 0.25f, 0.75f);
        Check(diagnostics.Count == 0 && draw.Vertices.Length > 0, "Text must draw without a diagnostic.");
        foreach (var vertex in draw.Vertices)
            Check(vertex.Color == expected, "Text quads must carry the Text color.");
        var before = draw.Vertices.ToArray();
        item.GetComponent<Transform>()!.LocalPosition = new Vector3(30, 40, 0);
        Check(EditSceneRenderer.Build(draw, scene, new Dictionary<Guid, byte[]>(), viewport).Count == 0,
            "Moving Text must not report a diagnostic.");
        var after = draw.Vertices.ToArray();
        Check(before.Length == after.Length && before.Length > 0, "Moving Text must keep its quads.");
        for (var i = 0; i < before.Length; i++)
            Check(after[i].Position - before[i].Position == new Vector2(20, 20), "Position edits must reach the text preview.");
    }

    private static void EmptyContentDrawsNothing()
    {
        var scene = new Scene();
        var item = AddText(scene, "Empty", new Vector3(10, 20, 0), new Vector2(200, 60), "");
        using var draw = new DrawList();
        var viewport = new Vector2(400, 200);
        Check(EditSceneRenderer.Build(draw, scene, new Dictionary<Guid, byte[]>(), viewport).Count == 0
            && draw.Vertices.IsEmpty, "Empty content must draw nothing without a diagnostic.");
        item.GetComponent<Text>()!.Content = null!;
        Check(EditSceneRenderer.Build(draw, scene, new Dictionary<Guid, byte[]>(), viewport).Count == 0
            && draw.Vertices.IsEmpty, "Null content must draw nothing without a diagnostic.");
    }

    private static void InvalidFontSizeReportsDiagnostic()
    {
        var scene = new Scene();
        var item = AddText(scene, "Broken", new Vector3(10, 20, 0), new Vector2(200, 60), "Hi");
        item.GetComponent<Text>()!.FontSize = 0;
        using var draw = new DrawList();
        var viewport = new Vector2(400, 200);
        var diagnostics = EditSceneRenderer.Build(draw, scene, new Dictionary<Guid, byte[]>(), viewport);
        Check(diagnostics.Count == 1 && diagnostics[0].ObjectId == item.Id && draw.Vertices.IsEmpty,
            "Invalid font size must be skipped with a diagnostic and keep its ID.");
    }

    private static void GameDrawsText()
    {
        var scene = new Scene();
        AddText(scene, "Label", new Vector3(10, 20, 0), new Vector2(200, 60), "Play");
        using var draw = new DrawList();
        var diagnostics = GameSceneRenderer.Build(draw, scene, new Dictionary<Guid, byte[]>(), new Vector2(400, 200));
        Check(diagnostics.Count == 0 && draw.Vertices.Length > 0, "Game must draw runtime Text.");
    }

    private static void SameObjectDrawsImageBeforeText()
    {
        var imageId = Guid.NewGuid();
        var images = new Dictionary<Guid, byte[]> { [imageId] = CreatePng() };
        var scene = new Scene();
        var item = scene.AddEmpty();
        item.Rename("Badge");
        item.Attach(new Transform { LocalPosition = new Vector3(10, 20, 0) });
        item.Attach(new UiElement { Pivot = Vector2.Zero, SizeDelta = new Vector2(100, 40) });
        item.Attach(new PureEngine.Core.Image { Sprite = new Sprite(imageId), Color = new Color(1, 0, 0, 1) });
        item.Attach(new Text { Content = "Hi", Color = new Color(0, 0, 1, 1), FontSize = 20f });
        using var draw = new DrawList();
        var diagnostics = EditSceneRenderer.Build(draw, scene, images, new Vector2(400, 200));
        var vertices = draw.Vertices.ToArray();
        Check(diagnostics.Count == 0 && vertices.Length > 6, "Image and Text on one object must both draw.");
        Check(vertices.Take(6).All(vertex => vertex.Color == new Vector4(1, 0, 0, 1)),
            "Image must draw before Text on the same object.");
        Check(vertices.Skip(6).All(vertex => vertex.Color == new Vector4(0, 0, 1, 1)),
            "Text must draw after Image on the same object.");
    }

    private static byte[] CreatePng()
    {
        using var bitmap = new SKBitmap(16, 16);
        bitmap.Erase(SKColors.White);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}