namespace PureEngine.Core.Attributes;

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class InspectorAttribute : Attribute
{
}
