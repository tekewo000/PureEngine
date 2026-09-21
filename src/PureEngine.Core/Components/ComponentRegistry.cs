namespace PureEngine.Core;

/// <summary>Stable scene IDs for explicitly registered plain C# component types.</summary>
public sealed class ComponentRegistry
{
    private readonly Dictionary<string, Type> _types = new(StringComparer.Ordinal);

    public IReadOnlyList<Type> Types => _types.Values.ToArray();

    public void Register<T>(string id) where T : class => RegisterType(typeof(T), id);

    public void RegisterType(Type type, string id)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!type.IsClass)
            throw new ArgumentException($"Component must be a class: {type.FullName}");
        if (id != id.Trim() || _types.ContainsKey(id) || _types.ContainsValue(type))
            throw new ArgumentException($"Duplicate or invalid component registration: {id}");
        _types.Add(id, type);
    }

    public Type GetType(string id) => _types.TryGetValue(id, out var type)
        ? type : throw new InvalidDataException($"Unknown component typeId: {id}");

    public string GetId(Type type) => _types.FirstOrDefault(pair => pair.Value == type).Key
        ?? throw new InvalidDataException($"Unregistered component: {type.FullName}");

    /// <summary>Removes a registration, used when project user code is reloaded. Samples stay registered.</summary>
    public bool Unregister(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var entry = _types.FirstOrDefault(pair => pair.Value == type);
        if (entry.Key is null) return false;
        return _types.Remove(entry.Key);
    }

    public IReadOnlyList<string> Ids => _types.Keys.ToArray();
}
