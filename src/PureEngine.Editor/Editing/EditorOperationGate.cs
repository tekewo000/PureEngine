namespace PureEngine.Editor;

/// <summary>
/// 保存・Play・再読み込み・ファイル操作の競合判定をUIコントロール参照なしに行う。
/// 画面由来の情報（Play中・ファイル操作中・入力エラー・未保存）はbool値として渡す。
/// MainWindowは <c>_invalidFields.Count &gt; 0 || NameError.IsVisible</c> などを
/// <c>hasInputErrors</c> に集約して渡し、判定結果の理由表示だけを担当する。
/// 非同期コンパイルの結果採用時にも同じ制約を確認する。
/// </summary>
public static class EditorOperationGate
{
    /// <summary>再読み込みを保留すべき理由。直ちに実行できる場合はnull。</summary>
    public static string? ReloadBlockReason(bool isPlaying, bool fileBusy, bool hasInputErrors)
    {
        if (isPlaying) return "Reload deferred while playing.";
        if (fileBusy) return "Reload deferred during file operations.";
        if (hasInputErrors) return "Reload deferred while there are input errors.";
        return null;
    }

    /// <summary>Play開始を拒否すべき理由。開始できる場合はnull。</summary>
    public static string? PlayBlockReason(bool alreadyPlaying, bool fileBusy, bool hasInputErrors)
    {
        if (alreadyPlaying) return "Already playing.";
        if (fileBusy) return "Cannot play during file operations.";
        if (hasInputErrors) return "Fix the Inspector input errors.";
        return null;
    }

    /// <summary>シーンのファイル操作（開く・保存・新規・Explorer操作）を拒否すべき理由。実行できる場合はnull。</summary>
    public static string? FileOperationBlockReason(bool isPlaying, bool fileBusy)
    {
        if (isPlaying) return "Cannot operate on scenes while playing. Stop first.";
        if (fileBusy) return "A file operation is in progress.";
        return null;
    }

    /// <summary>保存を拒否すべき理由。保存できる場合はnull。</summary>
    public static string? SaveBlockReason(bool hasInputErrors) =>
        hasInputErrors ? "Fix the Inspector input errors." : null;

    /// <summary>未保存確認ダイアログが必要かどうか。入力エラー中の破棄も確認対象にする。</summary>
    public static bool NeedsUnsavedConfirmation(bool isDirty, bool hasInputErrors) =>
        isDirty || hasInputErrors;

    /// <summary>再読み込みを今実行できるかどうか。保留状態の有無も含めて判定する。</summary>
    public static bool CanReloadNow(bool hasPending, bool isReloading, bool isPlaying, bool fileBusy, bool hasInputErrors) =>
        hasPending && !isReloading && ReloadBlockReason(isPlaying, fileBusy, hasInputErrors) is null;
}
