using PureEngine.Core;
using PureEngine.Runtime;

namespace PureEngine.Editor;

/// <summary>Owns a validated startup scene, editing services and project code until transferred to an editor.</summary>
public sealed class ProjectSession : IDisposable
{
    public ProjectFile Project { get; }
    public ProjectComponents Components { get; }
    public Scene Scene { get; }
    public GameSession EditServices { get; }
    private bool _disposed;
    private bool _ownershipTransferred;

    private ProjectSession(ProjectFile project, ProjectComponents components, Scene scene, GameSession services)
    {
        Project = project;
        Components = components;
        Scene = scene;
        EditServices = services;
    }

    internal void TransferOwnership()
    {
        ObjectDisposedException.ThrowIf(_disposed || _ownershipTransferred, this);
        _ownershipTransferred = true;
    }

    // Synchronous entry for tools/checks; the Launcher always uses OpenAsync.
    public static ProjectSession Open(string manifestPath)
    {
        var project = ProjectFile.Open(manifestPath);
        ProjectCodeWorkspace.Ensure(project);
        return Prepare(project, UserCodeCompiler.CompileProject(project.RootDirectory));
    }

    public static async Task<ProjectSession> OpenAsync(string manifestPath, CancellationToken cancellationToken = default,
        Func<string, CancellationToken, Task<UserCodeCompileResult>>? compileAsync = null)
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
        // Keep the caller's context: constructors and scene restoration run on the UI thread.
        return Prepare(project, attempt.Result ?? throw new InvalidOperationException("Compilation returned no result."));
    }

    private static ProjectSession Prepare(ProjectFile project, UserCodeCompileResult compiled)
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
            services = GameSession.Create(GameServices.ForUserCode(compiled.Success ? compiled : null));
            try
            {
                scene = new SceneSerializer(registry).Deserialize(File.ReadAllText(project.StartupScenePath), services.Factory);
            }
            catch (Exception error) when (!compiled.Success)
            {
                throw new InvalidDataException("C#のコンパイルに失敗したため起動シーンを開けません。\n"
                    + string.Join(Environment.NewLine, compiled.Diagnostics.Where(d => d.IsError)
                        .Select(UserCodeCompiler.FormatDiagnostic)), error);
            }
            components.Adopt(compiled.Success ? compiled : null);
            return new(project, components, scene, services);
        }
        catch (Exception error)
        {
            var errors = new List<Exception> { error };
            try
            {
                if (scene is not null) ComponentAssets.DisposeComponents(scene.Objects.SelectMany(item => item.Components));
            }
            catch (Exception cleanup) { errors.Add(cleanup); }
            try { services?.Dispose(); }
            catch (Exception cleanup) { errors.Add(cleanup); }
            finally
            {
                components.Dispose();
                ProjectComponents.Release(compiled);
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
            services = GameSession.Create(GameServices.ForProject(components));
            return new(project, components, scene, services);
        }
        catch
        {
            try { services?.Dispose(); }
            finally { components.Dispose(); }
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed || _ownershipTransferred) return;
        _disposed = true;
        var errors = new List<Exception>();
        try { ComponentAssets.DisposeComponents(Scene.Objects.SelectMany(item => item.Components)); }
        catch (Exception error) { errors.Add(error); }
        try { EditServices.Dispose(); }
        catch (Exception error) { errors.Add(error); }
        finally { Components.Dispose(); }
        if (errors.Count != 0) throw new AggregateException("Project cleanup failed.", errors);
    }
}