using PureEngine.Core;

namespace PureEngine.Editor;

/// <summary>
/// コード再読み込みの準備・採用・失敗時後片付けの中核。AvaloniaのWindowやControlを
/// 生成せず、画面なしで検証できる。MainWindowは操作受付・ダイアログ・選択状態・表示更新を担当する。
/// </summary>
/// <remarks>
/// 所有の境界：
/// - 編集SceneとRegistryは呼び出し側（将来はプロジェクト単位の所有者）が所有し、引数で受け取る。
///   このクラスは <see cref="ComponentAssets"/> のstatic構造を作り直さず、渡されたRegistryを使う。
/// - GameSession・PlaySessionの移動は行わず、factoryとして受け取るだけにする。
/// - 同期処理として責務を分離し、非同期化（A3）は行わない。
/// 採用順序：コンパイル成功だけでは採用せず、候補Registryの作成とScene移行の準備成功後に
/// publish（既定は <see cref="ComponentAssets.SetUserCode"/>）を呼ぶ。失敗時は旧状態を保持し、
/// 採用できなかった移行先の破棄とLoadContextの解放を行う。
/// </remarks>
public sealed class UserCodeReloadCoordinator
{
    public bool HasPending { get; private set; }

    public bool IsReloading { get; private set; }

    public void RequestPending() => HasPending = true;

    public void ClearPending() => HasPending = false;

    /// <summary>
    /// 今すぐ再読み込みできるかどうか。UIコントロール参照なしに判定する。
    /// </summary>
    public bool ShouldReloadNow(bool isPlaying, bool fileBusy, bool hasInputErrors) =>
        EditorOperationGate.CanReloadNow(HasPending, IsReloading, isPlaying, fileBusy, hasInputErrors);

    /// <summary>
    /// 準備のみを行う。成功時は移行済みSceneと候補Registryを返すが、全体Registryへの反映は行わない。
    /// 失敗時は旧Sceneに触らず、不要になったLoadContextを解放する。呼び出し側は成功時に
    /// <see cref="Adopt"/>、失敗時または採用見送り時に <see cref="Discard"/> を呼ぶ。
    /// </summary>
    public UserCodeReloadPreparation Prepare(
        Scene current,
        ComponentRegistry currentRegistry,
        Func<Type, object>? factory,
        string projectRoot,
        Func<string, UserCodeCompileResult>? compile = null,
        Func<UserCodeCompileResult, ComponentRegistry>? createCandidate = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(currentRegistry);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        compile ??= UserCodeCompiler.CompileProject;
        createCandidate ??= result => BuildCandidateRegistry(currentRegistry, result);

        UserCodeCompileResult? compiled = null;
        try
        {
            compiled = compile(projectRoot);
            if (compiled is null)
                throw new InvalidOperationException("Compilation returned no result.");
            if (!compiled.Success)
                return UserCodeReloadPreparation.FromCompileFailure(compiled);
            var candidate = createCandidate(compiled);
            var migrated = SceneCodeMigrator.Migrate(current, currentRegistry, candidate, factory);
            return UserCodeReloadPreparation.FromSuccess(compiled, candidate, migrated);
        }
        catch (Exception error) when (compiled is not null && !IsSuccessLike(compiled, error))
        {
            // 準備失敗：コンパイル済みのLoadContextだけを解放し、旧Sceneは保持する。
            try { compiled.LoadContext?.Unload(); } catch { }
            return UserCodeReloadPreparation.FromFailure(compiled, error);
        }
        catch (Exception error)
        {
            try { compiled?.LoadContext?.Unload(); } catch { }
            return UserCodeReloadPreparation.FromFailure(compiled, error);
        }
    }

    private static bool IsSuccessLike(UserCodeCompileResult compiled, Exception error) =>
        // Prepare内の想定外の例外はすべて失敗扱いにする。成功扱いの例外はない。
        false;

