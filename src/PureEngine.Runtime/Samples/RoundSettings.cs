using PureEngine.Core;

namespace PureEngine.Editor.Samples;

public sealed class RoundSettings
{
    [Inspector] public int MaxRounds { get; set; } = 5;
    [Inspector] public float TurnSeconds { get; set; } = 30;
}
