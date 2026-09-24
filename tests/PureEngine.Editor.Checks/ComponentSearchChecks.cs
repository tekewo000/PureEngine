using PureEngine.Core;
using PureEngine.Editor;

internal static class ComponentSearchChecks
{
    public static void Run()
    {
        using var components = new ProjectComponents();
        var registry = components.Registry;
        Check(registry.Ids.Contains("core.transform"), "Transform must be registered.");
        Check(registry.Ids.Contains("core.ui-element"), "UiElement must be registered.");
        Check(registry.Ids.Contains("core.image"), "Image must be registered.");
        Check(registry.Ids.Contains("core.text"), "Text must be registered.");

        var all = ComponentAssets.SearchCandidates(registry, "");
        Check(all.Any(candidate => candidate.TypeId == "core.transform")
            && all.Any(candidate => candidate.TypeId == "core.ui-element")
            && all.Any(candidate => candidate.TypeId == "core.image")
            && all.Any(candidate => candidate.TypeId == "core.text"),
            "Search without query must list engine UI components.");
        var filtered = ComponentAssets.SearchCandidates(registry, "image");
        Check(filtered.Any(candidate => candidate.TypeId == "core.image")
            && filtered.All(candidate => candidate.Type.Name.Contains("image", StringComparison.OrdinalIgnoreCase)
                || (candidate.Type.FullName ?? "").Contains("image", StringComparison.OrdinalIgnoreCase)
                || candidate.TypeId.Contains("image", StringComparison.OrdinalIgnoreCase)),
            "Search must filter by name and typeId.");
        Check(ComponentAssets.SearchCandidates(registry, "no-such-component-xyz").Count == 0,
            "Search with no match must be empty.");
        var textFiltered = ComponentAssets.SearchCandidates(registry, "text");
        Check(textFiltered.Any(candidate => candidate.TypeId == "core.text"),
            "Search must find Text by name.");

        using var services = PureEngine.Runtime.GameSession.Create(GameServices.Configure);
        var target = new SceneObject("Target");
        var imageType = registry.GetType("core.image");
        Check(components.CanAttach(target, imageType), "Unattached engine type must be attachable.");
        Check(components.TryAttach(target, imageType, services.Factory), "Engine attach must reuse the factory.");
        Check(target.GetComponent<PureEngine.Core.Image>() is not null, "Attached Image must be retrievable.");
        Check(!components.CanAttach(target, imageType) && !components.TryAttach(target, imageType, services.Factory)
            && target.Components.Count == 1, "Duplicate engine attach must be rejected.");
        Check(!components.TryAttach(null, imageType, services.Factory), "Attach without a target must be rejected.");
        Check(!components.TryAttach(target, null, services.Factory), "Attach without a type must be rejected.");
        Console.WriteLine("PASS: engine component registration, search, factory attach, and duplicate prevention.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
