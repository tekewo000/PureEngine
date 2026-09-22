using PureEngine.Core;

namespace PureEngine.Editor.Samples;

public sealed class PlayerStats
{
    [Inspector]
    public string Name { get; set; } = "Player";

    [Inspector]
    public int Hp { get; set; } = 100;

#pragma warning disable CA1822 // Empty lifecycle examples must remain instance methods for ComponentSchema.
    [Start]
    private void Start()
    {

    }

    [Update]
    private void Update()
    {

    }

#pragma warning restore CA1822
}
