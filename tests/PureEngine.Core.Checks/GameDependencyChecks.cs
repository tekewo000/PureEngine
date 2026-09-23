using Microsoft.Extensions.DependencyInjection;
using PureEngine.Core;
using PureEngine.Runtime;

/// <summary>
/// Verifies MS DI from runtime wiring (PureEngine.Runtime) through the real creation paths (edit, Play, and standalone runs).
/// Uses check-only registration and local components/services without referencing Editor or Avalonia.
/// Follow-up after A1 integration: verify through the real project registration path.
/// </summary>
static class GameDependencyChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static T Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    public static void Run()
    {
        PlainNew();
        EditAddSaveReload();
        PlayInjectionAndInspector();
        ScopedSharingAndIsolation();
        MissingDependencyFailsBeforeStart();
        TerminationAndOwnership();
        StopOwnsServiceLifetime();
        Console.WriteLine("PASS: game registration to edit/play paths, scoped sharing and separation, failures, and single-release ownership.");
    }

    private static ComponentRegistry RegistryFor(params (Type Type, string Id)[] entries)
    {
        var registry = new ComponentRegistry();
        foreach (var (type, id) in entries)
        {
            var method = typeof(ComponentRegistry).GetMethod(nameof(ComponentRegistry.Register))!
                .MakeGenericMethod(type);
            method.Invoke(registry, [id]);
        }
        return registry;
    }

    private static void PlainNew()
    {
        var random = new CheckRandomService();
        var session = new CheckBattleSession();
        var direct = new CheckInjectedPlayer(random, session);
        Check(ReferenceEquals(direct.Random, random) && ReferenceEquals(direct.Session, session),
            "Plain new must keep the given services.");
        Check(direct.Hp == 100, "New component must start from initializers.");
        direct.Dispose();
        Check(!session.IsDisposed, "Component Dispose must not dispose the injected service.");
        session.Dispose();
    }

    private static void EditAddSaveReload()
    {
        using var edit = GameSession.Create(CheckGameServices.Configure);
        var target = new SceneObject("Edit me");
        var created = (CheckInjectedPlayer)edit.Factory(typeof(CheckInjectedPlayer));
        target.Attach(created);
        var added = target.GetComponent<CheckInjectedPlayer>()!;
        Check(added.Random is not null && added.Session is not null, "Edit instance must have services.");
        added.Hp = 37;

        var registry = RegistryFor((typeof(CheckInjectedPlayer), "checks.injected-player"));
        var serializer = new SceneSerializer(registry);
        var scene = new Scene();
        var item = scene.AddEmpty();
        item.Rename("Hero");
        Check(target.Detach(added), "Edited instance must detach from its draft owner before reuse.");
        item.Attach(added);
        var yaml = serializer.Serialize(scene);
        Check(yaml.Contains("Hp: 37"), "Edited Inspector value must be saved.");

        var reloaded = serializer.Deserialize(yaml, edit.Factory);
        var copy = reloaded.Objects[0].GetComponent<CheckInjectedPlayer>()!;
        Check(copy.Hp == 37 && !ReferenceEquals(copy, added), "Reload must restore values into a new instance.");
        Check(copy.Random is not null && copy.Session is not null, "Reloaded edit instance must have services.");
        Check(ReferenceEquals(copy.Session, added.Session),
            "Same edit scope must share the Scoped session.");
    }

    private static void PlayInjectionAndInspector()
    {
        var registry = RegistryFor((typeof(CheckInjectedPlayer), "checks.injected-player"));
        var source = new Scene();
        var item = source.AddEmpty();
        item.Rename("Hero");
        var authoringRandom = new CheckRandomService();
        var authoringSession = new CheckBattleSession();
        item.Attach(new CheckInjectedPlayer(authoringRandom, authoringSession) { Hp = 63 });

        using var play = PlaySession.Prepare(source, registry, CheckGameServices.Configure);
        var copy = play.Runtime.Scene.Objects[0].GetComponent<CheckInjectedPlayer>()!;
        Check(copy.Hp == 63, "Play must restore Inspector values.");
        Check(!ReferenceEquals(copy.Random, authoringRandom) && !ReferenceEquals(copy.Session, authoringSession),
            "Play must resolve fresh services instead of copying authoring references.");
        Check(copy.StartedHp == -1, "Constructor must not see saved values.");
        play.Start();
        Check(copy.Starts == 1 && copy.StartedHp == 63, "Save-based initialization belongs in Start.");
        play.Step(0.1f);
        Check(copy.Updates == 1, "Injected Play must update.");
    }

    private static void ScopedSharingAndIsolation()
    {
        var registry = RegistryFor((typeof(CheckInjectedPlayer), "checks.injected-player"));
        var source = new Scene();
        for (var i = 0; i < 2; i++)
            source.AddEmpty().Attach(new CheckInjectedPlayer(new CheckRandomService(), new CheckBattleSession()) { Hp = 10 + i });

        using var edit = GameSession.Create(CheckGameServices.Configure);
        var editSession = edit.Services.GetRequiredService<CheckBattleSession>();
        var editRandom = edit.Services.GetRequiredService<ICheckRandomService>();
        editRandom.Next(10);

        CheckBattleSession firstSession;
        ICheckRandomService firstRandom;
        using (var first = PlaySession.Prepare(source, registry, CheckGameServices.Configure))
        {
            var copies = first.Runtime.Scene.Objects.Select(o => o.GetComponent<CheckInjectedPlayer>()!).ToArray();
            Check(ReferenceEquals(copies[0].Session, copies[1].Session)
                && ReferenceEquals(copies[0].Random, copies[1].Random),
                "Scoped services must be shared within one Play.");
            firstSession = copies[0].Session;
            firstRandom = copies[0].Random;
            Check(!ReferenceEquals(firstSession, editSession) && !ReferenceEquals(firstRandom, editRandom),
                "Play must not share service state with edit.");
            first.Start();
            Check(firstSession.Members == 2, "Play state must accumulate within the Play.");
        }

        using (var second = PlaySession.Prepare(source, registry, CheckGameServices.Configure))
        {
            var copies = second.Runtime.Scene.Objects.Select(o => o.GetComponent<CheckInjectedPlayer>()!).ToArray();
            Check(!ReferenceEquals(copies[0].Session, firstSession)
                && !ReferenceEquals(copies[0].Random, firstRandom),
                "A replay must not carry previous Play state, including Singletons of that provider.");
            Check(copies[0].Session.Members == 0 && ((CheckRandomService)copies[0].Random).Calls == 0,
                "Replay services must start fresh.");
            Check(firstSession.IsDisposed, "The previous Play Scope must have ended its services.");
            second.Start();
        }

        Check(editRandom.Calls == 1 && editSession.Members == 0,
            "Edit service state must be unaffected by Plays.");
    }

    private static void MissingDependencyFailsBeforeStart()
    {
        GoodProbe.Disposed.Clear();
        GoodProbe.LastSession = null;
        var registry = new ComponentRegistry();
        registry.Register<GoodProbe>("di.good");
        registry.Register<NeedMissing>("di.missing");
        var source = new Scene();
        var first = source.AddEmpty();
        first.Rename("First");
        first.Attach(new GoodProbe(new CheckBattleSession()) { Tag = "good" });
        var second = source.AddEmpty();
        second.Rename("Second");
        second.Attach(new NeedMissing(new UnregisteredService()));

        PlaySession? play = null;
        var error = Reject<InvalidOperationException>(() => play = PlaySession.Prepare(source, registry, CheckGameServices.Configure));
        Check(play is null, "Failed preparation must not produce a running session.");
        Check(error.Message.Contains("di.missing") && error.Message.Contains(nameof(NeedMissing))
            && error.Message.Contains("Second") && error.InnerException is not null
            && error.InnerException.Message.Contains(nameof(UnregisteredService)),
            "Missing dependencies must fail before Start with the target component identified.");
        Check(GoodProbe.Disposed.Count == 1 && GoodProbe.Disposed[0].Tag == "good",
            "Preparation failure must dispose created components without Start.");
        Check(GoodProbe.Disposed[0].Starts == 0, "Failed preparation must call no Start.");
        Check(GoodProbe.LastSession?.IsDisposed == true,
            "Preparation failure must still end the Play Scope.");
        Check(source.Objects[0].GetComponent<GoodProbe>()!.Starts == 0,
            "Authoring instances must gain no execution state.");
    }

    private static void TerminationAndOwnership()
    {
        var registry = RegistryFor((typeof(CheckInjectedPlayer), "checks.injected-player"));
        var source = new Scene();
        source.AddEmpty().Attach(new CheckInjectedPlayer(new CheckRandomService(), new CheckBattleSession()) { Hp = 5 });
        CheckInjectedPlayer copy;
        CheckBattleSession playSession;
        using (var play = PlaySession.Prepare(source, registry, CheckGameServices.Configure))
        {
            copy = play.Runtime.Scene.Objects[0].GetComponent<CheckInjectedPlayer>()!;
            playSession = copy.Session;
            play.Start();
            play.Step(0);
            Check(copy is { Starts: 1, Updates: 1 } && copy.Destroys == 0 && copy.Disposes == 0,
                "Termination must not run before Stop.");
        }
        Check(copy.Destroys == 1 && copy.Disposes == 1, "Normal end must destroy and dispose once.");
        Check(playSession.DisposeCalls == 1 && playSession.IsDisposed, "DI-owned services end with the Scope.");
        Check(copy.Session.DisposeCalls == 1, "Component must not double-dispose the injected service.");
        Check(playSession.Members == 1, "Component must finish using live services during Destroy.");
    }

    private static void StopOwnsServiceLifetime()
    {
        foreach (var mode in new[] { "stop", "before-start", "dispose-start", "stop-update", "runtime-stop",
            "fail-start", "fail-update", "dispose-destroy" })
        {
            var registry = new ComponentRegistry();
            registry.Register<StopProbe>("stop-probe");
            var source = new Scene();
            source.AddEmpty().Attach(new StopProbe(new CheckBattleSession()));
            using var play = PlaySession.Prepare(source, registry, CheckGameServices.Configure);
            var probe = play.Runtime.Scene.Objects[0].GetComponent<StopProbe>()!;
            var failure = new ApplicationException("lifecycle failed");
            if (mode == "dispose-start") probe.OnStart = play.Dispose;
            if (mode == "stop-update") probe.OnUpdate = play.Stop;
            if (mode == "runtime-stop") probe.OnUpdate = play.Runtime.Stop;
            if (mode == "fail-start") probe.OnStart = () => throw failure;
            if (mode == "fail-update") probe.OnUpdate = () => throw failure;
            if (mode == "dispose-destroy") probe.OnDestroy = play.Dispose;
            if (mode != "before-start") play.Start();
            if (mode is "stop-update" or "runtime-stop" or "fail-update") play.Step(0);
            if (mode is "stop" or "before-start" or "dispose-destroy") play.Stop();
            Check(probe.Session.IsDisposed && probe.Session.DisposeCalls == 1,
                $"Services must be released on actual termination ({mode}).");
            Check(probe.DestroyedAlive && probe.DisposedAlive && probe.Disposes == 1,
                $"Services must outlive component termination ({mode}).");
            Check(play.Runtime.Errors.Count == (mode.StartsWith("fail-") ? 1 : 0),
                $"Unexpected termination errors ({mode}).");
            if (mode.StartsWith("fail-")) Check(play.Runtime.Errors[0].Exception == failure, "Original failure lost.");
            play.Stop();
            play.Dispose();
            Check(probe.Session.DisposeCalls == 1 && probe.Disposes == 1, "Repeated termination must be a no-op.");
        }
    }

    private static class CheckGameServices
    {
        public static void Configure(IServiceCollection services)
        {
            services.AddScoped<ICheckRandomService, CheckRandomService>();
            services.AddScoped<CheckBattleSession>();
        }
    }

    private interface ICheckRandomService
    {
        int Calls { get; }
        int Next(int max);
    }

    private sealed class CheckRandomService : ICheckRandomService
    {
        private int _calls;

        public int Calls => _calls;

        public int Next(int max)
        {
            _calls++;
            return 0;
        }
    }

    private sealed class CheckBattleSession : IDisposable
    {
        private bool _disposed;

        public int Members { get; private set; }

        public bool IsDisposed => _disposed;

        public int DisposeCalls { get; private set; }

        public void Join() => Members++;

        public void Dispose()
        {
            if (_disposed) throw new InvalidOperationException("BattleSession must be disposed once.");
            _disposed = true;
            DisposeCalls++;
        }
    }

    private sealed class CheckInjectedPlayer(GameDependencyChecks.ICheckRandomService random, GameDependencyChecks.CheckBattleSession session) : IDisposable
    {
        private readonly ICheckRandomService _random = random ?? throw new ArgumentNullException(nameof(random));
        private readonly CheckBattleSession _session = session ?? throw new ArgumentNullException(nameof(session));
        private bool _disposed;

        public int Starts;
        public int Updates;
        public int Destroys;
        public int Disposes;
        public int StartedHp = -1;

        public ICheckRandomService Random => _random;
        public CheckBattleSession Session => _session;

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

    private sealed class StopProbe(CheckBattleSession session) : IDisposable
    {
        public CheckBattleSession Session { get; } = session;
        public Action? OnStart, OnUpdate, OnDestroy;
        public bool DestroyedAlive, DisposedAlive;
        public int Disposes;
        [Start] private void Begin() => OnStart?.Invoke();
        [Update] private void Tick() => OnUpdate?.Invoke();
        [Destroy] private void End() { OnDestroy?.Invoke(); DestroyedAlive = !Session.IsDisposed; }
        public void Dispose() { Disposes++; DisposedAlive = !Session.IsDisposed; }
    }

    private sealed class UnregisteredService
    {
    }

    private sealed class NeedMissing
    {
        public NeedMissing(UnregisteredService service) => _ = service;
#pragma warning disable CA1822 // Reflection tests require these lifecycle/Inspector members to remain instance members.
        [Start] private void Begin() { }
#pragma warning restore CA1822
    }

    private sealed class GoodProbe(GameDependencyChecks.CheckBattleSession session) : IDisposable
    {
        public static readonly List<GoodProbe> Disposed = [];
        public static CheckBattleSession? LastSession;

        public readonly CheckBattleSession Session = session;
        [Inspector] public string Tag = "";
        public int Starts;
        private bool _disposed;

        [Start] private void Begin() => Starts++;

        public void Dispose()
        {
            if (_disposed) throw new InvalidOperationException("Dispose must run once.");
            _disposed = true;
            LastSession = Session;
            Disposed.Add(this);
        }
    }
}
