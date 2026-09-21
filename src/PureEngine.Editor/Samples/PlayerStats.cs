using PureEngine.Core.Attributes;

namespace PureEngine.Editor.Samples;

public sealed class PlayerStats
{
    [Inspector] public string Name { get; set; } = "Player";
    [Inspector] public int Hp { get; set; } = 100;
}
