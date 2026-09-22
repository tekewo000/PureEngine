using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace PureEngine.Editor;

/// <summary>
/// プロジェクト側のゲーム用サービス登録口。ユーザーコード内の
/// <c>public static void ConfigureGameServices(IServiceCollection services)</c> を1つだけ探して適用する。
/// 登録がない既存プロジェクトは従来どおり組み込み登録だけで動く。
/// 曖昧・不正な登録口は理由付きで報告し、採用しない。
/// Core は引き続き Func(Type, object) だけを受け、MS DI に依存しない。
/// </summary>
public static class ProjectGameServices
{
    public const string RegistrarName = "ConfigureGameServices";

    /// <summary>
    /// ユーザーコードの登録処理を services へ適用する。登録がなければ何もしない。
    /// 曖昧・不正・登録中の例外は InvalidOperationException として報告する。
    /// </summary>
    public static void Apply(UserCodeCompileResult? userCode, IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var registrar = FindRegistrar(userCode);
        if (registrar is null) return;
        try
        {
            registrar.Invoke(null, [services]);
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            throw new InvalidOperationException(
                $"Failed to apply project service registration {registrar.DeclaringType?.FullName}.{registrar.Name}: {error.InnerException.GetBaseException().Message}",
                error.InnerException);
        }
        catch (Exception error) when (error is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Failed to apply project service registration {registrar.DeclaringType?.FullName}.{registrar.Name}: {error.GetBaseException().Message}",
                error);
        }
    }

    /// <summary>
    /// 登録メソッドを探す。なければ null。曖昧・不正な場合は理由付きで投げる。
    /// 成功したコンパイル結果の LoadedAssembly がない場合（ソースなし等）も null。
    /// </summary>
    public static MethodInfo? FindRegistrar(UserCodeCompileResult? userCode)
    {
        if (userCode is null) return null;
        if (!userCode.Success)
            throw new InvalidOperationException("Cannot apply service registration from a failed compilation.");
        var assembly = userCode.LoadedAssembly;
        if (assembly is null) return null;

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException error)
        {
            throw new InvalidOperationException(
                $"Failed to get project type list: {error.GetBaseException().Message}", error);
        }

        var named = new List<MethodInfo>();
        foreach (var type in types)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            foreach (var method in methods)
            {
                if (method.Name == RegistrarName)
                    named.Add(method);
            }
        }

        if (named.Count == 0) return null;

        static bool IsValid(MethodInfo method) =>
            method.IsStatic && method.IsPublic
            && method.ReturnType == typeof(void)
            && method.GetParameters() is { Length: 1 } parameters
            && parameters[0].ParameterType == typeof(IServiceCollection)
            && !method.ContainsGenericParameters
            && !method.IsDefined(typeof(System.Runtime.CompilerServices.AsyncStateMachineAttribute), false)
            && method.DeclaringType is { ContainsGenericParameters: false };

        var valid = named.Where(IsValid).ToArray();
        if (valid.Length == 1 && named.Count == 1) return valid[0];

        if (valid.Length > 1)
        {
            var owners = string.Join(", ", valid.Select(m => m.DeclaringType?.FullName ?? "(unknown)"));
            throw new InvalidOperationException(
                $"Multiple project service registrations {RegistrarName} found ({owners}). Define only one per project.");
        }

        // valid が1つでも同名が複数あれば曖昧として扱う。
        if (valid.Length == 1)
        {
            var owners = string.Join(", ", named.Select(m =>
                $"{m.DeclaringType?.FullName ?? "(unknown)"}{SignatureOf(m)}"));
            throw new InvalidOperationException(
                $"Ambiguous project service registration {RegistrarName} definition. Keep only one correct definition ({owners}). " +
                $"Expected: public static void {RegistrarName}(IServiceCollection services).");
        }

        // 同名はあるが正しい定義がない。不正として期待形式と実際を報告する。
        var found = string.Join(", ", named.Select(m =>
            $"{m.DeclaringType?.FullName ?? "(unknown)"}{SignatureOf(m)}"));
        throw new InvalidOperationException(
            $"Invalid project service registration {RegistrarName} format ({found}). " +
            $"Expected: public static void {RegistrarName}(IServiceCollection services).");
    }

    private static string SignatureOf(MethodInfo method)
    {
        var visibility = method.IsPublic ? "public" : "non-public";
        var staticality = method.IsStatic ? "static" : "instance";
        var parameters = string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name));
        return $" [{visibility} {staticality} {method.ReturnType.Name}({parameters})]";
    }
}
