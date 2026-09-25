using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PureEngine.Core;

namespace PureEngine.Editor;

/// <summary>
/// Project-scoped incremental state for user code. Reuses unchanged syntax trees and metadata references
/// across saves and skips compilation when inputs are unchanged. Keeps no loaded assemblies or scenes.
/// </summary>
public sealed class UserCodeIncrementalCompiler : IDisposable
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, SourceEntry> _sources = [with(StringComparer.OrdinalIgnoreCase)];
    private readonly Dictionary<string, CachedReference> _referenceCache = [with(StringComparer.OrdinalIgnoreCase)];
    private readonly HashSet<string> _referencePaths = [with(StringComparer.OrdinalIgnoreCase)];
    private CSharpCompilation? _compilation;
    private List<MetadataReference> _references = [];
    private bool _hasLastResult;
    private bool _lastSuccess;
    private List<UserCodeDiagnostic> _lastDiagnostics = [];
    private bool _disposed;

    /// <summary>The project root this cache targets.</summary>
    public string ProjectRoot { get; }

    /// <summary>Total syntax trees parsed since creation. Used to verify reuse.</summary>
    public int TotalParses { get; private set; }

    /// <summary>Total metadata references created since creation. Used to verify reuse.</summary>
    public int TotalReferenceCreations { get; private set; }

    /// <summary>Total compilations emitted since creation. Unchanged inputs do not emit.</summary>
    public int TotalEmits { get; private set; }

    private sealed record SourceEntry(string Content, SyntaxTree Tree);

    private sealed record CachedReference(MetadataReference Reference, DateTime LastWriteUtc, long Length);

    public UserCodeIncrementalCompiler(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ProjectRoot = Path.GetFullPath(projectRoot);
    }

    /// <summary>Compiles the project, reusing unchanged inputs. Skips work and returns an unchanged result when inputs match the last commit.</summary>
    public UserCodeCompileResult CompileProject(CancellationToken cancellationToken = default)
    {
        var result = Compile(cancellationToken);
        if (!result.Success || result.Unchanged) return result;
        try { UserCodeIdentity.Resolve(ProjectRoot, result); }
        catch (Exception error)
        {
            UserCodeCompileTracker.Release(result);
            return new UserCodeCompileResult
            {
                Success = false,
                SourceFiles = result.SourceFiles,
                Diagnostics = [new UserCodeDiagnostic("", 0, 0, "PE-IDENTITY", error.Message, true)],
            };
        }
        return result;
    }

    /// <summary>Roslyn-only compilation with input reuse. Identity resolution is handled by <see cref="CompileProject"/>.</summary>
    public UserCodeCompileResult Compile(CancellationToken cancellationToken = default)
    {
        Dictionary<string, string> newContents;
        IReadOnlyList<string> files;
        List<UserCodeDiagnostic> readDiagnostics;
        lock (_sync) ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            files = UserCodeCompiler.ListSourceFiles(ProjectRoot);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Cannot read C# folder: {ProjectRoot}", error);
        }
        (newContents, readDiagnostics) = ReadSources(files, cancellationToken);
        if (readDiagnostics.Any(d => d.IsError))
        {
            return new UserCodeCompileResult { Success = false, Diagnostics = readDiagnostics, SourceFiles = files };
        }
        cancellationToken.ThrowIfCancellationRequested();
        var (references, referencePaths, referencesChanged, referenceCreations) = BuildIncrementalReferences(cancellationToken);
        var filesUnchanged = IsSourcesUnchanged(newContents);
        var inputsUnchanged = filesUnchanged && !referencesChanged && HasLastResult();
        if (inputsUnchanged)
        {
            var (success, previousDiagnostics) = GetLastResult();
            return new UserCodeCompileResult
            {
                Success = success,
                Unchanged = true,
                Diagnostics = previousDiagnostics,
                SourceFiles = files,
            };
        }
        var (trees, newEntries, parseCount) = BuildIncrementalTrees(newContents, cancellationToken);
        lock (_sync)
        {
            TotalParses += parseCount;
            TotalReferenceCreations += referenceCreations;
        }
        var compilation = BuildIncrementalCompilation(trees, newEntries, references, cancellationToken);
        var diagnostics = UserCodeCompiler.CollectDiagnostics(compilation.GetDiagnostics(cancellationToken), files);
        if (diagnostics.Any(d => d.IsError))
        {
            Commit(newEntries, compilation, references, referencePaths, success: false, diagnostics);
            return new UserCodeCompileResult { Success = false, Diagnostics = diagnostics, SourceFiles = files };
        }
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream, cancellationToken: cancellationToken);
        var emitErrors = UserCodeCompiler.CollectDiagnostics(
            emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error), files);
        diagnostics.AddRange(emitErrors);
        if (!emit.Success)
        {
            Commit(newEntries, compilation, references, referencePaths, success: false, diagnostics);
            return new UserCodeCompileResult { Success = false, Diagnostics = diagnostics, SourceFiles = files };
        }
        lock (_sync) TotalEmits++;
        var bytes = stream.ToArray();
        UserCodeLoadContext context;
        Assembly assembly;
        try
        {
            assembly = UserCodeCompiler.LoadUserCode(bytes, out context);
        }
        catch (Exception error)
        {
            var list = diagnostics.ToList();
            list.Add(new UserCodeDiagnostic("", 0, 0, "PE-LOAD", $"Cannot load assembly: {error.GetBaseException().Message}", true));
            Commit(newEntries, compilation, references, referencePaths, success: false, list);
            return new UserCodeCompileResult { Success = false, Diagnostics = list, SourceFiles = files };
        }
        Type[] allTypes;
        try
        {
            allTypes = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException error)
        {
            try { context.Unload(); } catch { }
            var list = diagnostics.ToList();
            foreach (var loaderError in error.LoaderExceptions.Where(e => e is not null))
                list.Add(new UserCodeDiagnostic("", 0, 0, "PE-TYPES", loaderError!.Message, true));
            Commit(newEntries, compilation, references, referencePaths, success: false, list);
            return new UserCodeCompileResult { Success = false, Diagnostics = list, SourceFiles = files };
        }
        var attachable = allTypes.Where(UserCodeCompiler.IsAttachable).OrderBy(t => t.FullName, StringComparer.Ordinal).ToArray();
        var fileTypes = UserCodeCompiler.BuildFileMap(compilation, attachable);
        var dataAssets = allTypes.Where(type => type.IsDefined(typeof(DataAssetAttribute), inherit: false)).ToArray();
        var warnings = diagnostics.Where(d => !d.IsError).ToList();
        var result = new UserCodeCompileResult
        {
            Success = true,
            Diagnostics = warnings,
            SourceFiles = files,
            AssemblyBytes = bytes,
            LoadedAssembly = assembly,
            LoadContext = context,
            AttachableTypes = attachable,
            DataAssetTypes = dataAssets,
            FileTypes = fileTypes,
        };
        Commit(newEntries, compilation, references, referencePaths, success: true, warnings);
        return result;
    }

    private static (Dictionary<string, string> Contents, List<UserCodeDiagnostic> Diagnostics) ReadSources(
        IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        var contents = new Dictionary<string, string>(files.Count, StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<UserCodeDiagnostic>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string text;
            try
            {
                try
                {
                    text = File.ReadAllText(file);
                }
                catch (IOException)
                {
                    Thread.Sleep(50);
                    cancellationToken.ThrowIfCancellationRequested();
                    text = File.ReadAllText(file);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                diagnostics.Add(new UserCodeDiagnostic(file, 0, 0, "PE-READ", $"Cannot read file: {error.GetBaseException().Message}", true));
                continue;
            }
            contents[Path.GetFullPath(file)] = text;
        }
        return (contents, diagnostics);
    }

    private bool IsSourcesUnchanged(Dictionary<string, string> newContents)
    {
        lock (_sync)
        {
            if (newContents.Count != _sources.Count) return false;
            foreach (var (path, content) in newContents)
            {
                if (!_sources.TryGetValue(path, out var entry)) return false;
                if (!string.Equals(entry.Content, content, StringComparison.Ordinal)) return false;
            }
            return true;
        }
    }

    private bool HasLastResult()
    {
        lock (_sync) return _hasLastResult;
    }

    private (bool Success, IReadOnlyList<UserCodeDiagnostic> Diagnostics) GetLastResult()
    {
        lock (_sync) return (_lastSuccess, _lastDiagnostics.ToArray());
    }

    private (List<MetadataReference> References, HashSet<string> Paths, bool Changed, int Creations) BuildIncrementalReferences(
        CancellationToken cancellationToken)
    {
        var candidatePaths = UserCodeCompiler.GetCandidateReferencePaths();
        var paths = new HashSet<string>(candidatePaths, StringComparer.OrdinalIgnoreCase);
        var references = new List<MetadataReference>(paths.Count);
        var changed = false;
        var creations = 0;
        lock (_sync)
        {
            if (!_hasLastResult || !_referencePaths.SetEquals(paths)) changed = true;
        }
        foreach (var path in candidatePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateTime lastWrite;
            long length;
            try
            {
                lastWrite = File.GetLastWriteTimeUtc(path);
                length = new FileInfo(path).Length;
            }
            catch { continue; }
            MetadataReference? cached = null;
            lock (_sync)
            {
                if (_referenceCache.TryGetValue(path, out var entry)
                    && entry.LastWriteUtc == lastWrite && entry.Length == length)
                {
                    cached = entry.Reference;
                }
            }
            if (cached is not null)
            {
                references.Add(cached);
                continue;
            }
            MetadataReference created;
            try { created = MetadataReference.CreateFromFile(path); }
            catch { continue; }
            lock (_sync) _referenceCache[path] = new CachedReference(created, lastWrite, length);
            references.Add(created);
            creations++;
            changed = true;
        }
        lock (_sync)
        {
            if (references.Count != _references.Count) changed = true;
            else
            {
                for (var i = 0; i < references.Count; i++)
                {
                    if (!ReferenceEquals(references[i], _references[i]))
                    {
                        changed = true;
                        break;
                    }
                }
            }
        }
        return (references, paths, changed, creations);
    }

    private (List<SyntaxTree> Trees, Dictionary<string, SourceEntry> Entries, int ParseCount) BuildIncrementalTrees(
        Dictionary<string, string> newContents, CancellationToken cancellationToken)
    {
        var trees = new List<SyntaxTree>(newContents.Count);
        var entries = new Dictionary<string, SourceEntry>(newContents.Count, StringComparer.OrdinalIgnoreCase);
        var parses = 0;
        var orderedPaths = new List<string>(newContents.Keys);
        orderedPaths.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (var path in orderedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = newContents[path];
            SourceEntry? reused = null;
            lock (_sync)
            {
                if (_sources.TryGetValue(path, out var old)
                    && string.Equals(old.Content, content, StringComparison.Ordinal))
                {
                    reused = old;
                }
            }
            if (reused is not null)
            {
                trees.Add(reused.Tree);
                entries[path] = reused;
            }
            else
            {
                var tree = CSharpSyntaxTree.ParseText(content, UserCodeCompiler.ParseOptions, path, encoding: null, cancellationToken);
                var entry = new SourceEntry(content, tree);
                trees.Add(tree);
                entries[path] = entry;
                parses++;
            }
        }
        return (trees, entries, parses);
    }

    private CSharpCompilation BuildIncrementalCompilation(
        List<SyntaxTree> trees, Dictionary<string, SourceEntry> newEntries, List<MetadataReference> references, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_compilation is null)
            {
                var created = CSharpCompilation.Create("PureEngine.UserCode", trees, references, UserCodeCompiler.CompilationOptions);
                return created;
            }
        }
        CSharpCompilation current;
        Dictionary<string, SourceEntry> oldEntries;
        lock (_sync)
        {
            current = _compilation!;
            oldEntries = new Dictionary<string, SourceEntry>(_sources, StringComparer.OrdinalIgnoreCase);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var withReferences = current;
        lock (_sync)
        {
            if (!ReferenceEquals(_references, references))
            {
                var same = _references.Count == references.Count;
                if (same)
                {
                    for (var i = 0; i < _references.Count; i++)
                    {
                        if (!ReferenceEquals(_references[i], references[i]))
                        {
                            same = false;
                            break;
                        }
                    }
                }
                if (!same) withReferences = current.WithReferences(references);
            }
        }
        var updated = withReferences;
        foreach (var (path, old) in oldEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!newEntries.ContainsKey(path)) updated = updated.RemoveSyntaxTrees(old.Tree);
        }
        foreach (var (path, entry) in newEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!oldEntries.TryGetValue(path, out var old)) updated = updated.AddSyntaxTrees(entry.Tree);
            else if (!ReferenceEquals(old.Tree, entry.Tree)) updated = updated.ReplaceSyntaxTree(old.Tree, entry.Tree);
        }
        return updated;
    }

    private void Commit(
        Dictionary<string, SourceEntry> entries, CSharpCompilation compilation, List<MetadataReference> references,
        HashSet<string> referencePaths, bool success, List<UserCodeDiagnostic> diagnostics)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _sources.Clear();
            foreach (var (path, entry) in entries) _sources[path] = entry;
            _compilation = compilation;
            _references = references;
            _referencePaths.Clear();
            foreach (var path in referencePaths) _referencePaths.Add(path);
            var stale = new List<string>();
            foreach (var path in _referenceCache.Keys)
            {
                if (!referencePaths.Contains(path)) stale.Add(path);
            }
            foreach (var path in stale) _referenceCache.Remove(path);
            _hasLastResult = true;
            _lastSuccess = success;
            _lastDiagnostics = [.. diagnostics];
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _sources.Clear();
            _referenceCache.Clear();
            _referencePaths.Clear();
            _references = [];
            _compilation = null;
            _lastDiagnostics = [];
            _hasLastResult = false;
        }
    }
}
