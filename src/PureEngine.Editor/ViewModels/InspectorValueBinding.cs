using System.Collections;
using System.Reflection;

namespace PureEngine.Editor;

/// <summary>A live value location. Child writes commit through every parent, including boxed structs.</summary>
internal abstract class InspectorValueBinding(object owner, Type valueType)
{
    public object Owner { get; } = owner;
    public Type ValueType { get; } = valueType;
    public abstract object? Read();
    public abstract void Write(object? value);
    public abstract PropertyInfo ValueMember { get; }

    public static InspectorValueBinding Create(object owner, Type type, Func<object?> read, Action<object?> write) =>
        (InspectorValueBinding)Activator.CreateInstance(typeof(TypedValue<>).MakeGenericType(type), owner, read, write)!;

    public static InspectorValueBinding ForMember(object owner, MemberInfo member) =>
        Create(owner, MemberType(member), () => InspectorViewModel.ReadMember(owner, member),
            value => WriteMember(owner, member, value));

    public InspectorValueBinding Member(MemberInfo member) =>
        Create(Owner, MemberType(member), () => InspectorViewModel.ReadMember(Read()!, member), value =>
        {
            var parent = Read()!;
            WriteMember(parent, member, value);
            Write(parent);
        });

    public InspectorValueBinding Element(Type type, int[] indices) =>
        Create(Owner, type, () => Read() is Array array ? array.GetValue(indices) : ((IList)Read()!)[indices[0]], value =>
        {
            var parent = Read()!;
            if (parent is Array array) array.SetValue(value, indices);
            else ((IList)parent)[indices[0]] = value;
            Write(parent);
        });

    public InspectorValueBinding Entry(Type type, string key) =>
        Create(Owner, type, () => ((IDictionary)Read()!)[key], value =>
        {
            var parent = (IDictionary)Read()!;
            parent[key] = value;
            Write(parent);
        });

    private static Type MemberType(MemberInfo member) =>
        member is FieldInfo field ? field.FieldType : ((PropertyInfo)member).PropertyType;

    private static void WriteMember(object owner, MemberInfo member, object? value)
    {
        if (member is FieldInfo field) field.SetValue(owner, value);
        else ((PropertyInfo)member).SetValue(owner, value);
    }

    private sealed class TypedValue<T>(object owner, Func<object?> read, Action<object?> write)
        : InspectorValueBinding(owner, typeof(T))
    {
        public T? Value
        {
            get => (T?)read();
            set => write(value);
        }

        public override object? Read() => read();
        public override void Write(object? value) => write(value);
        public override PropertyInfo ValueMember => typeof(TypedValue<T>).GetProperty(nameof(Value))!;
    }
}
