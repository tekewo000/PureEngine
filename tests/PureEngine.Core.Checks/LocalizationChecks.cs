using PureEngine.Core;
using PureEngine.Runtime;
using Microsoft.Extensions.DependencyInjection;

internal static class LocalizationChecks
{
    public static void Run()
    {
        DescriptorAndRoundTrip();
        ServiceResolution();
        SceneAndPlayIsolation();
        Console.WriteLine("PASS: localized text assets, language fallback, scene save/clone, and Play isolation.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or InvalidOperationException)
        { return; }
        throw new InvalidOperationException(message);
    }

    private static ComponentRegistry LocalizationRegistry()
    {
        var registry = new ComponentRegistry();
        registry.Register<Transform>("core.transform");
        registry.Register<UiElement>("core.ui-element");
        registry.Register<Text>("core.text");
        registry.Register<LocalizedText>("core.localized-text");
        return registry;
    }

    private static void DescriptorAndRoundTrip()
    {
        var registry = LocalizationRegistry();
        Check(DataAssetDescriptor.TryCreate(typeof(LocalizedText), registry, out var descriptor, out _)
            && descriptor is { MenuPath: "Localization/Text", DisplayName: "Text", TypeId: "core.localized-text" },
            "LocalizedText must appear under the Localization/Text creation menu.");
        var serializer = new DataAssetSerializer(registry);
        var entry = new LocalizedText
        {
            Key = "menu.start",
            Texts = new Dictionary<string, string>
            {
                ["ja"] = "スタート",
                ["en"] = "Start",
            },
            Voices = new Dictionary<string, string> { ["ja"] = "voice/start_ja" },
        };
        var id = Guid.NewGuid();
        var yaml = serializer.Serialize(entry, id);
        var (restored, restoredId) = serializer.Deserialize(yaml, out var membersChanged);
        var copy = (LocalizedText)restored;
        Check(restoredId == id && !membersChanged && copy.Key == "menu.start"
            && copy.Texts["ja"] == "スタート" && copy.Texts["en"] == "Start"
            && copy.Voices["ja"] == "voice/start_ja",
            "LocalizedText must survive save/load with unicode text and voice slots.");
        Check(serializer.Serialize(copy, id) == yaml, "LocalizedText save/load changed output.");
    }

    private static void ServiceResolution()
    {
        var entry = new LocalizedText
        {
            Key = "menu.start",
            Texts = new Dictionary<string, string> { ["ja"] = "スタート", ["en"] = "Start" },
            Voices = new Dictionary<string, string> { ["ja"] = "voice/start_ja" },
        };
        var service = new LocalizationService();
        Check(service.CurrentLanguage == LocalizationService.DefaultLanguage,
            "Preview and run language must start at the default.");
        Check(LocalizationService.ResolveText(entry, "Fallback", "ja") == "スタート"
            && LocalizationService.ResolveText(entry, "Fallback", "en") == "Start",
            "Resolution must follow the requested language.");
        Check(service.ResolveText(entry, "Fallback") == "スタート", "Default resolution must use the default language.");
        service.CurrentLanguage = "en";
        Check(service.ResolveText(entry, "Fallback") == "Start", "Instance resolution must follow CurrentLanguage.");
        service.CurrentLanguage = "ko";
        Check(service.ResolveText(entry, "Fallback") == "スタート", "Missing languages must fall back to the default.");
        Check(service.ResolveVoice(entry) == "voice/start_ja", "Missing voice languages must fall back to the default.");
        service.CurrentLanguage = "EN";
        Check(service.CurrentLanguage == "en" && service.ResolveText(entry, "Fallback") == "Start",
            "Language codes must normalize to lowercase.");
        Reject(() => service.CurrentLanguage = "", "Empty language codes must be rejected.");
        Reject(() => service.CurrentLanguage = "e", "Single-letter language codes must be rejected.");
        Reject(() => service.CurrentLanguage = "has space", "Language codes with spaces must be rejected.");
        Check(LocalizationService.IsValidLanguageCode("ja") && LocalizationService.IsValidLanguageCode("ko")
            && LocalizationService.IsValidLanguageCode("ja-JP") && !LocalizationService.IsValidLanguageCode(null)
            && !LocalizationService.IsValidLanguageCode(""), "Language code validation must accept BCP47-style tags.");
        var upper = new LocalizedText
        {
            Key = "menu.upper",
            Texts = new Dictionary<string, string> { ["JA"] = " Upper " },
        };
        Check(LocalizationService.ResolveText(upper, "Fallback", "ja") == " Upper ",
            "Resolution must tolerate hand-typed uppercase language keys.");
        var partial = new LocalizedText
        {
            Key = "menu.partial",
            Texts = new Dictionary<string, string> { ["en"] = "", ["ko"] = "시작" },
        };
        Check(LocalizationService.ResolveText(partial, "Fallback", "en") == "시작",
            "Empty values must count as untranslated and fall through to the first available language.");
        Check(LocalizationService.ResolveText(null, "Fallback", "en") == "Fallback"
            && LocalizationService.ResolveText(null, null, "en") == "",
            "Missing entries must resolve to the caller fallback.");
        Check(LocalizationService.ResolveVoice(partial, "en") is null, "Missing voice slots must resolve to silence.");
    }

    private static void SceneAndPlayIsolation()
    {
        var root = Path.Combine(Path.GetTempPath(), "PureEngine-Localization-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var registry = LocalizationRegistry();
            var entryId = Guid.NewGuid();
            var path = Path.Combine(root, "Start.pure.asset.yaml");
            File.WriteAllText(path, new DataAssetSerializer(registry).Serialize(new LocalizedText
            {
                Key = "menu.start",
                Texts = new Dictionary<string, string> { ["ja"] = "スタート", ["en"] = "Start" },
            }, entryId));
            var assets = DataAssetStore.ScanFolder(root, registry, out _);
            var entry = assets.Get<LocalizedText>(entryId);
            var serializer = new SceneSerializer(registry, assets);
            var scene = serializer.Deserialize("version: 3\nobjects: []");
            var item = scene.AddEmpty();
            item.Rename("Label");
            item.Attach(new Transform());
            item.Attach(new UiElement());
            item.Attach(new Text { Content = "Direct", LocalizedEntry = entry });
            var yaml = serializer.Serialize(scene);
            Check(yaml.Contains(entryId.ToString("D")) && yaml.Contains("LocalizedEntry"),
                "Scene must store the localization entry ID, not its values.");
            var restored = serializer.Deserialize(yaml).Objects.Single().GetComponent<Text>()!;
            Check(ReferenceEquals(restored.LocalizedEntry, entry) && restored.Content == "Direct",
                "Scene load must resolve the entry instance and keep direct content as fallback.");
            var clone = new SceneSerializer(registry).Clone(scene).Objects.Single().GetComponent<Text>()!;
            Check(clone.LocalizedEntry is not null && !ReferenceEquals(clone.LocalizedEntry, entry)
                && clone.LocalizedEntry.Texts["en"] == "Start",
                "Clone must isolate the entry snapshot while keeping its values.");
            for (var run = 0; run < 2; run++)
            {
                using var play = PlaySession.Prepare(scene, registry, services =>
                    services.AddSingleton(DataAssetStore.ScanFolder(root, registry, out _)));
                var live = play.Runtime.Scene.Objects.Single().GetComponent<Text>()!;
                Check(LocalizationService.ResolveText(live.LocalizedEntry, live.Content, "en") == "Start",
                    "Play must resolve saved entries for the run language.");
                live.LocalizedEntry!.Texts["en"] = "Changed";
            }
            Check(entry.Texts["en"] == "Start", "Play must not mutate editing entries.");
            File.Delete(path);
            var missing = new SceneSerializer(registry, DataAssetStore.ScanFolder(root, registry, out _)).Deserialize(yaml);
            Check(missing.Objects.Single().GetComponent<Text>()!.LocalizedEntry is null, "Missing entries must resolve to null.");
            var resaved = new SceneSerializer(registry, DataAssetStore.ScanFolder(root, registry, out _)).Serialize(missing);
            Check(resaved.Contains(entryId.ToString("D")), "Missing entry IDs must survive saving.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
