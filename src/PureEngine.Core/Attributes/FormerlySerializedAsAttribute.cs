namespace PureEngine.Core;

/// <summary>Accepts an earlier Inspector field/property name when loading or reloading a scene.</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = true, Inherited = true)]
public sealed class FormerlySerializedAsAttribute(string oldName) : Attribute
{
    public string OldName { get; } = oldName;
}
