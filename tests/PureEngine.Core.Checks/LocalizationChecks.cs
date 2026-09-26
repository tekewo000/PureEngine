using PureEngine.Core;
using PureEngine.Runtime;
using Microsoft.Extensions.DependencyInjection;

internal static class LocalizationChecks
{
    public static void Run()
    {
        TableRoundTrip();
        ServiceResolution();
        SceneAndPlayIsolation();
        Console.WriteLine("PASS: localization table save/load, language fallback, scene save/clone, and Play isolation.");
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
        return registry;
    }

    private static LocalizationTable SampleTable(out Guid startId)
    {
        startId = Guid.NewGuid();
        return new LocalizationTable
        {
            Languages = ["ja", "en"],
            Entries =
            [
                new LocalizationEntry
                {
                    Id = startId,
                    Key = "menu.start",
                    Texts = new Dictionary<string, string> { ["ja"] = "スタート", ["en"] = "Start" },
                    Voices = new Dictionary<string, string> { ["ja"] = "voice/start_ja" },
                },
            ],
        };
    }

    private static void TableRoundTrip()
    {
        var serializer = new LocalizationSerializer();
        var table = SampleTable(out var startId);
        var yaml = serializer.Serialize(table);
        var (restored, diagnostics) = serializer.Deserialize(yaml);
        Check(diagnostics.Count == 0 && restored.Languages.SequenceEqual(["ja", "en"])
            && restored.Entries.Count == 1 && restored.Entries[0].Id == startId
            && restored.Entries[0].Key == "menu.start"
            && restored.Entries[0].Texts["ja"] == "スタート"
            && restored.Entries[0].Voices["ja"] == "voice/start_ja",
            "Localization table must survive save/load with unicode text and voice slots.");
        Check(serializer.Serialize(restored) == yaml, "Localization save/load changed output.");
        var (messyTable, messyDiagnostics) = serializer.Deserialize("version: 1\nlanguages: [JA, en, xx-invalid!, en]\nentries:\n" +
            $"- id: {startId:D}\n  key: menu.start\n  texts: {{ ja: Hi, fr: Salut }}\n  voices: {{}}\n" +
            $"- id: {startId:D}\n  key: menu.start\n  texts: {{}}\n  voices: {{}}\n" +
            "- id: 00000000-0000-0000-0000-000000000000\n  key: menu.broken\n  texts: {}\n  voices: {}\n");
        Check(messyTable.Languages.SequenceEqual(["ja", "en"])
            && messyTable.Entries.Count == 1 && messyTable.Entries[0].Key == "menu.start"
            && !messyTable.Entries[0].Texts.ContainsKey("fr")
            && messyDiagnostics.Count >= 3,
            "Table load must normalize languages and drop invalid rows and cells with diagnostics.");
        Reject(() => serializer.Deserialize("version: 99\nlanguages: []\nentries: []"),
            "Unsupported table versions must be rejected.");
        Reject(() => serializer.Serialize(null!), "Null tables must be rejected.");
    }