    /// <summary>
    /// 準備成功後の採用。全体Registryへの反映だけを行い、Sceneの差し替えは呼び出し側が行う。
    /// 既定は <see cref="ComponentAssets.SetUserCode"/> を使い、直前の正常なuser.*だけを置き換える。
    /// </summary>
    public void Adopt(
        UserCodeReloadPreparation preparation,
        Action<UserCodeCompileResult>? publish = null)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        if (!preparation.Success)
            throw new InvalidOperationException("Cannot adopt a failed preparation.");
        publish ??= static result => ComponentAssets.SetUserCode(result);
        publish(preparation.Compiled!);
    }

    /// <summary>
    /// 採用しなかった準備の後片付け。移行先Componentの破棄とLoadContextの解放を行う。
    /// 旧Sceneには触らない。
    /// </summary>
    public void Discard(
        UserCodeReloadPreparation preparation,
        Action<IEnumerable<object>>? disposeComponents = null)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        disposeComponents ??= static components => ComponentAssets.DisposeComponents(components);
        if (preparation.MigratedScene is not null)
        {
            try
            {
                disposeComponents(preparation.MigratedScene.Objects.SelectMany(item => item.Components));
            }
            catch
            {
                // 後片付けの失敗は呼び出し側の報告に任せ、Unloadは続ける。
                try { preparation.Compiled?.LoadContext?.Unload(); } catch { }
                throw;
            }
        }
        try { preparation.Compiled?.LoadContext?.Unload(); } catch { }
    }

    /// <summary>
    /// 準備・採用・後片付けを一括で行う。MainWindowのReloadUserCodeから使う同期処理。
    /// 競合時は作業せず保留にして <see cref="UserCodeReloadOutcome.Deferred"/> を返す。
    /// 成功時は移行済みSceneを返し、旧Sceneの破棄は呼び出し側が行う。
    /// </summary>
    public UserCodeReloadOutcome Reload(
        Scene current,
        ComponentRegistry currentRegistry,
        Func<Type, object>? factory,
        string projectRoot,
        bool isPlaying,
        bool fileBusy,
        bool hasInputErrors,
        Func<string, UserCodeCompileResult>? compile = null,
        Func<UserCodeCompileResult, ComponentRegistry>? createCandidate = null,
        Action<UserCodeCompileResult>? publish = null,
        Action<IEnumerable<object>>? disposeComponents = null)
    {
        if (IsReloading)
        {
            HasPending = true;
            return UserCodeReloadOutcome.Deferred("再読み込み中です。");
        }
        var blockReason = EditorOperationGate.ReloadBlockReason(isPlaying, fileBusy, hasInputErrors);
        if (blockReason is not null)
        {
            HasPending = true;
            return UserCodeReloadOutcome.Deferred(blockReason);
        }

        HasPending = false;
        IsReloading = true;
        UserCodeReloadPreparation? preparation = null;
        var adopted = false;
        try
        {
            preparation = Prepare(current, currentRegistry, factory, projectRoot, compile, createCandidate);
            if (!preparation.Success)
            {
                // コンパイル失敗・移行失敗は旧状態を保持する。Prepare内でUnload済み。
                // 移行に成功して見えるがSuccess=falseの場合は念のため破棄する。
                if (preparation.MigratedScene is not null)
                {
                    try { Discard(preparation, disposeComponents); } catch (Exception discardError)
                    {
                        return UserCodeReloadOutcome.Failed(preparation.Compiled, preparation.Diagnostics,
                            preparation.FailureException is null ? discardError
                                : new AggregateException(preparation.FailureException, discardError),
                            adoptedAfterPublish: false);
                    }
                }
                return preparation.FailureException is not null
                    ? UserCodeReloadOutcome.Failed(preparation.Compiled, preparation.Diagnostics, preparation.FailureException, adoptedAfterPublish: false)
                    : UserCodeReloadOutcome.CompileFailed(preparation.Compiled!);
            }

            try
            {
                Adopt(preparation, publish);
            }
            catch (Exception publishError)
            {
                try { Discard(preparation, disposeComponents); } catch (Exception discardError)
                {
                    return UserCodeReloadOutcome.Failed(preparation.Compiled, preparation.Diagnostics,
                        new AggregateException(publishError, discardError), adoptedAfterPublish: false);
                }
                return UserCodeReloadOutcome.Failed(preparation.Compiled, preparation.Diagnostics, publishError, adoptedAfterPublish: false);
            }

            adopted = true;
            return UserCodeReloadOutcome.Succeeded(preparation.Compiled!, preparation.MigratedScene!, preparation.CandidateRegistry!);
        }
        catch (Exception error)
        {
            if (preparation is not null && !adopted)
            {
                try { Discard(preparation, disposeComponents); } catch { }
            }
            else
            {
                try { preparation?.Compiled?.LoadContext?.Unload(); } catch { }
            }
            return UserCodeReloadOutcome.Failed(preparation?.Compiled, [], error, adoptedAfterPublish: adopted);
        }
        finally
        {
            IsReloading = false;
        }
    }

    /// <summary>
    /// 渡されたRegistryを所有者として候補Registryを作る。直前のuser.*以外を複写し、新しい型を自動IDで登録する。
    /// <see cref="ComponentAssets.CreateRegistry"/> と同じ規則だが、static所有を作り直さず引数のRegistryを使う。
    /// 将来A4でプロジェクト単位の所有者が来てもこのまま受け取れる。
    /// </summary>
    public static ComponentRegistry BuildCandidateRegistry(ComponentRegistry current, UserCodeCompileResult result)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(result);
        var registry = new ComponentRegistry();
        foreach (var id in current.Ids.Where(id => !id.StartsWith("user.", StringComparison.Ordinal)))
            registry.RegisterType(current.GetType(id), id);
        if (result.Success)
            foreach (var type in result.AttachableTypes)
                registry.RegisterType(type, result.GetTypeId(type));
        return registry;
    }

    /// <summary>テスト用の明示解放。採用しなかったコンパイル結果のLoadContextを解放する。</summary>
    public static void Unload(UserCodeCompileResult? result)
    {
        try { result?.LoadContext?.Unload(); } catch { }
    }
}

