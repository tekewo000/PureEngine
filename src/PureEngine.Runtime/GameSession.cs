using Microsoft.Extensions.DependencyInjection;

namespace PureEngine.Runtime;

/// <summary>
/// An independent set of services (provider plus explicit Scope) and the factory for Core.
/// Takes registration logic from outside and holds separate instances for editing and each Play run.
/// Shares no state between editing and Play, or between different Play runs, including Singletons.
/// Never resolves Scoped services directly from the root provider; always goes through this Scope.
/// Does not depend on the Editor or Avalonia. Takes game-side registration as configure.
/// </summary>
public sealed class GameSession : IDisposable
{
    private ServiceProvider? _provider;
    private IServiceScope? _scope;
    private bool _disposed;

    /// <summary>Core only knows this factory. Keeps the MS DI reference here.</summary>
    public Func<Type, object> Factory { get; }

    /// <summary>Provider of the explicit Scope. For direct checks of Scoped services.</summary>
    public IServiceProvider Services => _scope?.ServiceProvider
        ?? throw new ObjectDisposedException(nameof(GameSession));

    private GameSession(ServiceProvider provider, IServiceScope scope)
    {
        _provider = provider;
        _scope = scope;
        Factory = type => ActivatorUtilities.CreateInstance(Services, type);
    }

    /// <summary>
    /// Creates an independent service set from external registration logic.
    /// The caller passes the service registration for that game/project.
    /// </summary>
    public static GameSession Create(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var services = new ServiceCollection();
        configure(services);
        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
        try { return new GameSession(provider, provider.CreateScope()); }
        catch (Exception error)
        {
            try { provider.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var scope = _scope;
        var provider = _provider;
        _scope = null;
        _provider = null;
        // Shuts down the Scope first and the provider second. The owner (PlaySession)
        // completes Component shutdown first, then calls here.
        var errors = new List<Exception>();
        try { scope?.Dispose(); }
        catch (Exception error) { errors.Add(error); }
        try { provider?.Dispose(); }
        catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0) throw new AggregateException("Game services cleanup failed.", errors);
    }
}
