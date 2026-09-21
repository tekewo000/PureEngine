namespace PureEngine.Core;

public sealed class ProjectDocument
{
    public int Version { get; set; }
    public string? Name { get; set; }
    public string? StartupScene { get; set; }
}
