using Microsoft.Extensions.DependencyInjection;
using PureEngine.Core;
using PureEngine.Runtime;

namespace PureEngine.Editor;

/// <summary>Owns a validated startup scene, editing services and project code until transferred to an editor.</summary>
public sealed class ProjectSession : IDisposable
{
    public ProjectFile Project { get; }
    public ProjectComponents Components { get; }
    public Scene Scene { get; }
    internal bool SceneNeedsSave { get; private init; }
    public GameSession EditServices { get; }
    private bool _disposed;
    private bool _ownershipTransferred;
    private UserCodeIncrementalCompiler? _userCodeCache;

    private ProjectSession(ProjectFile project, ProjectComponents components, Scene scene, GameSession services, UserCodeIncrementalCompiler? userCodeCache)
    {
        Project = project;
        Components = components;
        Scene = scene;
        EditServices = services;
        _userCodeCache = userCodeCache;
    }

    internal void TransferOwnership()
    {
        ObjectDisposedException.ThrowIf(_disposed || _ownershipTransferred, this);
        _ownershipTransferred = true;
    }

    /// <summary>Takes ownership of the incremental compilation cache, usually to reuse it for save watching. Returns null for custom compilation.</summary>
    internal UserCodeIncrementalCompiler? TakeUserCodeCache()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var cache = _userCodeCache;
        _userCodeCache = null;
        return cache;
    }

    // Synchronous entry for tools/checks; the Launcher always uses OpenAsync.
    public static ProjectSession Open(string manifestPath)
    {
        var project = ProjectFile.Open(manifestPath);
        ProjectCodeWorkspace.Ensure(project);
        var cache = new UserCodeIncrementalCompiler(project.RootDirectory);
        UserCodeCompileResult compiled;
        try
        {
            compiled = cache.CompileProject();
        }
        catch
        {
            try { cache.Dispose(); } catch { }
            throw;
        }
        return Prepare(project, compiled, cache);
    }

    public static async Task<ProjectSession> OpenAsync(string manifestPath,
        Func<string, CancellationToken, Task<UserCodeCompileResult>>? compileAsync = null, CancellationToken cancellationToken = default)
    {
        var project = ProjectFile.Open(manifestPath);
        ProjectCodeWorkspace.Ensure(project);
        using var tracker = new UserCodeCompileTracker(project.RootDirectory, compileAsync);
        var attempt = await tracker.CompileAsync(tracker.Request(), cancellationToken);
        if (cancellationToken.IsCancellationRequested || attempt.Canceled || attempt.Superseded)
        {
            UserCodeCompileTracker.Release(attempt.Result);
            throw new OperationCanceledException(cancellationToken);
        }
        var cache = tracker.TakeCache();
        try
        {
            // Keep the caller's context: constructors and scene restoration run on the UI thread.
            return Prepare(project, attempt.Result ?? throw new InvalidOperationException("Compilation returned no result."), cache);
        }
        catch
        {
            if (cache is not null)
            {
                try { cache.Dispose(); } catch { }
            }
            throw;
        }
    }

    private static ProjectSession Prepare(ProjectFile project, UserCodeCompileResult compiled, UserCodeIncrementalCompiler? userCodeCache)
    {
        var components = new ProjectComponents();
        Scene? scene = null;
        GameSession? services = null;
        try
        {
            foreach (var diagnostic in compiled.Diagnostics)
            {
                var message = UserCodeCompiler.FormatDiagnostic(diagnostic);
                if (diagnostic.IsError) Log.Engine.Error(message);
                else Log.Engine.Warning(message);
            }
            var registry = components.CreateCandidateRegistry(compiled);
            _ = project.ListDirectories();
            _ = project.ListFiles("Scenes");
            var assets = ScanProjectAssets(project, registry);
            services = GameSession.Create(services =>
            {
                GameServices.ForUserCode(compiled.Success ? compiled : null)(services);
                if (assets is not null) services.AddSingleton(assets);
            });
            bool membersChanged;
            try
            {
                scene = new SceneSerializer(registry, assets, PrefabCatalog.ScanFolder(project.RootDirectory, out _)).Deserialize(File.ReadAllText(project.StartupScenePath), out membersChanged, services.Factory);
            }
            catch (Exception error) when (!compiled.Success)
            {
                throw new InvalidDataException("Cannot open the startup scene because C# compilation failed.\n"
                    + string.Join(Environment.NewLine, compiled.Diagnostics.Where(d => d.IsError)
                        .Select(UserCodeCompiler.FormatDiagnostic)), error);
            }
            components.Adopt(compiled.Success ? compiled : null);
            var session = new ProjectSession(project, components, scene, services, userCodeCache) { SceneNeedsSave = membersChanged };
            userCodeCache = null;
            return session;
        }
        catch (Exception error)
        {
            var errors = new List<Exception> { error };
            try
            {
                if (scene is not null) ComponentAssets.DisposeComponents(scene.OwnedComponents);
            }
            catch (Exception cleanup) { errors.Add(cleanup); }
            try { services?.Dispose(); }
            catch (Exception cleanup) { errors.Add(cleanup); }
            finally
            {
                components.Dispose();
                ProjectComponents.Release(compiled);
                if (userCodeCache is not null)
                {
                    try { userCodeCache.Dispose(); } catch { }
                }
            }
            if (errors.Count > 1) throw new AggregateException("Project open and cleanup failed.", errors);
            throw;
        }
    }

    public static ProjectSession Create(string parentDirectory, string name)
    {
        var components = new ProjectComponents();
        GameSession? services = null;
        try
        {
            var scene = new Scene();
            var project = ProjectFile.Create(parentDirectory, name, new SceneSerializer(components.Registry).Serialize(scene));
            ProjectCodeWorkspace.Ensure(project);
            var assets = ScanProjectAssets(project, components.Registry);
            services = GameSession.Create(services =>
            {
                GameServices.ForProject(components)(services);
                if (assets is not null) services.AddSingleton(assets);
            });
            return new ProjectSession(project, components, scene, services, null);
        }
        catch
        {
            try { services?.Dispose(); }
            finally { components.Dispose(); }
            throw;
        }
    }

    private static DataAssetStore? ScanProjectAssets(ProjectFile project, ComponentRegistry registry)
    {
        try
        {
            var store = DataAssetStore.ScanFolder(project.RootDirectory, registry, out var diagnostics);
            foreach (var diagnostic in diagnostics) Log.Engine.Warning(diagnostic);
            return store;
        }
        catch (Exception error)
        {
            Log.Engine.Error("Cannot scan data assets.", error);
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed || _ownershipTransferred) return;
        _disposed = true;
        var errors = new List<Exception>();
        try { ComponentAssets.DisposeComponents(Scene.OwnedComponents); }
        catch (Exception error) { errors.Add(error); }
        try { EditServices.Dispose(); }
        catch (Exception error) { errors.Add(error); }
        try { Components.Dispose(); }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            if (_userCodeCache is not null)
            {
                try { _userCodeCache.Dispose(); }
                catch (Exception error) { errors.Add(error); }
                finally { _userCodeCache = null; }
            }
        }
        if (errors.Count != 0) throw new AggregateException("Project cleanup failed.", errors);
    }
}
