using PureEngine.Core;
using PureEngine.Runtime;

namespace PureEngine.Editor;

/// <summary>Adopts compiled code on the scene's owning thread; has no UI dependencies.</summary>
public sealed class UserCodeReloadCoordinator
{
    public bool IsReloading { get; private set; }

    // Takes ownership of compiled, whether preparation succeeds or fails.
    public UserCodeReloadOutcome Apply(EditSceneStore state, ProjectComponents components, UserCodeCompileResult compiled)
    {
        if (IsReloading) throw new InvalidOperationException("Reload cannot be nested.");
        IsReloading = true;
        Scene? candidate = null;
        GameSession? services = null;
        var adopted = false;
        var errors = new List<Exception>();
        try
        {
            if (!compiled.Success) return new(false, compiled.Diagnostics, null);
            var registry = components.CreateCandidateRegistry(compiled);
            services = GameSession.Create(GameServices.ForUserCode(compiled));
            candidate = SceneCodeMigrator.Migrate(state.Current, components.Registry, registry, out var membersChanged, services.Factory);
            var oldCode = components.Exchange(compiled);
            var previous = state.Replace(candidate, state.Path, state.IsDirty || membersChanged);
            var previousServices = state.ReplaceServices(services);
            adopted = true;
            try { ComponentAssets.DisposeComponents(previous.Objects.SelectMany(item => item.Components)); }
            catch (Exception error) { errors.Add(error); }
            try { previousServices.Dispose(); }
            catch (Exception error) { errors.Add(error); }
            finally { ProjectComponents.Release(oldCode); }
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            if (!adopted)
            {
                try
                {
                    if (candidate is not null)
                        ComponentAssets.DisposeComponents(candidate.Objects.SelectMany(item => item.Components));
                }
                catch (Exception error) { errors.Add(error); }
                try { services?.Dispose(); }
                catch (Exception error) { errors.Add(error); }
                finally { ProjectComponents.Release(compiled); }
            }
            IsReloading = false;
        }
        return new(adopted, compiled.Diagnostics, errors.Count == 0 ? null : new AggregateException(errors));
    }
}

public sealed record UserCodeReloadOutcome(bool Adopted, IReadOnlyList<UserCodeDiagnostic> Diagnostics, Exception? Error);
