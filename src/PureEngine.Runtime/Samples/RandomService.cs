namespace PureEngine.Editor.Samples;

/// <summary>Random numbers shared during play. Deterministic state for verifying sharing and isolation of scoped resolution.</summary>
public interface IRandomService
{
    int Calls { get; }
    int Next(int max);
}

public sealed class RandomService : IRandomService
{
    private int _calls;

    public int Calls => _calls;

    public int Next(int max)
    {
        _calls++;
        return 0;
    }
}