/// <summary>準備の結果。成功時は採用・破棄のどちらかを必ず呼ぶ。</summary>
public sealed class UserCodeReloadPreparation
{
    private UserCodeReloadPreparation() { }

    public bool Success { get; private init; }

    public UserCodeCompileResult? Compiled { get; private init; }

    public IReadOnlyList<UserCodeDiagnostic> Diagnostics => Compiled?.Diagnostics ?? [];

    public ComponentRegistry? CandidateRegistry { get; private init; }

    public Scene? MigratedScene { get; private init; }

    public Exception? FailureException { get; private init; }

    public static UserCodeReloadPreparation FromSuccess(
        UserCodeCompileResult compiled, ComponentRegistry candidate, Scene migrated) => new()
        {
            Success = true,
            Compiled = compiled,
            CandidateRegistry = candidate,
            MigratedScene = migrated,
        };

    public static UserCodeReloadPreparation FromCompileFailure(UserCodeCompileResult compiled) => new()
    {
        Success = false,
        Compiled = compiled,
    };

    public static UserCodeReloadPreparation FromFailure(UserCodeCompileResult? compiled, Exception error) => new()
    {
        Success = false,
        Compiled = compiled,
        FailureException = error,
    };
}

/// <summary>一括再読み込みの結果。画面表示に必要な診断と採用状態を持つ。</summary>
public sealed class UserCodeReloadOutcome
{
    private UserCodeReloadOutcome() { }

    /// <summary>採用まで完了したかどうか。コンパイル成功だけではtrueにならない。</summary>
    public bool Success { get; private init; }

    /// <summary>競合により作業せず保留にしたかどうか。</summary>
    public bool IsDeferred { get; private init; }

    /// <summary>publish後に失敗したかどうか。trueの場合は旧コードの解放失敗として報告する。</summary>
    public bool AdoptedBeforeFailure { get; private init; }

    public string? DeferredReason { get; private init; }

    public UserCodeCompileResult? Compiled { get; private init; }

    public IReadOnlyList<UserCodeDiagnostic> Diagnostics => Compiled?.Diagnostics ?? [];

    public Scene? MigratedScene { get; private init; }

    public ComponentRegistry? CandidateRegistry { get; private init; }

    public Exception? FailureException { get; private init; }

    public int AttachableCount => Compiled?.AttachableTypes.Count ?? 0;

    public static UserCodeReloadOutcome Succeeded(
        UserCodeCompileResult compiled, Scene migrated, ComponentRegistry candidate) => new()
        {
            Success = true,
            Compiled = compiled,
            MigratedScene = migrated,
            CandidateRegistry = candidate,
        };

    public static UserCodeReloadOutcome CompileFailed(UserCodeCompileResult compiled) => new()
    {
        Success = false,
        Compiled = compiled,
    };

    public static UserCodeReloadOutcome Failed(
        UserCodeCompileResult? compiled,
        IReadOnlyList<UserCodeDiagnostic> diagnostics,
        Exception error,
        bool adoptedAfterPublish) => new()
        {
            Success = false,
            Compiled = compiled,
            FailureException = error,
            AdoptedBeforeFailure = adoptedAfterPublish,
        };

    public static UserCodeReloadOutcome Deferred(string reason) => new()
    {
        Success = false,
        IsDeferred = true,
        DeferredReason = reason,
    };
}
