using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PureEngine.Core;
using PureEngine.Editor;

namespace PureEngine.Runtime;

/// <summary>Loads packaged data and precompiled code without the editor, source files, or working-directory lookup.</summary>
public sealed class GamePackage : IDisposable
{
    public const string ManifestFileName = "package.json";
    public const string DataDirectoryName = "Data";
    private readonly AssemblyLoadContext? _loadContext;
    private readonly Assembly? _assembly;
    private readonly string _startupScene;
    private readonly List<PlaySession> _sessions = [];
    private bool _disposed;

    public string Name { get; }
    public string DataDirectory { get; }
    public ComponentRegistry Registry { get; }
    public ProjectAssets Assets { get; }
    public DataAssetStore DataAssets { get; }
    public PrefabCatalog Prefabs { get; }
    public LocalizationStore Localization { get; }

    private GamePackage(string root, GamePackageManifest manifest, AssemblyLoadContext? context, Assembly? assembly)
    {
        _loadContext = context;
        _assembly = assembly;
        Name = manifest.Name;
        DataDirectory = ContentPaths.Resolve(root, DataDirectoryName);
        if (!Directory.Exists(DataDirectory))
            throw new DirectoryNotFoundException("The package Data directory is missing.");
        ValidateTree(DataDirectory);
        _startupScene = ContentPaths.Resolve(DataDirectory, manifest.StartupScene);
        if (!_startupScene.EndsWith(".pure.scene.yaml", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(_startupScene))
            throw new InvalidDataException("The package startup scene is missing or has an invalid extension.");
        Registry = new ComponentRegistry();
        GameRegistration.RegisterBuiltins(Registry);
        foreach (var (id, fullName) in manifest.Types)
        {
            if (!id.StartsWith("user.", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(fullName))
                throw new InvalidDataException($"Invalid packaged type identity: {id}.");
            var type = assembly?.GetType(fullName, throwOnError: false, ignoreCase: false)
                ?? throw new InvalidDataException($"Packaged type {id} ({fullName}) is missing.");
            if (!type.IsClass || type.IsAbstract || type.ContainsGenericParameters || !type.IsVisible
                || typeof(Delegate).IsAssignableFrom(type))
                throw new InvalidDataException($"Packaged type {id} is not a public concrete class.");
            Registry.RegisterType(type, id);
        }
        _ = GameServiceRegistration.FindRegistrar(assembly);
        Assets = ProjectAssets.Scan(DataDirectory);
        RequireClean(Assets.Diagnostics);
        // Unlike the forgiving editor preview, a package cannot silently omit unreadable image files.
        foreach (var entry in Assets.Images.Values)
            _ = File.ReadAllBytes(entry.FullPath);
        DataAssets = DataAssetStore.ScanFolder(DataDirectory, Registry, out var assetDiagnostics);
        RequireClean(assetDiagnostics);
        Prefabs = PrefabCatalog.ScanFolder(DataDirectory, out var prefabDiagnostics);
        RequireClean(prefabDiagnostics);
        var (localization, localizationDiagnostics) = LocalizationStore.LoadFile(
            ContentPaths.Resolve(DataDirectory, LocalizationSerializer.FileName));
        RequireClean(localizationDiagnostics);
        Localization = localization;
    }

    public static GamePackage Open(string packageRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        var root = Path.GetFullPath(packageRoot);
        var manifestPath = ContentPaths.Resolve(root, ManifestFileName);
        var manifest = JsonSerializer.Deserialize<GamePackageManifest>(File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException("The package manifest is empty.");
        if (manifest.Version != 1 || string.IsNullOrWhiteSpace(manifest.Name)
            || string.IsNullOrWhiteSpace(manifest.StartupScene) || manifest.Types is null)
            throw new InvalidDataException("The package manifest requires version 1, Name, StartupScene, and Types.");
        AssemblyLoadContext? context = null;
        try
        {
            Assembly? assembly = null;
            if (manifest.GameAssembly is not null)
            {
                var assemblyPath = ContentPaths.Resolve(root, manifest.GameAssembly);
                if (!assemblyPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("GameAssembly must identify a packaged DLL.");
                context = new PackageLoadContext();
                // Load only the game assembly in this context. Core, Runtime and DI retain host type identity.
                using var stream = File.OpenRead(assemblyPath);
                assembly = context.LoadFromStream(stream);
            }
            return new GamePackage(root, manifest, context, assembly);
        }
        catch
        {
            context?.Unload();
            throw;
        }
    }

    /// <summary>Validates all packaged scenes, prefab templates and asset references without invoking lifecycle callbacks.</summary>
    public void Validate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateTree(DataDirectory);
        foreach (var path in Directory.EnumerateFiles(DataDirectory, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".pure.scene.yaml", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal))
            WithScene(factory => new SceneSerializer(Registry, DataAssets, Prefabs, Localization)
                .Deserialize(File.ReadAllText(path), factory), static _ => { });
        foreach (var id in Prefabs.Ids)
            WithScene(factory => new PrefabSerializer(Registry).RestoreForEditing(
                Prefabs.Find(id)!, out _, DataAssets, Prefabs, factory, Localization), static _ => { });
        foreach (var id in DataAssets.Ids)
            ValidateValue(DataAssets.Find(id, typeof(object)), [with(ReferenceEqualityComparer.Instance)]);
    }

    /// <summary>Prepares an independent run. The caller invokes Start; package disposal also stops any remaining runs.</summary>
    public PlaySession CreateSession()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PlaySession? result = null;
        try
        {
            WithScene(factory => new SceneSerializer(Registry, DataAssets, Prefabs, Localization)
                .Deserialize(File.ReadAllText(_startupScene), factory),
                scene => result = PlaySession.Prepare(scene, Registry, Configure));
            _sessions.Add(result!);
            return result!;
        }
        catch (Exception error)
        {
            try { result?.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
            throw;
        }
    }

    private void Configure(IServiceCollection services)
    {
        GameRegistration.Configure(services);
        GameServiceRegistration.Apply(_assembly, services);
        services.AddSingleton(DataAssets.Clone(Registry));
        services.AddSingleton(Prefabs);
        services.AddSingleton(Localization.Clone());
    }

    private void WithScene(Func<Func<Type, object>, Scene> restore, Action<Scene> use)
    {
        GameSession? services = null;
        Scene? scene = null;
        List<Exception> errors = [];
        try
        {
            services = GameSession.Create(Configure);
            scene = restore(services.Factory);
            if (scene.References.MissingCount != 0 || scene.References.HasLegacy)
                throw new InvalidDataException("Packaged scene or prefab contains missing IDs or legacy references.");
            HashSet<object> visited = [with(ReferenceEqualityComparer.Instance)];
            foreach (var component in scene.OwnedComponents)
            {
                _ = ComponentSchema.GetStartMethod(component.GetType());
                ValidateValue(component, visited);
            }
            use(scene);
        }
        catch (Exception error) { errors.Add(error); }
        if (scene is not null)
        {
            foreach (var component in scene.OwnedComponents.Distinct(ReferenceEqualityComparer.Instance).Reverse())
            {
                if (component is not IDisposable disposable) continue;
                try { disposable.Dispose(); }
                catch (Exception error) { errors.Add(error); }
            }
        }
        try { services?.Dispose(); }
        catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0)
            throw new AggregateException("Packaged scene preparation or cleanup failed.", errors);
    }

    private void ValidateValue(object? value, HashSet<object> visited)
    {
        if (value is null || value is string || !visited.Add(value)) return;
        if (value is Sprite sprite)
        {
            if (!Assets.Images.ContainsKey(sprite.ImageId))
                throw new InvalidDataException($"Missing packaged image ID: {sprite.ImageId:D}.");
            return;
        }
        if (value is LocalizedTextId text)
        {
            if (!text.IsEmpty && !Localization.TryGetEntry(text.Id, out _))
                throw new InvalidDataException($"Missing packaged localization ID: {text.Id:D}.");
            return;
        }
        if (value is IDictionary dictionary)
        {
            foreach (var item in dictionary.Values) ValidateValue(item, visited);
            return;
        }
        if (value is IEnumerable items)
        {
            foreach (var item in items) ValidateValue(item, visited);
            return;
        }
        foreach (var member in ComponentSchema.GetInspectorMembers(value.GetType()))
            ValidateValue(member is FieldInfo field ? field.GetValue(value) : ((PropertyInfo)member).GetValue(value), visited);
    }

    private static void RequireClean(IReadOnlyList<string> diagnostics)
    {
        if (diagnostics.Count != 0)
            throw new InvalidDataException(string.Join(Environment.NewLine, diagnostics));
    }

    private static void ValidateTree(string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            ContentPaths.Validate(root, entry);
            if (Directory.Exists(entry)) ValidateTree(entry);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        List<Exception> errors = [];
        foreach (var session in _sessions.AsEnumerable().Reverse())
        {
            try { session.Dispose(); }
            catch (Exception error) { errors.Add(error); }
        }
        _sessions.Clear();
        _loadContext?.Unload();
        if (errors.Count != 0) throw new AggregateException("Package cleanup failed.", errors);
    }

    private sealed class PackageLoadContext() : AssemblyLoadContext("PureEngine.GamePackage", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName) => null;
    }
}
