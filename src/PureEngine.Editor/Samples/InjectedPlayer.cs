using PureEngine.Core;

namespace PureEngine.Editor.Samples;

/// <summary>
/// Minimal example of constructor injection. Dependencies come from the constructor, saved data from [Inspector].
/// Initialize with saved values in Start; never start communication or game progression in the constructor.
/// Its own Dispose releases owned resources; never dispose injected services.
/// </summary>
public sealed class InjectedPlayer(IRandomService random, BattleSession session) : IDisposable
{
    private readonly IRandomService _random = random ?? throw new ArgumentNullException(nameof(random));
    private readonly BattleSession _session = session ?? throw new ArgumentNullException(nameof(session));
    private bool _disposed;

    public int Starts;
    public int Updates;
    public int Destroys;
    public int Disposes;
    public int StartedHp = -1;

    public IRandomService Random => _random;
    public BattleSession Session => _session;

    [Inspector] public int Hp { get; set; } = 100;

    [Start] private void OnStart()
    {
        Starts++;
        StartedHp = Hp;
        _session.Join();
        _ = _random.Next(100);
    }

    [Update] private void Tick(float _) => Updates++;

    [Destroy] private void OnEnd() => Destroys++;

    public void Dispose()
    {
        if (_disposed) throw new InvalidOperationException("Dispose must run once.");
        _disposed = true;
        Disposes++;
    }
}
