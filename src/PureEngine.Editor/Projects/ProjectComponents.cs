using PureEngine.Core;

namespace PureEngine.Editor;

/// <summary>
/// Per-project owner of type registrations and loaded code (A4).
/// Keeps the registry, custom type list with source mapping, adopted compilation result, and loaded code (ALC) in one place,
/// and owns replacement on reload plus release requests on shutdown.
/// Never touches owners of other projects and keeps the old state on failure.
/// Shutdown order: dispose editing scene components, then editing services, then request code release (Unload).
/// Unload is only a request; the code survives until GC while references remain. The owner keeps no references to old types, assemblies, or results.
/// Registers built-in types (sample.*) into each owner's registry on project creation and keeps them across user.* replacement.
/// Save compatibility: typeId and .pureengine/types.json formats stay unchanged. Scene save and restore take this registry explicitly.
/// A2 (reload extraction) and A3 (backgrounding) use this owner's CreateCandidateRegistry, Adopt, and Dispose as entry points.
/// </summary>
public sealed class ProjectComponents : IDisposable
{
    private readonly Lock _sync = new();
    private UserCodeCompileResult? _userCode;
    private readonly Dictionary<string, IReadOnlyList<Type>> _userFileTypes
        = [with(StringComparer.OrdinalIgnoreCase)];
    private bool _disposed;

    /// <summary>Registry dedicated to this project. Includes built-ins plus custom (user.*) types. Keeps the instance and replaces only the contents.</summary>
    public ComponentRegistry Registry { get; } = new();

    public ProjectComponents() => ComponentAssets.RegisterBuiltins(Registry);

    /// <summary>All registered types in this project (built-in plus custom). Used to decide attachability.</summary>
    public IReadOnlyList<Type> Types => Registry.Types;

    /// <summary>Current list of custom C# types in this project. Empty on failure.</summary>
    public IReadOnlyList<Type> UserTypes
    {
        get { lock (_sync) return _userCode?.AttachableTypes ?? []; }
    }

    /// <summary>Directly marked custom types, including invalid declarations for Create Data Asset diagnostics. Empty on failure.</summary>
    public IReadOnlyList<Type> DataAssetTypes
    {
        get { lock (_sync) return _userCode?.DataAssetTypes ?? []; }
    }

    public IReadOnlyDictionary<string, IReadOnlyList<Type>> UserFileTypes
    {
        get { lock (_sync) return new Dictionary<string, IReadOnlyList<Type>>(_userFileTypes, StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>Adopted compilation result. Entry point where A2 and A3 check adoption and release state. Never hold or dispose externally.</summary>
    internal UserCodeCompileResult? ActiveUserCode
    {
        get { lock (_sync) return _userCode; }
    }

    /// <summary>Attachable types in the specified C# file. Empty for helper-only files.</summary>
    public IReadOnlyList<Type> GetTypesForFile(string? fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return [];
        lock (_sync)
        {
            var key = Path.GetFullPath(fullPath!);
            return _userFileTypes.TryGetValue(key, out var types) ? types : [];
        }
    }

    public bool CanAttach(SceneObject? target, Type? type) =>
        ComponentAssets.CanAttach(Registry, target, type);

    public bool TryAttach(SceneObject? target, Type? type, Func<Type, object>? factory = null) =>
        ComponentAssets.TryAttach(Registry, target, type, factory);

    /// <summary>
    /// Builds a validation candidate from the current non-user.* registrations plus a new compilation result. Never modifies the owner.
    /// Open and reload try scene restore and migration with this candidate before calling Adopt.
    /// </summary>
    public ComponentRegistry CreateCandidateRegistry(UserCodeCompileResult? result) =>
        ComponentAssets.CreateCandidateRegistry(Registry, result);

    /// <summary>
    /// Call on successful compilation. Validates the candidate before adoption and removes only the previous healthy user.* registrations.
    /// Keeps the same registry instance so existing scene serializers keep working.
    /// Keeps the old registrations and scene on failure; the caller releases the candidate ALC.
    /// </summary>
    public void Adopt(UserCodeCompileResult? result) => Release(Exchange(result));

    // The caller releases old code after its components and services have terminated.
    internal UserCodeCompileResult? Exchange(UserCodeCompileResult? result)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (result is { Success: false }) throw new ArgumentException("Cannot publish failed compilation.", nameof(result));
        _ = CreateCandidateRegistry(result); // Validate before removing any current registrations.
        if (result is not null) UserCodeIdentity.Save(result);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var previous = _userCode;
            // Remove only the previous user.* registrations. Keep sample.* and test registrations (checks.*).
            foreach (var id in Registry.Ids.Where(id => id.StartsWith("user.", StringComparison.Ordinal)).ToArray())
            {
                var type = Registry.GetType(id);
                Registry.Unregister(type);
            }
            _userFileTypes.Clear();
            _userCode = null;
            // Exchange returns the old code. The coordinator unloads it after releasing old components and services.
            foreach (var type in result?.AttachableTypes ?? [])
            {
                var id = result!.GetTypeId(type);
                Registry.RegisterType(type, id);
            }
            if (result is not null)
                foreach (var (path, types) in result.FileTypes)
                    _userFileTypes[Path.GetFullPath(path)] = types;
            _userCode = result;
            return previous;
        }
    }

    internal static void Release(UserCodeCompileResult? result)
    {
        try { result?.LoadContext?.Unload(); } catch { }
    }

    public void Clear() => Adopt(null);

    /// <summary>
    /// Releases on shutdown. The caller disposes the editing scene components and services first.
    /// Removes user.* registrations and requests ALC unload. Built-in registrations remain, but the owner itself is disposed.
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            var previous = _userCode;
            foreach (var id in Registry.Ids.Where(id => id.StartsWith("user.", StringComparison.Ordinal)).ToArray())
            {
                try { Registry.Unregister(Registry.GetType(id)); } catch { }
            }
            _userFileTypes.Clear();
            _userCode = null;
            if (previous?.LoadContext is not null)
            {
                try { previous.LoadContext.Unload(); } catch { }
            }
        }
    }
}
