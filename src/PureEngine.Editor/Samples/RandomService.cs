using PureEngine.Core;

namespace PureEngine.Editor.Samples;

/// <summary>Play 中に共有する乱数。Scoped 解決の共有・分離を確認するための決定論的な状態。</summary>
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
