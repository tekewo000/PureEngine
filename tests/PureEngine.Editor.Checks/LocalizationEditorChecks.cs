using System.Numerics;
using Avalonia.Controls;
using Avalonia.Threading;
using PureEngine.Core;
using PureEngine.Editor;
using PureEngine.Rendering;

internal static class LocalizationEditorChecks
{
    public static void Run()
    {
        PreviewLanguageDefaults();
        PreviewLanguageRefresh();
        LocalizedRenderResolution();
        Console.WriteLine("PASS: preview language selection, asset-driven language list, and localized rendering.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void PreviewLanguageDefaults()
    {
        var editor = new MainWindow();
        editor.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var box = editor.FindControl<ComboBox>("PreviewLanguageBox");
            Check(box is not null, "The toolbar must offer a preview language selector.");
            Check(editor.ViewModel.Localization.PreviewLanguage == "ja"
                && editor.ViewModel.Localization.AvailableLanguages.SequenceEqual(["ja"]),
                "Preview language must start at the default with no project.");
            Check(!box!.IsEnabled, "Preview language must be disabled with no project.");
            Check(editor.ViewModel.Play.PlayLocalization is null, "No Play run must own a localization service.");
        }
        finally
        {
            editor.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void PreviewLanguageRefresh()
    {
        var root = Path.Combine(Path.GetTempPath(), "PureEngine-LocalizationEditor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var editor = new MainWindow();
        editor.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            using var components = new ProjectComponents();
            var registry = components.Registry;
            Check(registry.Ids.Contains("core.localized-text"), "LocalizedText must be registered for every project.");
            File.WriteAllText(Path.Combine(root, "Start.pure.asset.yaml"),
                new DataAssetSerializer(registry).Serialize(new LocalizedText
                {
                    Key = "menu.start",
                    Texts = new Dictionary<string, string> { ["ja"] = "スタート", ["en"] = "Start", ["ko"] = "시작" },
                }, Guid.NewGuid()));
            var store = DataAssetStore.ScanFolder(root, registry, out _);
            editor.ViewModel.Localization.Refresh(store);
            Dispatcher.UIThread.RunJobs();
            Check(editor.ViewModel.Localization.AvailableLanguages.SequenceEqual(["en", "ja", "ko"]),
                "Available preview languages must union entry languages with the default, sorted.");
            Check(editor.ViewModel.Localization.PreviewLanguage == "ja", "Refresh must keep the selection when still available.");
            var box = editor.FindControl<ComboBox>("PreviewLanguageBox")!;
            box.SelectedItem = "ko";
            Dispatcher.UIThread.RunJobs();
            Check(editor.ViewModel.Localization.PreviewLanguage == "ko"
                && editor.ViewModel.Localization.PreviewService.CurrentLanguage == "ko",
                "Toolbar selection must drive the preview service.");
            editor.ViewModel.Localization.Refresh(new DataAssetStore());
            Dispatcher.UIThread.RunJobs();
            Check(editor.ViewModel.Localization.AvailableLanguages.SequenceEqual(["ja"])
                && editor.ViewModel.Localization.PreviewLanguage == "ja",
                "Empty stores must reset the list and fall back to the default.");
            editor.ViewModel.Localization.PreviewLanguage = "not a language";
            Check(editor.ViewModel.Localization.PreviewLanguage == "ja", "Invalid preview languages must be ignored.");
        }
        finally
        {
            editor.Close();
            Dispatcher.UIThread.RunJobs();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void LocalizedRenderResolution()
    {
        var entry = new LocalizedText
        {
            Key = "menu.start",
            Texts = new Dictionary<string, string> { ["ja"] = "スタート", ["en"] = "Start" },
        };
        var scene = new Scene();
        var item = scene.AddEmpty();
        item.Rename("Label");
        item.Attach(new Transform());
        item.Attach(new UiElement { Pivot = Vector2.Zero, SizeDelta = new Vector2(200, 60) });
        var text = new Text { Content = "Direct", LocalizedEntry = entry };
        item.Attach(text);
        var viewport = new Vector2(400, 200);
        using var draw = new DrawList();
        var images = new Dictionary<Guid, byte[]>();
        var english = new LocalizationService { CurrentLanguage = "en" };
        Check(EditSceneRenderer.Build(draw, scene, images, viewport, Matrix4x4.Identity, english).Count == 0
            && draw.Vertices.Length > 0, "Localized Text must draw for the preview language.");
        var englishVertices = draw.Vertices.Length;
        text.LocalizedEntry = null;
        text.Content = "Start";
        Check(EditSceneRenderer.Build(draw, scene, images, viewport, Matrix4x4.Identity, english).Count == 0
            && draw.Vertices.Length == englishVertices,
            "Preview resolution must submit the entry text for the preview language.");
        text.LocalizedEntry = entry;
        var japanese = new LocalizationService();
        Check(EditSceneRenderer.Build(draw, scene, images, viewport, Matrix4x4.Identity, japanese).Count == 0
            && draw.Vertices.Length > 0, "Localized Text must draw for the default language.");
        var japaneseVertices = draw.Vertices.Length;
        text.LocalizedEntry = null;
        text.Content = "スタート";
        Check(EditSceneRenderer.Build(draw, scene, images, viewport, Matrix4x4.Identity, japanese).Count == 0
            && draw.Vertices.Length == japaneseVertices,
            "Default resolution must submit the default language text.");
        text.LocalizedEntry = entry;
        var korean = new LocalizationService { CurrentLanguage = "ko" };
        Check(EditSceneRenderer.Build(draw, scene, images, viewport, Matrix4x4.Identity, korean).Count == 0
            && draw.Vertices.Length == japaneseVertices,
            "Missing preview languages must fall back to the default text.");
        var gameDiagnostics = GameSceneRenderer.Build(draw, scene, images, viewport, null, english);
        Check(gameDiagnostics.Count == 0 && draw.Vertices.Length == englishVertices,
            "Game rendering must resolve the run language.");
        text.LocalizedEntry = new LocalizedText { Key = "menu.empty" };
        text.Content = "";
        Check(EditSceneRenderer.Build(draw, scene, images, viewport, Matrix4x4.Identity, english).Count == 0
            && draw.Vertices.Length == 0,
            "Empty entries with empty fallback must draw nothing.");
    }
}
