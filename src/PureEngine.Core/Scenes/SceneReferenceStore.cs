namespace PureEngine.Core;

/// <summary>
/// Scene-owned missing identities and legacy inline values for null reference slots.
/// Used at preparation, persistence, and structural mutation boundaries, never per frame.
/// </summary>
public sealed class SceneReferenceStore
{
    public static string DictionaryPath(string path, string key) => $"{path}[{Uri.EscapeDataString(key)}]";

    private readonly Dictionary<(Guid OwnerId, string Path), Guid> _missing = [];
    private readonly Dictionary<(Guid OwnerId, string Path), object?> _legacy = [];
    private readonly Dictionary<(Guid OwnerId, string Path), Guid> _missingPrefabs = [];

    public int MissingCount => _missing.Count;

    public int LegacyCount => _legacy.Count;

    public bool HasLegacy => _legacy.Count != 0;

    public bool TryGetMissing(Guid ownerId, string path, out Guid targetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return _missing.TryGetValue((ownerId, path), out targetId);
    }

    public void SetMissing(Guid ownerId, string path, Guid targetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (ownerId == Guid.Empty || targetId == Guid.Empty)
            throw new ArgumentException("Reference IDs must not be empty.");
        _missingPrefabs.Remove((ownerId, path));
        _missing[(ownerId, path)] = targetId;
    }

    internal void SetMissingPrefab(Guid ownerId, string path, Guid prefabId, Guid targetId)
    {
        SetMissing(ownerId, path, targetId);
        _missingPrefabs[(ownerId, path)] = prefabId;
    }

    internal bool TryGetMissingPrefab(Guid ownerId, string path, out Guid prefabId) =>
        _missingPrefabs.TryGetValue((ownerId, path), out prefabId);

    public bool ClearMissing(Guid ownerId, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _missingPrefabs.Remove((ownerId, path));
        return _missing.Remove((ownerId, path));
    }

    public void RemoveOwner(Guid ownerId)
    {
        foreach (var key in _missing.Keys.Where(key => key.OwnerId == ownerId).ToArray())
            ClearMissing(key.OwnerId, key.Path);
        foreach (var key in _legacy.Keys.Where(key => key.OwnerId == ownerId).ToArray())
            _legacy.Remove(key);
    }

    public void RemovePathsForMember(Guid ownerId, string memberName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);
        var prefix = memberName + ".";
        var elements = memberName + "[";
        foreach (var key in _missing.Keys.Where(key => key.OwnerId == ownerId
            && (key.Path == memberName || key.Path.StartsWith(prefix, StringComparison.Ordinal)
                || key.Path.StartsWith(elements, StringComparison.Ordinal))).ToArray())
            ClearMissing(key.OwnerId, key.Path);
        foreach (var key in _legacy.Keys.Where(key => key.OwnerId == ownerId
            && (key.Path == memberName || key.Path.StartsWith(prefix, StringComparison.Ordinal)
                || key.Path.StartsWith(elements, StringComparison.Ordinal))).ToArray())
            _legacy.Remove(key);
    }

    public bool TryGetLegacy(Guid ownerId, string path, out object? raw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return _legacy.TryGetValue((ownerId, path), out raw);
    }

    /// <summary>Removes a list or vector row and shifts retained paths in all later rows.</summary>
    public void RemoveSequenceElement(Guid ownerId, string path, int index)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        RemapPaths(ownerId, savedPath =>
        {
            var prefix = path + "[";
            if (!savedPath.StartsWith(prefix, StringComparison.Ordinal)) return savedPath;
            var end = savedPath.IndexOf(']', prefix.Length);
            if (end < 0 || !int.TryParse(savedPath.AsSpan(prefix.Length, end - prefix.Length),
                System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var savedIndex))
                return savedPath;
            if (savedIndex == index) return null;
            return savedIndex > index ? $"{path}[{savedIndex - 1}]{savedPath[(end + 1)..]}" : savedPath;
        });
    }

    /// <summary>Moves a dictionary row, including nested Missing and legacy reference slots.</summary>
    public void RenameDictionaryKey(Guid ownerId, string path, string oldKey, string newKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(oldKey);
        ArgumentNullException.ThrowIfNull(newKey);
        if (oldKey == newKey) return;
        var oldPath = DictionaryPath(path, oldKey);
        var newPath = DictionaryPath(path, newKey);
        RemovePathsForMember(ownerId, newPath);
        MovePath(ownerId, oldPath, newPath);
    }

    private void RemapPaths(Guid ownerId, Func<string, string?> remap)
    {
        Remap(_missing);
        Remap(_missingPrefabs);
        Remap(_legacy);
        void Remap<T>(Dictionary<(Guid OwnerId, string Path), T> entries)
        {
            var saved = entries.Where(entry => entry.Key.OwnerId == ownerId).ToList();
            foreach (var entry in saved) entries.Remove(entry.Key);
            foreach (var entry in saved)
                if (remap(entry.Key.Path) is { } path)
                    entries[(ownerId, path)] = entry.Value;
        }
    }

    /// <summary>Moves retained IDs and legacy values when an Inspector collection slot moves.</summary>
    public void MovePath(Guid ownerId, string oldPath, string newPath)
    {
        Move(_missing);
        Move(_missingPrefabs);
        Move(_legacy);
        void Move<T>(Dictionary<(Guid OwnerId, string Path), T> entries)
        {
            var moved = entries.Where(entry => entry.Key.OwnerId == ownerId
                && (entry.Key.Path == oldPath || entry.Key.Path.StartsWith(oldPath + ".", StringComparison.Ordinal)
                    || entry.Key.Path.StartsWith(oldPath + "[", StringComparison.Ordinal))).ToArray();
            foreach (var entry in moved) entries.Remove(entry.Key);
            foreach (var entry in moved) entries[(ownerId, newPath + entry.Key.Path[oldPath.Length..])] = entry.Value;
        }
    }

    public void SetLegacy(Guid ownerId, string path, object? raw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (ownerId == Guid.Empty)
            throw new ArgumentException("Owner ID must not be empty.");
        _legacy[(ownerId, path)] = raw;
    }

    public bool ClearLegacy(Guid ownerId, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return _legacy.Remove((ownerId, path));
    }

    public IReadOnlyList<(Guid OwnerId, string Path, Guid TargetId)> MissingEntries =>
        [.. _missing.Select(entry => (entry.Key.OwnerId, entry.Key.Path, entry.Value))];

    public IReadOnlyList<(Guid OwnerId, string Path)> LegacyEntries =>
        [.. _legacy.Select(entry => (entry.Key.OwnerId, entry.Key.Path))];
}
