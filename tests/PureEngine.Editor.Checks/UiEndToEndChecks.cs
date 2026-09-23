using System.Numerics;
using PureEngine.Core;
using PureEngine.Editor;
using PureEngine.Rendering;
using SkiaSharp;

internal static class UiEndToEndChecks
{
    public static void Run(string root)
    {
        var parent = Path.Combine(root, "UiE2E-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        using var components = new ProjectComponents();
        var serializer = new SceneSerializer(components.Registry);
        var project = ProjectFile.Create(parent, "UiE2E", serializer.Serialize(new Scene()));

        var source = Path.Combine(parent, "card.png");
        using (var bitmap = new SKBitmap(32, 32))
        {
            bitmap.Erase(SKColors.CornflowerBlue);
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(source, data.ToArray());
        }
        var imported = ProjectAssets.ImportImage(project.RootDirectory, source);
        var assets = ProjectAssets.Scan(project.RootDirectory);
        Check(assets.Images.ContainsKey(imported.Id), "End-to-end asset import must resolve.");
        var images = assets.LoadImageBytes();

        var scene = new Scene();
        var card = scene.AddEmpty();
        card.Rename("Card");
        using (var editServices = PureEngine.Runtime.GameSession.Create(GameServices.ForProject(components)))
        {
            Check(components.TryAttach(card, typeof(Transform), editServices.Factory), "E2E Transform add failed.");
            Check(components.TryAttach(card, typeof(UiElement), editServices.Factory), "E2E UiElement add failed.");
            Check(components.TryAttach(card, typeof(global::Image), editServices.Factory), "E2E Image add failed.");
        }
        card.GetComponent<Transform>()!.LocalPosition = new Vector3(20, 30, 0);
        card.GetComponent<UiElement>()!.SizeDelta = new Vector2(120, 60);
        card.GetComponent<UiElement>()!.Pivot = Vector2.Zero;
        card.GetComponent<global::Image>()!.Sprite = new Sprite(imported.Id);
        card.GetComponent<global::Image>()!.Color = new Vector4(1, 0.5f, 0.25f, 1);
        card.GetComponent<global::Image>()!.Order = 3;
        var child = scene.AddEmpty();
        child.Rename("Badge");
        child.SetParent(card);
        child.Attach(new Transform { LocalPosition = new Vector3(10, 10, 0) });
        child.Attach(new UiElement { Pivot = Vector2.Zero, SizeDelta = new Vector2(24, 24) });
        child.Attach(new global::Image { Sprite = new Sprite(imported.Id), Order = -1 });
        Check(UiComponentRequirements.GetMissing(card).Count == 0
            && UiComponentRequirements.GetMissing(child).Count == 0, "E2E requirements must clear.");

        using var beforeDraw = new DrawList();
        var viewport = new Vector2(400, 200);
        var beforeDiagnostics = EditSceneRenderer.Build(beforeDraw, scene, images, viewport);
        Check(beforeDiagnostics.Count == 0 && beforeDraw.Vertices.Length == 12, "E2E preview must draw parent and child.");
        var beforeVertices = beforeDraw.Vertices.ToArray();

        var scenePath = project.StartupScenePath;
        SceneFile.Write(scenePath, serializer.Serialize(scene));
        using (var session = ProjectSession.Open(project.ManifestPath))
        {
            Check(session.Scene.Objects.Count == 2, "Reopen must restore parent and child.");
            var reopened = session.Scene.Objects.First(item => item.Name == "Card");
            var reopenedChild = session.Scene.Objects.First(item => item.Name == "Badge");
            Check(ReferenceEquals(reopenedChild.Parent, reopened), "Reopen must restore parent links.");
            Check(reopened.GetComponent<global::Image>()!.Sprite!.ImageId == imported.Id
                && reopened.GetComponent<Transform>()!.LocalPosition == new Vector3(20, 30, 0)
                && reopened.GetComponent<UiElement>()!.SizeDelta == new Vector2(120, 60)
                && reopened.GetComponent<global::Image>()!.Color == new Vector4(1, 0.5f, 0.25f, 1)
                && reopened.GetComponent<global::Image>()!.Order == 3
                && reopenedChild.GetComponent<global::Image>()!.Order == -1,
                "Reopen must restore Sprite reference, placement, color and Order.");
            var reopenedAssets = ProjectAssets.Scan(project.RootDirectory);
            var reopenedImages = reopenedAssets.LoadImageBytes();
            using var afterDraw = new DrawList();
            var afterDiagnostics = EditSceneRenderer.Build(afterDraw, session.Scene, reopenedImages, viewport);
            Check(afterDiagnostics.Count == 0 && afterDraw.Vertices.Length == beforeVertices.Length,
                "Reopen preview must draw the same quads.");
            for (var i = 0; i < beforeVertices.Length; i++)
                Check(afterDraw.Vertices[i].Position == beforeVertices[i].Position
                    && afterDraw.Vertices[i].Color == beforeVertices[i].Color,
                    $"Reopen display differs at vertex {i}.");
            var clone = serializer.Clone(session.Scene);
            var cloneCard = clone.Objects.First(item => item.Name == "Card");
            Check(!ReferenceEquals(cloneCard, reopened)
                && !ReferenceEquals(cloneCard.GetComponent<global::Image>(), reopened.GetComponent<global::Image>())
                && ReferenceEquals(clone.Objects.First(item => item.Name == "Badge").Parent, cloneCard),
                "Clone must separate components and resolve parents to the clone.");
            Check(cloneCard.GetComponent<global::Image>()!.Order == 3
                && clone.Objects.First(item => item.Name == "Badge").GetComponent<global::Image>()!.Order == -1,
                "Clone must preserve render Order.");
        }
        Console.WriteLine("PASS: empty/add/search/sprite/placement/color/Order/save/reopen/clone end-to-end with identical preview.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
