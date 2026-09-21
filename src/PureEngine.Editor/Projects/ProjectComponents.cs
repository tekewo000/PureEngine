using PureEngine.Core;

namespace PureEngine.Editor;

/// <summary>
/// プロジェクト単位の型登録と読込コードの所有者（A4）。
/// Registry・自作型一覧とソース対応・採用中のコンパイル結果と読込コード（ALC）を1か所で保持し、
/// 再読み込み時の差し替えと終了時の解放要求の責任を持つ。
/// 別プロジェクトの所有者に触れず、失敗時は旧状態を保持する。
/// 終了順序：編集SceneのComponent破棄 → 編集サービス破棄 → コード解放要求（Unload）。
/// Unloadは要求のみで、参照が残る間はGCまで生存する。所有者は旧Type・Assembly・結果への参照を残さない。
/// 組み込み型（sample.*）はプロジェクト生成時に各所有者のRegistryへ登録し、user.* の差し替えでは維持する。
/// 保存互換：typeIdと .pureengine/types.json の形式は従来どおり。Sceneの保存・復元はこのRegistryを明示的に渡す。
/// A2（再読み込み切り出し）・A3（バックグラウンド化）はこの所有者の CreateCandidateRegistry／Adopt／Dispose を入口に使う。
/// </summary>
public sealed class ProjectComponents : IDisposable
{
    private readonly object _sync = new();
    private UserCodeCompileResult? _userCode;
    private readonly Dictionary<string, IReadOnlyList<Type>> _userFileTypes
        = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>このプロジェクト専用の登録表。組み込み＋自作（user.*）を含む。インスタンスは維持し、中身だけ差し替える。</summary>
    public ComponentRegistry Registry { get; } = new();

    public ProjectComponents()
    {
        ComponentAssets.RegisterBuiltins(Registry);
    }

    /// <summary>このプロジェクトの全登録型（組み込み＋自作）。アタッチ可否の判定に使う。</summary>
    public IReadOnlyList<Type> Types => Registry.Types;

    /// <summary>このプロジェクトの自作C#の現在の一覧。失敗時は空。</summary>
    public IReadOnlyList<Type> UserTypes
    {
        get { lock (_sync) return _userCode?.AttachableTypes ?? []; }
    }

    public IReadOnlyDictionary<string, IReadOnlyList<Type>> UserFileTypes
    {
        get { lock (_sync) return new Dictionary<string, IReadOnlyList<Type>>(_userFileTypes, StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>採用中のコンパイル結果。A2・A3が採用・解放の状態を確認する入口。外部で保持・破棄しない。</summary>
    internal UserCodeCompileResult? ActiveUserCode
    {
        get { lock (_sync) return _userCode; }
    }

    /// <summary>指定C#ファイルに含まれるアタッチ対象の型。ヘルパーのみのファイルは空。</summary>
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
    /// 候補の検証用に、現在の非user.*登録＋新しいコンパイル結果から作る。所有者は変更しない。
    /// 開く処理・再読み込みはこの候補でScene復元・移行を試してからAdoptする。
    /// </summary>
    public ComponentRegistry CreateCandidateRegistry(UserCodeCompileResult? result) =>
        ComponentAssets.CreateCandidateRegistry(Registry, result);

    /// <summary>
    /// コンパイル成功時に呼ぶ。候補を検証してから採用し、直前の正常な user.* 登録だけを外す。
    /// 同じRegistryインスタンスを維持するため、既存のSceneSerializerはそのまま使える。
    /// 失敗時は旧登録とSceneを保持し、候補のALCは呼び出し側で解放する。
    /// </summary>
    public void Adopt(UserCodeCompileResult? result)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (result is { Success: false }) throw new ArgumentException("Cannot publish failed compilation.", nameof(result));
        _ = CreateCandidateRegistry(result); // Validate before removing any current registrations.
        if (result is not null) UserCodeIdentity.Save(result);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
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
            // 呼び出し側は旧SceneのComponentをAdopt後に破棄する（再読み込み）か、事前に破棄する（終了）。
            if (previous?.LoadContext is not null)
            {
                try { previous.LoadContext.Unload(); } catch { }
            }
            if (result is null || !result.Success) return;
            foreach (var type in result.AttachableTypes)
            {
                var id = result.GetTypeId(type);
                Registry.RegisterType(type, id);
            }
            foreach (var (path, types) in result.FileTypes)
                _userFileTypes[Path.GetFullPath(path)] = types;
            _userCode = result;
        }
    }

    public void Clear() => Adopt(null);

    /// <summary>
    /// 終了時の解放。呼び出し側が編集SceneのComponentとサービスを先に破棄してから呼ぶ。
    /// user.* 登録を外し、ALCのUnloadを要求する。組み込み登録は残るが、所有者自体は破棄済みになる。
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
