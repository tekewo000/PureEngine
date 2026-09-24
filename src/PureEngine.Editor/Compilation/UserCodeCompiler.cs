using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PureEngine.Core;

namespace PureEngine.Editor;

/// <summary>A single entry in the compilation result for project-owned C# files. Holds file, line, and content for console display.</summary>
public sealed record UserCodeDiagnostic(
    string FilePath,
    int Line,
    int Column,
    string Id,
    string Message,
    bool IsError);

/// <summary>Compilation result for project-owned C# files.</summary>
public sealed class UserCodeCompileResult
{
    internal Dictionary<Type, string> TypeIds { get; } = [];
    internal string? IdentityPath { get; set; }
    internal string? IdentityContent { get; set; }
    internal string GetTypeId(Type type) => TypeIds.GetValueOrDefault(type) ?? UserCodeCompiler.TypeIdFor(type);
    public bool Success { get; init; }
    public IReadOnlyList<UserCodeDiagnostic> Diagnostics { get; init; } = [];
    public IReadOnlyList<string> SourceFiles { get; init; } = [];
    internal byte[]? AssemblyBytes { get; init; }
    internal Assembly? LoadedAssembly { get; set; }
    internal AssemblyLoadContext? LoadContext { get; set; }
    /// <summary>Attachable types. Empty on failure.</summary>
    public IReadOnlyList<Type> AttachableTypes { get; internal set; } = [];
    /// <summary>All directly marked data asset types, including invalid declarations for editor diagnostics. Empty on failure.</summary>
    public IReadOnlyList<Type> DataAssetTypes { get; internal set; } = [];
    /// <summary>Full path to the attachable types in that file. Keeps the folder structure for display and drag-and-drop.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Type>> FileTypes { get; internal set; }
        = new Dictionary<string, IReadOnlyList<Type>>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Collectible load context for project user code. Unloaded on every reload.</summary>
internal sealed class UserCodeLoadContext() : AssemblyLoadContext("PureEngine.UserCode", isCollectible: true)
{
    protected override Assembly? Load(AssemblyName assemblyName) =>
        // User assembly only; dependencies resolve from the default context.
        null;
}

/// <summary>
/// Compiles project-owned C# files for use in that project.
/// Targets .cs files in any folder without requiring a dedicated Components folder.
/// </summary>
public static class UserCodeCompiler
{
    /// <summary>
    /// How attachable types are identified: public concrete classes (top-level or nested public).
    /// Treats abstract, static, generic definitions, interfaces, enums, structs, delegates, internal/private, and compiler-generated types as helper classes and excludes them.
    /// Ignores attributes (data-only classes without attributes are also attachable).
    /// </summary>
    public static bool IsAttachable(Type type)
    {
        if (type is null) return false;
        if (!type.IsClass) return false;
        if (type.IsAbstract) return false; // Exclude abstract and static (abstract+sealed)
        if (type.IsSealed && type.IsAbstract) return false;
        if (type.ContainsGenericParameters) return false;
        if (typeof(Delegate).IsAssignableFrom(type)) return false;
        if (type.Name.Contains('<', StringComparison.Ordinal)) return false; // compiler-generated
        if (!type.IsVisible) return false;
        return true;
    }

    /// <summary>ID for the first registration. Within the project, UserCodeIdentity keeps the original ID after renames.</summary>
    public static string TypeIdFor(Type type) => "user." + (type.FullName ?? type.Name);

    /// <summary>Recursively enumerates .cs files under the project root. Excludes bin/obj/.git/.vs.</summary>
    public static IReadOnlyList<string> ListSourceFiles(string rootDirectory)
    {
        if (!Directory.Exists(rootDirectory)) return [];
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", ".vs", ".idea" };
        var files = new List<string>();
        var stack = new Stack<string>();
        stack.Push(Path.GetFullPath(rootDirectory));
        while (stack.Count > 0)
        {
            var directory = stack.Pop();
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Cannot read C# folder: {directory}", error);
            }
            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                if (name.StartsWith('.') && !name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    // Skip temporary folders such as .pure-project-* and hidden files. Still pick up .cs files.
                    if (Directory.Exists(entry)) continue;
                }
                if (Directory.Exists(entry))
                {
                    if (ignored.Contains(name)) continue;
                    try
                    {
                        if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch { continue; }
                    stack.Push(entry);
                }
                else if (entry.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    files.Add(entry);
                }
            }
        }
        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    public static UserCodeCompileResult CompileProject(string rootDirectory)
    {
        var files = ListSourceFiles(rootDirectory);
        var result = CompileFiles(files);
        if (!result.Success) return result;
        try { UserCodeIdentity.Resolve(rootDirectory, result); }
        catch (Exception error)
        {
            result.LoadContext?.Unload();
            return new UserCodeCompileResult
            {
                Success = false, SourceFiles = files,
                Diagnostics = [new UserCodeDiagnostic("", 0, 0, "PE-IDENTITY", error.Message, true)],
            };
        }
        return result;
    }

