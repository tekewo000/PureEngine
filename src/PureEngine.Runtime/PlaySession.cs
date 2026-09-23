using Microsoft.Extensions.DependencyInjection;
using PureEngine.Core;

namespace PureEngine.Runtime;

/// <summary>
/// One Play run (including standalone runs). Creates a Play provider and Scope and passes factory to SceneRuntime.
/// Starts after creation, restore, and validation succeed, and on Stop completes Runtime shutdown before shutting down Scope and provider.
/// On preparation failure, still releases owned resources and preserves both the original and cleanup exceptions.
/// Does not depend on the Editor or Avalonia. Takes the Registry and registration logic from outside.
/// </summary>
public sealed class PlaySession : IDisposable
{
    private GameSession? _services;
    public SceneRuntime Runtime { get; }

    private PlaySession(GameSession services, SceneRuntime runtime)
    {
        _services = services;
        Runtime = runtime;
        runtime.Stopped += ReleaseServices;
    }

    /// <summary>
    /// Prepares with an independent service set for Play. Does not call Start.
    /// Takes registry from the calling project owner and game service registration as configure.
    /// </summary>
    public static PlaySession Prepare(Scene source, ComponentRegistry registry, Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(configure);
        var services = GameSession.Create(configure);
        SceneRuntime runtime;
        try
        {
            runtime = new SceneRuntime(source, registry, services.Factory);
        }
        catch (Exception preparationError)
        {
            // Components created during Clone were already released in reverse order by serializer/runtime.
            // Here shuts down the Play Scope and provider while preserving the exception.
            try
            {
                services.Dispose();
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Play preparation and cleanup failed.", preparationError, cleanupError);
            }
            throw;
        }
        return new PlaySession(services, runtime);
    }

    public void Start() => Runtime.Start();

    public void Step(float dt) => Runtime.Step(dt);

    public void Stop() => Runtime.Stop();

    public void Dispose() => Stop();

    private void ReleaseServices()
    {
        var services = _services;
        _services = null;
        // Runtime.Stop may defer termination until the current callback returns.
        // Automatic stops on lifecycle failure and direct Runtime.Stop also reach this point.
        services?.Dispose();
    }
}
