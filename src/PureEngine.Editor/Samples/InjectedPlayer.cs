using PureEngine.Core.Attributes;

namespace PureEngine.Editor.Samples;

/// <summary>
/// コンストラクタ注入の最小例。依存は ctor、保存データは [Inspector]。
/// 保存値を使う初期化は Start で行い、ctor では通信やゲーム進行を開始しない。
/// 所有資源の解放は自身の Dispose が担当し、注入されたサービスは Dispose しない。
/// </summary>
public sealed class InjectedPlayer : IDisposable
{
    private readonly IRandomService _random;
    private readonly BattleSession _session;
    private bool _disposed;

    public int Starts;
    public int Updates;
    public int Destroys;
    public int Disposes;
    public int StartedHp = -1;

    public IRandomService Random => _random;
    public BattleSession Session => _session;

    public InjectedPlayer(IRandomService random, BattleSession session)
    {
        _random = random ?? throw new ArgumentNullException(nameof(random));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    [Inspector] public int Hp { get; set; } = 100;

    [Start] private void OnStart()
    {
        Starts++;
        StartedHp = Hp;
        _session.Join();
        _ = _random.Next(100);
    }

    [Update] private void Tick(float dt) => Updates++;

    [Destroy] private void OnEnd() => Destroys++;

    public void Dispose()
    {
        if (_disposed) throw new InvalidOperationException("Dispose must run once.");
        _disposed = true;
        Disposes++;
    }
}
