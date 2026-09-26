namespace PureEngine.Runtime;

/// <summary>Versioned, source-free game package metadata. Paths are relative to the package or Data root.</summary>
public sealed class GamePackageManifest
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "";
    public string StartupScene { get; set; } = "";
    public string? GameAssembly { get; set; }
    public Dictionary<string, string> Types { get; set; } = [with(StringComparer.Ordinal)];
}
