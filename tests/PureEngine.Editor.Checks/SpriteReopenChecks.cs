using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PureEngine.Core;
using PureEngine.Editor;
using SkiaSharp;

/// <summary>Guards the project close/reopen round trip for Image sprites at the editor UI level.</summary>
internal static class SpriteReopenChecks
{
    public static void Run(string root)
    {
        var parent = Path.Combine(root, "SpriteReopen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        using var components = new ProjectComponents();
        var serializer = new SceneSerializer(components.Registry);
        var project = ProjectFile.Create(parent, "SpriteReopen", serializer.Serialize(new Scene()));

        var source = Path.Combine(parent, "card.png");
        using (var bitmap = new SKBitmap(16, 16))
        {
            bitmap.Erase(SKColors.CornflowerBlue);
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(source, data.ToArray());
        }
        var imported = ProjectAssets.ImportImage(project.RootDirectory, source);

        var scene = new Scene();
        var card = scene.AddEmpty();
        card.Rename("Card");
        card.Attach(new Transform());
        card.Attach(new UiElement());
        card.Attach(new PureEngine.Core.Image { Sprite = new Sprite(imported.Id) });
        SceneFile.Write(project.StartupScenePath, serializer.Serialize(scene));

        CheckReopened(project, imported, null);
        card.GetComponent<PureEngine.Core.Image>()!.Sprite = new Sprite(imported.Id, (2, 3, 8, 6));
        SceneFile.Write(project.StartupScenePath, serializer.Serialize(scene));
        CheckReopened(project, imported, (2, 3, 8, 6));
        Console.WriteLine("PASS: reopened whole/cropped Sprites resolve immediately; asset loss/recovery updates all Inspector text without changing saved values.");
    }

    private static void CheckReopened(ProjectFile project, ProjectAssets.AssetEntry imported,
        (int X, int Y, int Width, int Height)? crop)
    {
        using var session = ProjectSession.Open(project.ManifestPath);
        session.Components.Registry.Register<SpriteNameProbe>("checks.sprite-name");
        session.Scene.Objects.Single().Attach(new SpriteNameProbe());
        var editor = new MainWindow(session);
        var store = (EditSceneStore)typeof(MainWindow).GetProperty("EditSceneStore", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(editor)!;
        var heldImage = imported.FullPath + ".held";
        try
        {
            editor.FindControl<Grid>("SceneViewport")!.Children.Clear();
            editor.FindControl<Grid>("GameViewport")!.Children.Clear();
            editor.Show();
            Dispatcher.UIThread.RunJobs();
            // No reselection: the Inspector built during project open must already resolve the saved sprite.
            var combo = editor.GetVisualDescendants().OfType<ComboBox>()
                .Single(box => Equals(box.GetValue(AutomationProperties.NameProperty) as string, "Image.Sprite"));
            var info = editor.GetVisualDescendants().OfType<TextBlock>()
                .Single(block => Equals(block.GetValue(AutomationProperties.NameProperty) as string, "Image.Sprite.Info"));
            var warning = editor.GetVisualDescendants().OfType<TextBlock>()
                .Single(block => Equals(block.GetValue(AutomationProperties.NameProperty) as string, "Image.Requirements"));
            var image = store.Current.Objects.Single().GetComponent<PureEngine.Core.Image>()!;
            var sprite = image.Sprite;
            var yaml = File.ReadAllText(project.StartupScenePath);

            void CheckState(bool missing)
            {
                var expected = missing ? $"Missing: {imported.Id.ToString("D")[..8]}" : imported.RelativePath;
                Check(combo.SelectedItem?.ToString() == expected,
                    $"Sprite selection must be '{expected}', got '{combo.SelectedItem}'.");
                Check(warning.IsVisible == missing && (!missing || warning.Text!.Contains("Missing image")),
                    $"Sprite warning must reflect asset availability, got '{warning.Text}'.");
                var tip = ToolTip.GetTip(combo) as string;
                if (missing)
                {
                    Check(info.IsVisible && info.Text!.Contains("Missing image") && info.Text.Contains(imported.Id.ToString("D")),
                        $"Missing sprites must keep a visible info line with the full ID, got '{info.Text}'.");
                    Check(tip is not null && tip.Contains("Missing image") && tip.Contains(imported.Id.ToString("D")),
                        $"Missing sprite tooltip must carry the full ID, got '{tip}'.");
                }
                else if (crop is not null)
                {
                    Check(info.IsVisible && info.Text!.Contains("2,3,8x6") && info.Text.Contains(imported.RelativePath),
                        $"Cropped sprites must keep a visible info line, got '{info.Text}'.");
                    Check(tip is not null && tip.Contains("2,3,8x6") && tip.Contains(imported.RelativePath),
                        $"Cropped sprite tooltip must carry the path and crop, got '{tip}'.");
                }
                else
                {
                    Check(!info.IsVisible, "Assigned sprites must stay single-line; the path lives in the tooltip.");
                    Check(tip is not null && tip.Contains(imported.RelativePath),
                        $"Sprite tooltip must show the path, got '{tip}'.");
                }
                Check(ReferenceEquals(image.Sprite, sprite) && image.Sprite?.ImageId == imported.Id
                    && image.Sprite.SourceRect == crop && !store.IsDirty,
                    "Opening and rescanning assets must preserve the Sprite, crop, and clean scene state.");
                Check(File.ReadAllText(project.StartupScenePath) == yaml, "Asset refresh must not rewrite the saved scene.");
            }

            void RefreshAssets()
            {
                typeof(MainWindow).GetMethod("RefreshProjectAssets", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(editor, null);
                Dispatcher.UIThread.RunJobs();
            }

            CheckState(missing: false);
            File.Move(imported.FullPath, heldImage);
            RefreshAssets();
            CheckState(missing: true);
            File.Move(heldImage, imported.FullPath);
            RefreshAssets();
            CheckState(missing: false);
        }
        finally
        {
            if (File.Exists(heldImage)) File.Move(heldImage, imported.FullPath);
            // A failed dirty-state assertion must not leave an unsaved dialog and watcher alive.
            store.MarkClean();
            editor.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public enum SpriteMode { First, Second }

    public sealed class SpriteNameProbe
    {
        [Inspector]
        public SpriteMode Sprite { get; set; }
    }
}
