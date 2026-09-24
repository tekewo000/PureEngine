namespace PureEngine.Core;

/// <summary>Marks a plain C# class as creatable data asset. The editor lists it under Create &gt; Data Asset.</summary>
/// <param name="menuPath">Optional menu location such as Items/Weapon. Defaults to the type name.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class DataAssetAttribute(string? menuPath = null) : Attribute
{
    public string? MenuPath { get; } = menuPath;
}
