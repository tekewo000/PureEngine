using PureEngine.Core;

namespace PureEngine.Editor;

public static class ComponentAssets
{
    /// <summary>Releases editing instances without running game lifecycle callbacks.</summary>
    internal static void DisposeComponents(IEnumerable<object> components)
    {
        var errors = new List<Exception>();
        foreach (var component in components.Distinct(ReferenceEqualityComparer.Instance).Reverse())
        {
            if (component is not IDisposable disposable) continue;
            try { disposable.Dispose(); }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count != 0) throw new AggregateException("Editor components cleanup failed.", errors);
    }

    /// <summary>
    /// 組み込み型の登録方針（A4）：各プロジェクトの所有者（ProjectComponents）が生成時に
    /// 自分のRegistryへ登録する。共有のstatic登録は持たない。user.* の差し替えでは維持される。
    /// </summary>
    public static void RegisterBuiltins(ComponentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register<Samples.PlayerStats>("sample.player-stats");
        registry.Register<Samples.RoundSettings>("sample.round-settings");
        registry.Register<Samples.InjectedPlayer>("sample.injected-player");
    }

    /// <summary>
    /// 候補の検証用に、現在の非user.*登録＋新しいコンパイル結果から作る。呼び出し元の所有者は変更しない。
    /// 採用前にScene復元・移行の検証へ渡す。失敗時は旧登録を保持する。
    /// </summary>
    internal static ComponentRegistry CreateCandidateRegistry(ComponentRegistry current, UserCodeCompileResult? result)
    {
        ArgumentNullException.ThrowIfNull(current);
        var registry = new ComponentRegistry();
        foreach (var id in current.Ids.Where(id => !id.StartsWith("user.", StringComparison.Ordinal)))
            registry.RegisterType(current.GetType(id), id);
        if (result is { Success: true })
            foreach (var type in result.AttachableTypes)
                registry.RegisterType(type, result.GetTypeId(type));
        return registry;
    }

    public static bool CanAttach(ComponentRegistry registry, SceneObject? target, Type? type)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return target is not null && type is not null && registry.Types.Contains(type)
            && !target.Components.Any(component => component.GetType() == type);
    }

    public static bool TryAttach(ComponentRegistry registry, SceneObject? target, Type? type, Func<Type, object>? factory = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (!CanAttach(registry, target, type)) return false;
        // Component 生成箇所 (編集時): ドラッグ＆ドロップで新しい編集用インスタンスを作る。
        // factory 未指定時は従来のパラメータレス生成、指定時はその factory でコンストラクタ注入する。
        // factory の失敗時は報告し、パラメータレス生成で再試行して隠さない。受け入れ失敗時は生成側で解放する。
        object component;
        if (factory is null)
        {
            component = Activator.CreateInstance(type!)!;
        }
        else
        {
            object? created;
            try
            {
                created = factory(type!);
            }
            catch (Exception error)
            {
                throw new InvalidOperationException(
                    $"Failed to create component {type!.FullName} for object '{target!.Name}'.", error);
            }
            if (created is null)
                throw new InvalidOperationException($"Component factory returned null for {type!.FullName}.");
            if (created.GetType() != type)
                throw new InvalidOperationException(
                    $"Component factory returned {created.GetType().FullName} instead of {type!.FullName}.");
            component = created;
        }
        try
        {
            target!.Attach(component);
        }
        catch
        {
            if (component is IDisposable disposable)
            {
                try { disposable.Dispose(); }
                catch { /* Attach 失敗の元例外を優先する。 */ }
            }
            throw;
        }
        return true;
    }
}