    public static UserCodeCompileResult CompileFiles(IReadOnlyList<string> files)
    {
        files ??= [];
        if (files.Count == 0)
        {
            return new UserCodeCompileResult
            {
                Success = true,
                Diagnostics = [],
                SourceFiles = [],
                AttachableTypes = [],
                DataAssetTypes = [],
                FileTypes = new Dictionary<string, IReadOnlyList<Type>>(StringComparer.OrdinalIgnoreCase),
            };
        }

        var trees = new List<SyntaxTree>(files.Count);
        var readDiagnostics = new List<UserCodeDiagnostic>();
        foreach (var file in files)
        {
            string text;
            try
            {
                // When a file is locked mid-save, reread instead of failing so the next attempt can handle it.
                // Retry only once here.
                try
                {
                    text = File.ReadAllText(file);
                }
                catch (IOException)
                {
                    Thread.Sleep(50);
                    text = File.ReadAllText(file);
                }
            }
            catch (Exception error)
            {
                readDiagnostics.Add(new UserCodeDiagnostic(file, 0, 0, "PE-READ", $"Cannot read file: {error.GetBaseException().Message}", true));
                continue;
            }
            // Roslyn 5.x Preview corresponds to the C# 15 preview. The stable NuGet package (5.9.0) has
            // no LanguageVersion.CSharp15 yet, so follow the latest via Preview instead of an explicit value.
            trees.Add(CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Preview), file));
        }
        if (readDiagnostics.Any(d => d.IsError))
        {
            return new UserCodeCompileResult
            {
                Success = false,
                Diagnostics = readDiagnostics,
                SourceFiles = files,
            };
        }

        var references = BuildReferences();
        var compilation = CSharpCompilation.Create(
            "PureEngine.UserCode." + Guid.NewGuid().ToString("N"),
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithOverflowChecks(true)
                .WithOptimizationLevel(OptimizationLevel.Debug));

        var diagnostics = new List<UserCodeDiagnostic>();
        foreach (var diagnostic in compilation.GetDiagnostics())
        {
            // Hide hidden diagnostics. Allow warnings and fail only on errors.
            if (diagnostic.Severity == DiagnosticSeverity.Hidden) continue;
            var line = 0;
            var column = 0;
            var path = files is [var first, ..] ? first : "";
            if (diagnostic.Location.IsInSource && diagnostic.Location.SourceTree is not null)
            {
                path = diagnostic.Location.SourceTree.FilePath;
                var span = diagnostic.Location.GetLineSpan();
                line = span.StartLinePosition.Line + 1;
                column = span.StartLinePosition.Character + 1;
            }
            diagnostics.Add(new UserCodeDiagnostic(path, line, column, diagnostic.Id,
                diagnostic.ToString(), diagnostic.Severity == DiagnosticSeverity.Error));
        }
        if (diagnostics.Any(d => d.IsError))
        {
            return new UserCodeCompileResult
            {
                Success = false,
                Diagnostics = diagnostics,
                SourceFiles = files,
            };
        }

        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        foreach (var diagnostic in emit.Diagnostics)
        {
            if (diagnostic.Severity == DiagnosticSeverity.Hidden) continue;
            if (diagnostic.Severity != DiagnosticSeverity.Error) continue; // Warnings were already collected via GetDiagnostics
            var line = 0;
            var column = 0;
            var path = files is [var first, ..] ? first : "";
            if (diagnostic.Location.IsInSource && diagnostic.Location.SourceTree is not null)
            {
                path = diagnostic.Location.SourceTree.FilePath;
                var span = diagnostic.Location.GetLineSpan();
                line = span.StartLinePosition.Line + 1;
                column = span.StartLinePosition.Character + 1;
            }
            // Treat emit errors as failures.
            diagnostics.Add(new UserCodeDiagnostic(path, line, column, diagnostic.Id,
                diagnostic.ToString(), true));
        }
        if (!emit.Success)
        {
            return new UserCodeCompileResult
            {
                Success = false,
                Diagnostics = diagnostics,
                SourceFiles = files,
            };
        }

