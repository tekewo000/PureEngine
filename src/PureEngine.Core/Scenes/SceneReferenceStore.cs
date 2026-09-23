namespace PureEngine.Core;

/// <summary>
/// Scene所有の参照管理情報。C#がnullの欄のMissing IDと、旧インライン値の保持を担う。
/// 定常実行では使わず、準備・保存・構造変更の境界でのみ触る。毎フレーム走査しない。
/// </summary>
public sealed class SceneReferenceStore
{
    public static string DictionaryPath(string path, string key) => $"{path}[{Uri.EscapeDataString(key)}]";

    private readonly Dictionary<(Guid OwnerId, string Path), Guid> _missing = [];
    private readonly Dictionary<(Guid OwnerId, string Path), object?> _legacy = [];

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
        _missing[(ownerId, path)] = targetId;
    }

    public bool ClearMissing(Guid ownerId, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return _missing.Remove((ownerId, path));
    }

    public void RemoveOwner(Guid ownerId)
    {
        foreach (var key in _missing.Keys.Where(key => key.OwnerId == ownerId).ToArray())
            _missing.Remove(key);
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
            _missing.Remove(key);
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

    /// <summary>Moves retained IDs and legacy values when an Inspector collection slot moves.</summary>
    public void MovePath(Guid ownerId, string oldPath, string newPath)
    {
        Move(_missing);
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
