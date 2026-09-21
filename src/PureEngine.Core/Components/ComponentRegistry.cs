namespace PureEngine.Core;

/// <summary>Stable scene IDs for explicitly registered plain C# component types.</summary>
public sealed class ComponentRegistry
{
    private readonly Dictionary<string, Type> _types = new(StringComparer.Ordinal);

    public IReadOnlyList<Type> Types => _types.Values.ToArray();

    public void Register<T>(string id) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id != id.Trim() || _types.ContainsKey(id) || _types.ContainsValue(typeof(T)))
            throw new ArgumentException($"Duplicate or invalid component registration: {id}");
        _types.Add(id, typeof(T));
    }

    public Type GetType(string id) => _types.TryGetValue(id, out var type)
        ? type : throw new InvalidDataException($"Unknown component typeId: {id}");

    public string GetId(Type type) => _types.FirstOrDefault(pair => pair.Value == type).Key
        ?? throw new InvalidDataException($"Unregistered component: {type.FullName}");
}
