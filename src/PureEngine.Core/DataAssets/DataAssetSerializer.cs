using System.Reflection;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PureEngine.Core;

/// <summary>Reads and writes data asset files. Reuses Inspector value conversion; scene references stay scene-owned and are rejected here.</summary>
public sealed class DataAssetSerializer(ComponentRegistry registry)
{
    public const int CurrentVersion = 1;
    public const string FileExtension = ".pure.asset.yaml";

    private readonly Lazy<ISerializer> _writer = new(static () => new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithQuotingNecessaryStrings().DisableAliases()
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull).Build());
    private readonly Lazy<IDeserializer> _reader = new(static () => new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithDuplicateKeyChecking().Build());

    /// <summary>Creates a default instance of a registered data asset type. Uses the public parameterless constructor.</summary>
    public object CreateInstance(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!DataAssetDescriptor.TryCreate(type, registry, out _, out var error))
            throw new InvalidDataException(error);
        return Activator.CreateInstance(type)!;
    }

    /// <summary>Captures the asset identity and Inspector values without file I/O.</summary>
    public string Serialize(object asset, Guid assetId)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (assetId == Guid.Empty) throw new InvalidDataException("Data asset ID must not be empty.");
        var type = asset.GetType();
        if (!DataAssetDescriptor.TryCreate(type, registry, out var descriptor, out var error) || descriptor is null)
            throw new InvalidDataException(error);
        if (asset.GetType() != descriptor.Type)
            throw new InvalidDataException($"{type.FullName}: derived-type assignment is not supported.");
        var values = new Dictionary<string, object?>();
        foreach (var member in ComponentSchema.GetInspectorMembers(type))
        {
            var memberType = MemberType(member);
            if (SceneReferenceTypes.ContainsAssetExternalReference(memberType, registry))
                throw new InvalidDataException($"{descriptor.TypeId}.{member.Name}: scene references cannot be stored in data assets.");
            InspectorValueTypes.ValidateType(memberType);
            var value = member is FieldInfo field ? field.GetValue(asset) : ((PropertyInfo)member).GetValue(asset);
            values.Add(member.Name, InspectorValueTypes.ToStorable(value, memberType));
        }
        return _writer.Value.Serialize(new DataAssetDocument
        {
            Version = CurrentVersion,
            Id = assetId,
            TypeId = descriptor.TypeId,
            Values = values,
        });
    }

    public (object Instance, Guid Id) Deserialize(string yaml) => Deserialize(yaml, out _, out _);

    /// <summary>Restores the asset and reports added, renamed, or discarded Inspector names so editors can request a save.</summary>
    public (object Instance, Guid Id) Deserialize(string yaml, out bool membersChanged) =>
        Deserialize(yaml, out _, out membersChanged);

    public (object Instance, Guid Id) Deserialize(string yaml, out string typeId, out bool membersChanged)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        membersChanged = false;
        var document = _reader.Value.Deserialize<DataAssetDocument>(yaml)
            ?? throw new InvalidDataException("The data asset document is empty.");
        if (document.Version != CurrentVersion)
            throw new InvalidDataException($"Unsupported data asset version: {document.Version}");
        if (document.Id == Guid.Empty) throw new InvalidDataException("Data asset ID must not be empty.");
        if (string.IsNullOrWhiteSpace(document.TypeId))
            throw new InvalidDataException("Data asset typeId is required.");
        if (document.Values is null) throw new InvalidDataException("Data asset values are required.");
        var type = registry.GetType(document.TypeId);
        if (!DataAssetDescriptor.TryCreate(type, registry, out var descriptor, out var error) || descriptor is null)
            throw new InvalidDataException(error);
        if (type != descriptor.Type)
            throw new InvalidDataException($"{document.TypeId}: derived-type assignment is not supported.");
        var names = ComponentSchema.GetInspectorMemberNames(type);
        Dictionary<MemberInfo, object?> values = [];
        foreach (var (name, raw) in document.Values)
        {
            if (!names.TryGetValue(name, out var member))
            {
                membersChanged = true;
                continue;
            }
            if (name != member.Name) membersChanged = true;
            if (!values.TryAdd(member, raw))
                throw new InvalidDataException($"{document.TypeId}.{member.Name}: multiple saved names refer to the same Inspector member.");
        }
        var instance = Activator.CreateInstance(type)!;
        foreach (var member in ComponentSchema.GetInspectorMembers(type))
        {
            var memberType = MemberType(member);
            if (SceneReferenceTypes.ContainsAssetExternalReference(memberType, registry))
                throw new InvalidDataException($"{document.TypeId}.{member.Name}: scene references cannot be stored in data assets.");
            InspectorValueTypes.ValidateType(memberType);
            if (!values.TryGetValue(member, out var raw))
            {
                membersChanged = true;
                continue;
            }
            var value = InspectorValueTypes.FromStorable(raw, memberType, $"{document.TypeId}.{member.Name}");
            if (member is FieldInfo field) field.SetValue(instance, value);
            else ((PropertyInfo)member).SetValue(instance, value);
        }
        typeId = descriptor.TypeId;
        return (instance, document.Id);
    }

    private static Type MemberType(MemberInfo member) => member is FieldInfo field
        ? field.FieldType : ((PropertyInfo)member).PropertyType;
}