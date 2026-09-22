using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace PureEngine.Editor;

/// <summary>プロジェクト内の自作C#ファイルのコンパイル結果の1件。Console表示用にファイル・行・内容を持つ。</summary>
public sealed record UserCodeDiagnostic(
    string FilePath,
    int Line,
    int Column,
    string Id,
    string Message,
    bool IsError);

/// <summary>プロジェクト内の自作C#ファイルのコンパイル結果。</summary>
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
    /// <summary>アタッチ対象の型一覧。失敗時は空。</summary>
    public IReadOnlyList<Type> AttachableTypes { get; internal set; } = [];
    /// <summary>フルパス→そのファイルに含まれるアタッチ対象の型。フォルダ構成のまま表示・D&D用。</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Type>> FileTypes { get; internal set; }
        = new Dictionary<string, IReadOnlyList<Type>>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Collectible load context for project user code. Unloaded on every reload.</summary>
internal sealed class UserCodeLoadContext() : AssemblyLoadContext("PureEngine.UserCode", isCollectible: true)
{
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // User assembly only; dependencies resolve from the default context.
        return null;
    }
}

/// <summary>
/// プロジェクト内の自作C#ファイルをコンパイルし、そのプロジェクトで使えるようにする。
/// 任意のフォルダに置いた .cs を対象にし、専用のComponentsフォルダへの配置は要求しない。
/// </summary>
public static class UserCodeCompiler
{
    /// <summary>
    /// アタッチ対象の識別方法：publicな具象クラス（トップレベルまたは入れ子のpublic）。
    /// abstract・static・generic定義・interface・enum・struct・delegate・internal/private・コンパイラ生成は補助クラスとして扱い、アタッチ対象にしない。
    /// 属性の有無は問わない（属性なしのデータだけのクラスもアタッチできる）。
    /// </summary>
    public static bool IsAttachable(Type type)
    {
        if (type is null) return false;
        if (!type.IsClass) return false;
        if (type.IsAbstract) return false; // abstractとstatic（abstract+sealed）を除外
        if (type.IsSealed && type.IsAbstract) return false;
        if (type.ContainsGenericParameters) return false;
        if (typeof(Delegate).IsAssignableFrom(type)) return false;
        if (type.Name.Contains('<', StringComparison.Ordinal)) return false; // compiler-generated
        if (!type.IsVisible) return false;
        return true;
    }

    /// <summary>初回登録用のID。Project内ではUserCodeIdentityが改名後も元のIDを保持する。</summary>
    public static string TypeIdFor(Type type) => "user." + (type.FullName ?? type.Name);

    /// <summary>プロジェクト直下の .cs を再帰列挙する。bin/obj/.git/.vs は除外する。</summary>
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
                string name = Path.GetFileName(entry);
                if (name.StartsWith('.') && !name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    // .pure-project-* 等の一時フォルダや隠しファイルを避ける。ただし .cs は拾う。
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
                // 保存途中のファイルでロックされている場合は次回に回すため失敗扱いにせず読み直す。
                // ここでは1回だけ再試行する。
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
            // Hiddenは表示しない。Warningは許可し、Errorのみ失敗とする。
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
            if (diagnostic.Severity != DiagnosticSeverity.Error) continue; // WarningはGetDiagnostics側で既に収集済み
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
            // Emit由来のエラーは失敗扱い。
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

        return new UserCodeCompileResult
        {
            Success = true,
            Diagnostics = diagnostics.Where(d => !d.IsError).ToArray(),
            SourceFiles = files,
            AssemblyBytes = bytes,
            LoadedAssembly = assembly,
            LoadContext = context,
            AttachableTypes = attachable,
            FileTypes = fileTypes,
        };
    }

    /// <summary>
    /// 1ファイルに複数クラスがある場合の扱い：そのファイルに含まれるアタッチ対象の全型をそのファイルにひも付ける。
    /// ドラッグ＆ドロップ時はそのファイルの未アタッチ分をすべて付ける。1ファイル1クラスを推奨するが、複数でも動作する。
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<Type>> BuildFileMap(
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
                if (!byFullName.TryGetValue(full, out var match)) continue; // 補助クラスは対象外
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
        INamedTypeSymbol? current = symbol;
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

    /// <summary>診断をConsole表示用の1行にする。ファイル名・行番号・内容を含む。</summary>
    public static string FormatDiagnostic(UserCodeDiagnostic diagnostic)
    {
        var file = string.IsNullOrEmpty(diagnostic.FilePath) ? "(unknown)" : Path.GetFileName(diagnostic.FilePath);
        var location = diagnostic.Line > 0 ? $"{file}({diagnostic.Line},{diagnostic.Column})" : file;
        return $"{location} {diagnostic.Id}: {diagnostic.Message}";
    }
}
