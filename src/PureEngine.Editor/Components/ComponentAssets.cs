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

    public static ComponentRegistry Registry { get; } = new();
    public static IReadOnlyList<Type> Types => Registry.Types;

    static ComponentAssets()
    {
        Registry.Register<Samples.PlayerStats>("sample.player-stats");
        Registry.Register<Samples.RoundSettings>("sample.round-settings");
        Registry.Register<Samples.InjectedPlayer>("sample.injected-player");
    }

    public static bool CanAttach(SceneObject? target, Type? type) =>
        target is not null && type is not null && Types.Contains(type)
        && !target.Components.Any(component => component.GetType() == type);

    public static bool TryAttach(SceneObject? target, Type? type, Func<Type, object>? factory = null)
    {
        if (!CanAttach(target, type)) return false;
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