    private static void ServiceResolution()
    {
        var table = SampleTable(out var startId);
        var store = new LocalizationStore(table);
        var id = new LocalizedTextId(startId);
        var service = new LocalizationService();
        Check(service.CurrentLanguage == LocalizationService.DefaultLanguage,
            "Preview and run language must start at the default.");
        Check(LocalizationService.ResolveText(store, id, "Fallback", "ja") == "スタート"
            && LocalizationService.ResolveText(store, id, "Fallback", "en") == "Start",
            "Resolution must follow the requested language.");
        Check(service.ResolveText(store, id, "Fallback") == "スタート", "Default resolution must use the default language.");
        service.CurrentLanguage = "en";
        Check(service.ResolveText(store, id, "Fallback") == "Start", "Instance resolution must follow CurrentLanguage.");
        service.CurrentLanguage = "ko";
        Check(service.ResolveText(store, id, "Fallback") == "スタート", "Missing languages must fall back to the default.");
        Check(service.ResolveVoice(store, id) == "voice/start_ja", "Missing voice languages must fall back to the default.");
        service.CurrentLanguage = "EN";
        Check(service.CurrentLanguage == "en" && service.ResolveText(store, id, "Fallback") == "Start",
            "Language codes must normalize to lowercase.");
        Reject(() => service.CurrentLanguage = "", "Empty language codes must be rejected.");
        Reject(() => service.CurrentLanguage = "e", "Single-letter language codes must be rejected.");
        Reject(() => service.CurrentLanguage = "has space", "Language codes with spaces must be rejected.");
        Check(LocalizationService.IsValidLanguageCode("ja") && LocalizationService.IsValidLanguageCode("ko")
            && LocalizationService.IsValidLanguageCode("ja-JP") && !LocalizationService.IsValidLanguageCode(null)
            && !LocalizationService.IsValidLanguageCode(""), "Language code validation must accept BCP47-style tags.");
        Check(LocalizationService.ResolveText(store, new LocalizedTextId(Guid.NewGuid()), "Fallback", "en") == "Fallback"
            && LocalizationService.ResolveText(null, id, "Fallback", "en") == "Fallback"
            && LocalizationService.ResolveText(store, null, null, "en") == "",
            "Missing entries and stores must resolve to the caller fallback.");
        Check(LocalizationService.ResolveVoice(store, new LocalizedTextId(Guid.NewGuid()), "en") is null
            && LocalizationService.ResolveVoice(null, id, "en") is null,
            "Missing voice slots must resolve to silence.");
        Check(LocalizationService.AvailableLanguages(store).SequenceEqual(["ja", "en"])
            && LocalizationService.AvailableLanguages(null).SequenceEqual([LocalizationService.DefaultLanguage]),
            "Available languages must follow the table columns.");
    }

    private static void SceneAndPlayIsolation()
    {
        var registry = LocalizationRegistry();
        var table = SampleTable(out var startId);
        var store = new LocalizationStore(table);
        var serializer = new SceneSerializer(registry, null, null, store);
        var scene = serializer.Deserialize("version: 3\nobjects: []");
        var item = scene.AddEmpty();
        item.Rename("Label");
        item.Attach(new Transform());
        item.Attach(new UiElement());
        var id = new LocalizedTextId(startId);
        item.Attach(new Text { Content = "Direct", LocalizedEntry = id });
        var yaml = serializer.Serialize(scene);
        Check(yaml.Contains(startId.ToString("D")) && yaml.Contains("loc:") && !yaml.Contains("スタート"),
            "Scene must store the entry ID, not its values.");
        var restored = serializer.Deserialize(yaml).Objects.Single().GetComponent<Text>()!;
        Check(restored.LocalizedEntry is { } restoredId && restoredId.Id == startId && restored.Content == "Direct",
            "Scene load must resolve the entry ID and keep direct content as fallback.");
        var clone = new SceneSerializer(registry).Clone(scene).Objects.Single().GetComponent<Text>()!;
        Check(clone.LocalizedEntry is { } cloneId && cloneId.Id == startId && !ReferenceEquals(cloneId, id),
            "Clone must carry the entry ID to a separate wrapper.");
        for (var run = 0; run < 2; run++)
        {
            using var play = new SceneRuntime(scene, registry, null, null, null, store.Clone());
            var live = play.Scene.Objects.Single().GetComponent<Text>()!;
            Check(LocalizationService.ResolveText(play.Scene.Localization, live.LocalizedEntry, live.Content, "en") == "Start",
                "Play must resolve saved entries for the run language.");
            Check(!ReferenceEquals(play.Scene.Localization, store), "Play must not share the edit snapshot.");
        }
        var missingSerializer = new SceneSerializer(registry);
        var missing = missingSerializer.Deserialize(yaml);
        var missingText = missing.Objects.Single().GetComponent<Text>()!;
        Check(missingText.LocalizedEntry is { } missingId && missingId.Id == startId,
            "Missing entries must keep their IDs.");
        Check(missingSerializer.Serialize(missing).Contains(startId.ToString("D")), "Missing entry IDs must survive saving.");
        Reject(() => missingSerializer.Deserialize(yaml.Replace("loc:", "ref:")),
            "Foreign reference shapes must be rejected for entry IDs.");
    }
}
