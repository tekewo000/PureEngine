namespace PureEngine.Editor;

/// <summary>
/// Decides conflicts between save, play, reload, and file operations without referencing UI controls.
/// Passes view-derived state (playing, file-busy, input errors, unsaved changes) as bool values.
/// MainWindow aggregates <c>_invalidFields.Count &gt; 0 || NameError.IsVisible</c> and similar state
/// into <c>hasInputErrors</c> and only handles displaying the resulting reason.
/// Applies the same constraints when adopting async compilation results.
/// </summary>
public static class EditorOperationGate
{
    /// <summary>Reason to defer a reload. Null when it can run immediately.</summary>
    public static string? ReloadBlockReason(bool isPlaying, bool fileBusy, bool hasInputErrors)
    {
        if (isPlaying) return "Reload deferred while playing.";
        if (fileBusy) return "Reload deferred during file operations.";
        if (hasInputErrors) return "Reload deferred while there are input errors.";
        return null;
    }

    /// <summary>Reason to refuse starting play. Null when it can start.</summary>
    public static string? PlayBlockReason(bool alreadyPlaying, bool fileBusy, bool hasInputErrors)
    {
        if (alreadyPlaying) return "Already playing.";
        if (fileBusy) return "Cannot play during file operations.";
        if (hasInputErrors) return "Fix the Inspector input errors.";
        return null;
    }

    /// <summary>Reason to refuse scene file operations (open, save, new, Explorer operations). Null when they can run.</summary>
    public static string? FileOperationBlockReason(bool isPlaying, bool fileBusy)
    {
        if (isPlaying) return "Cannot operate on scenes while playing. Stop first.";
        if (fileBusy) return "A file operation is in progress.";
        return null;
    }

    /// <summary>Reason to refuse saving. Null when it can save.</summary>
    public static string? SaveBlockReason(bool hasInputErrors) =>
        hasInputErrors ? "Fix the Inspector input errors." : null;

    /// <summary>Whether an unsaved-changes confirmation dialog is needed. Discarding with input errors also requires confirmation.</summary>
    public static bool NeedsUnsavedConfirmation(bool isDirty, bool hasInputErrors) =>
        isDirty || hasInputErrors;

    /// <summary>Whether a reload can run now. Includes pending state in the decision.</summary>
    public static bool CanReloadNow(bool hasPending, bool isReloading, bool isPlaying, bool fileBusy, bool hasInputErrors) =>
        hasPending && !isReloading && ReloadBlockReason(isPlaying, fileBusy, hasInputErrors) is null;
}
