namespace PureEngine.Core;

/// <summary>Stable reference to one localization table row. Resolution always goes through LocalizationService.</summary>
/// <remarks>
/// Identity only; the key name, text, and voice live in the table. Saves keep the ID so renaming the key never breaks them.
/// Has no parameterless constructor on purpose so the Inspector value system treats it as a reference, never an inline value.
/// </remarks>
public sealed class LocalizedTextId(Guid id)
{
    public Guid Id { get; } = id;

    public bool IsEmpty => Id == Guid.Empty;
}
