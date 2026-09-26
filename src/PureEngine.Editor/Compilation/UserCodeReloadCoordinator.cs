using Microsoft.Extensions.DependencyInjection;
using PureEngine.Core;
using PureEngine.Runtime;

namespace PureEngine.Editor;

/// <summary>Adopts compiled code on the scene's owning thread; has no UI dependencies.</summary>
public sealed class UserCodeReloadCoordinator
{
    public bool IsReloading { get; private set; }

    /// <summary>Prepares all open asset documents before replacing the scene, registry, or services.</summary>
    public UserCodeReloadOutcome Apply(EditorDocuments documents, ProjectComponents components, UserCodeCompileResult compiled, ProjectFile? project)
    {
        DataAssetEditState? asset = null;
        List<DataAssetEditState>? rows = null;
        Action<IServiceCollection>? configure = null;
        try
        {
            if (compiled.Success)
            {
                var registry = components.CreateCandidateRegistry(compiled);
                asset = documents.Asset?.Migrate(components.Registry, registry);
                rows = documents.Table.PrepareReload(components.Registry, registry);
                var store = EditorDocuments.BuildAssetStore(project, registry);
                if (store is not null) configure = services => services.AddSingleton(store);
            }
        }
        catch (Exception error)
        {
            ProjectComponents.Release(compiled);
            return new(false, compiled.Diagnostics, error);
        }
        var outcome = Apply(documents.Current, components, compiled, configure);
        if (outcome.Adopted)
        {
            documents.Asset = asset;
            documents.Table.AdoptReload(rows, components);
            documents.RefreshAssetOwnership();
        }
        return outcome;
    }

    // Takes ownership of compiled, whether preparation succeeds or fails.
    // extraConfigure registers reload-scoped extras (such as the data asset snapshot) into the new service set.
    public UserCodeReloadOutcome Apply(EditSceneStore state, ProjectComponents components, UserCodeCompileResult compiled, Action<IServiceCollection>? extraConfigure = null)
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
            services = GameSession.Create(services =>
            {
                GameServices.ForUserCode(compiled)(services);
                extraConfigure?.Invoke(services);
            });
            candidate = SceneCodeMigrator.Migrate(state.Current, components.Registry, registry, out var membersChanged,
                services.Factory, services.Services.GetService<DataAssetStore>());
            var oldCode = components.Exchange(compiled);
            var previous = state.Replace(candidate, state.Path, state.IsDirty || membersChanged);
            var previousServices = state.ReplaceServices(services);
            adopted = true;
            try { ComponentAssets.DisposeComponents(previous.OwnedComponents); }
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
                        ComponentAssets.DisposeComponents(candidate.OwnedComponents);
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