        var bytes = stream.ToArray();
        var context = new UserCodeLoadContext();
        Assembly assembly;
        try
        {
            using var loadStream = new MemoryStream(bytes);
            assembly = context.LoadFromStream(loadStream);
        }
        catch (Exception error)
        {
            try { context.Unload(); } catch { }
            var list = diagnostics.ToList();
            list.Add(new UserCodeDiagnostic("", 0, 0, "PE-LOAD", $"Cannot load assembly: {error.GetBaseException().Message}", true));
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
            return new UserCodeCompileResult { Success = false, Diagnostics = list, SourceFiles = files };
        }

        var attachable = allTypes.Where(IsAttachable).OrderBy(t => t.FullName, StringComparer.Ordinal).ToArray();
        var fileTypes = BuildFileMap(compilation, attachable);
        var dataAssets = allTypes
            .Where(type => type.IsDefined(typeof(DataAssetAttribute), inherit: false))
            .ToArray();

        return new UserCodeCompileResult
        {
            Success = true,
            Diagnostics = diagnostics.Where(d => !d.IsError).ToArray(),
            SourceFiles = files,
            AssemblyBytes = bytes,
            LoadedAssembly = assembly,
            LoadContext = context,
            AttachableTypes = attachable,
            DataAssetTypes = dataAssets,
            FileTypes = fileTypes,
        };
    }

    /// <summary>
    /// Handling for multiple classes per file: maps every attachable type in the file to that file.
    /// Drag-and-drop attaches all not-yet-attached types from that file. One class per file is recommended, but multiples work.
    /// </summary>
    private static Dictionary<string, IReadOnlyList<Type>> BuildFileMap(
        CSharpCompilation compilation, Type[] attachable)
    {
        var byFullName = attachable.ToDictionary(t => t.FullName ?? t.Name, StringComparer.Ordinal);
        var map = new Dictionary<string, List<Type>>(StringComparer.OrdinalIgnoreCase);
        foreach (var tree in compilation.SyntaxTrees)
        {
            var path = Path.GetFullPath(tree.FilePath);
            var model = compilation.GetSemanticModel(tree);
            var roots = tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax>();
            foreach (var node in roots)
            {
                if (model.GetDeclaredSymbol(node) is not INamedTypeSymbol symbol) continue;
                var full = SymbolFullName(symbol);
                if (full is null) continue;
                if (!byFullName.TryGetValue(full, out var match)) continue; // Helper classes are out of scope
                if (!map.TryGetValue(path, out var list)) map[path] = list = [];
                if (!list.Contains(match)) list.Add(match);
            }
        }
        var result = new Dictionary<string, IReadOnlyList<Type>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, list) in map)
            result[path] = list.OrderBy(t => t.FullName, StringComparer.Ordinal).ToArray();
        return result;
    }

    private static string? SymbolFullName(INamedTypeSymbol symbol)
    {
        var stack = new Stack<string>();
        var current = symbol;
        while (current is not null)
        {
            stack.Push(current.Name);
            current = current.ContainingType;
        }
        var nested = string.Join("+", stack);
        var ns = symbol.ContainingNamespace?.ToDisplayString();
        if (string.IsNullOrEmpty(ns) || ns == "<global namespace>") return nested;
        return ns + "." + nested;
    }

    private static List<MetadataReference> BuildReferences()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        {
            foreach (var path in tpa.Split(Path.PathSeparator))
            {
                if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    paths.Add(path);
            }
        }
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic) continue;
            try
            {
                var location = assembly.Location;
                if (!string.IsNullOrEmpty(location) && File.Exists(location))
                    paths.Add(location);
            }
            catch { }
        }
        var references = new List<MetadataReference>(paths.Count);
        foreach (var path in paths)
        {
            try { references.Add(MetadataReference.CreateFromFile(path)); }
            catch { }
        }
        return references;
    }

    /// <summary>Formats a diagnostic as one line for console display. Includes file name, line number, and content.</summary>
    public static string FormatDiagnostic(UserCodeDiagnostic diagnostic)
    {
        var file = string.IsNullOrEmpty(diagnostic.FilePath) ? "(unknown)" : Path.GetFileName(diagnostic.FilePath);
        var location = diagnostic.Line > 0 ? $"{file}({diagnostic.Line},{diagnostic.Column})" : file;
        return $"{location} {diagnostic.Id}: {diagnostic.Message}";
    }
}
