using PureEngine.Core;

namespace PureEngine.Editor;

public static class ComponentAssets
{
    private static UserCodeCompileResult? _userCode;
    private static readonly Dictionary<string, IReadOnlyList<Type>> _userFileTypes
        = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Sync = new();

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

    /// <summary>
    /// プロジェクトの自作C#の現在の一覧。フォルダ構成のまま表示・D&Dするためのファイル対応付き。
    /// 自作クラスを使うたびにエンジン側へ手動登録を追加する必要はない。自動IDは "user."＋FullName。
    /// </summary>
    public static IReadOnlyList<Type> UserTypes
    {
        get { lock (Sync) return _userCode?.AttachableTypes ?? []; }
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<Type>> UserFileTypes
    {
        get { lock (Sync) return new Dictionary<string, IReadOnlyList<Type>>(_userFileTypes, StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>指定C#ファイルに含まれるアタッチ対象の型。ヘルパーのみのファイルは空。</summary>
    public static IReadOnlyList<Type> GetTypesForFile(string? fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return [];
        lock (Sync)
        {
            var key = Path.GetFullPath(fullPath!);
            return _userFileTypes.TryGetValue(key, out var types) ? types : [];
        }
    }

    /// <summary>
    /// コンパイル成功時に呼ぶ。直前の正常な user.* 登録だけを外し、新しい型を自動IDで登録する。
    /// 同じRegistryインスタンスを維持するため、既存のSceneSerializerはそのまま使える。
    /// </summary>
    public static void SetUserCode(UserCodeCompileResult? result)
    {
        if (result is { Success: false }) throw new ArgumentException("Cannot publish failed compilation.", nameof(result));
        _ = CreateRegistry(result); // Validate before removing any current registrations.
        lock (Sync)
        {
            var previous = _userCode;
            // 直前の user.* だけを外す。sample.* やテスト登録（checks.*）は残す。
            foreach (var id in Registry.Ids.Where(id => id.StartsWith("user.", StringComparison.Ordinal)).ToArray())
            {
                var type = Registry.GetType(id);
                Registry.Unregister(type);
            }
            _userFileTypes.Clear();
            _userCode = null;
            // 古いALCは参照が残る間は生存し、GCで回収される。明示Unloadは参照切れ後に行われる。
            if (previous?.LoadContext is not null)
            {
                try { previous.LoadContext.Unload(); } catch { }
            }
            if (result is null || !result.Success) return;
            foreach (var type in result.AttachableTypes)
            {
                var id = UserCodeCompiler.TypeIdFor(type);
                Registry.RegisterType(type, id);
            }
            foreach (var (path, types) in result.FileTypes)
                _userFileTypes[Path.GetFullPath(path)] = types;
            _userCode = result;
        }
    }

    public static void ClearUserCode() => SetUserCode(null);

    internal static ComponentRegistry CreateRegistry(UserCodeCompileResult? result)
    {
        var registry = new ComponentRegistry();
        foreach (var id in Registry.Ids.Where(id => !id.StartsWith("user.", StringComparison.Ordinal)))
            registry.RegisterType(Registry.GetType(id), id);
        if (result is { Success: true })
            foreach (var type in result.AttachableTypes)
                registry.RegisterType(type, UserCodeCompiler.TypeIdFor(type));
        return registry;
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
