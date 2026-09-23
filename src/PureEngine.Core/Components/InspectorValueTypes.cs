using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Reflection;

namespace PureEngine.Core;

/// <summary>Inspector・保存対象の値型。scalar・enum・ベクトル・Transform・Sprite・配列・List・辞書・入れ子の自作クラスを扱う。</summary>
public static class InspectorValueTypes
{
    public static bool IsSupportedType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return IsSupportedTypeCore(type, []);
    }

    private static bool IsSupportedTypeCore(Type type, HashSet<Type> chain)
    {
        if (type == typeof(string))
            return true;
        if (type == typeof(int) || type == typeof(float) || type == typeof(double) || type == typeof(bool))
            return true;
        if (type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4) || type == typeof(Quaternion))
            return true;
        if (type == typeof(Transform))
            return true;
        if (type == typeof(Sprite))
            return true;
        if (type.IsEnum)
            return true;
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
            return underlying == typeof(int) || underlying == typeof(float) || underlying == typeof(double) || underlying == typeof(bool)
                || underlying == typeof(Vector2) || underlying == typeof(Vector3) || underlying == typeof(Vector4) || underlying == typeof(Quaternion)
                || underlying.IsEnum;
        if (type.IsArray)
            return type.GetArrayRank() == 1 && IsSupportedElementCore(type.GetElementType()!, chain);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            return IsSupportedElementCore(type.GetGenericArguments()[0], chain);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            var args = type.GetGenericArguments();
            return args[0] == typeof(string) && IsSupportedElementCore(args[1], chain);
        }
        return IsCustomInspectorObjectCore(type, chain);
    }

    public static void ValidateType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!IsSupportedType(type))
            throw new InvalidDataException($"Unsupported Inspector value type: {type.FullName}");
    }

    private static bool IsSupportedElementCore(Type type, HashSet<Type> chain)
    {
        if (type == typeof(string))
            return true;
        if (type == typeof(int) || type == typeof(float) || type == typeof(double) || type == typeof(bool))
            return true;
        if (type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4) || type == typeof(Quaternion))
            return true;
        if (type == typeof(Sprite))
            return true;
        if (type.IsEnum)
            return true;
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
            return underlying == typeof(int) || underlying == typeof(float) || underlying == typeof(double) || underlying == typeof(bool)
                || underlying.IsEnum;
        return IsCustomInspectorObjectCore(type, chain);
    }

    /// <summary>
    /// 組み込み変換ではなく <c>[Inspector]</c> メンバーの入れ子として扱う自作クラスかどうか。
    /// Editorはこの判定で入れ子エディタを出す。派生型の代入は扱わず、宣言型と実行時型の一致を要求する。
    /// </summary>
    /// <remarks>
    /// 条件：参照型のclass（string・配列・List・Dictionary・Nullable・enum・Transform・Spriteを除く）、
    /// 抽象・ジェネリック・struct・object自体を除き、publicな引数なしコンストラクタを持ち、
    /// すべての <c>[Inspector]</c> メンバーが対応型であること。再帰（自分を直接・間接に含む）は未対応。
    /// </remarks>
    public static bool IsCustomInspectorObject(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return IsCustomInspectorObjectCore(type, []);
    }

    private static bool IsCustomInspectorObjectCore(Type type, HashSet<Type> chain)
    {
        if (!IsCustomObjectShape(type))
            return false;
        if (!chain.Add(type))
            return false; // 自分を含む再帰型はYAML・Clone・Inspectorのいずれでも有限に扱えない。
        try
        {
            foreach (var member in ComponentSchema.GetInspectorMembers(type))
            {
                if (!IsSupportedTypeCore(MemberType(member), chain))
                    return false;
            }
            return true;
        }
        finally
        {
            chain.Remove(type);
        }
    }

    /// <summary>完全な対応判定なしで、自作クラスの形だけを見る。変換本体の分岐用。検証は呼び出し側が行う。</summary>
    private static bool IsCustomObjectShape(Type type)
    {
        if (type == typeof(object) || type == typeof(string))
            return false;
        if (!type.IsClass || type.IsAbstract || type.IsValueType || type.IsEnum)
            return false;
        if (type.IsArray || type.IsGenericType)
            return false;
        if (type == typeof(Transform) || type == typeof(Sprite))
            return false;
        return type.GetConstructor(Type.EmptyTypes) is not null;
    }

    private static Type MemberType(MemberInfo member) => member is FieldInfo field
        ? field.FieldType : ((PropertyInfo)member).PropertyType;

    private static object? GetObjectMember(object owner, MemberInfo member) => member switch
    {
        FieldInfo field => field.GetValue(owner),
        PropertyInfo property => property.GetValue(owner),
        _ => throw new NotSupportedException($"Unsupported member: {member.Name}"),
    };

    private static void SetObjectMember(object owner, MemberInfo member, object? value)
    {
        switch (member)
        {
            case FieldInfo field: field.SetValue(owner, value); break;
            case PropertyInfo property: property.SetValue(owner, value); break;
            default: throw new NotSupportedException($"Unsupported member: {member.Name}");
        }
    }

    /// <summary>Capture用にYAMLへ安定して書ける形へ変換する。参照型は深く複製する。</summary>
    public static object? ToStorable(object? value, Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        ValidateType(type);
        return ToStorableCore(value, type, []);
    }

    private static object? ToStorableCore(object? value, Type type, List<object> seen)
    {
        if (value is null)
        {
            if (type == typeof(string) || type == typeof(Transform) || type.IsArray
                || (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(List<>) || type.GetGenericTypeDefinition() == typeof(Dictionary<,>)))
                || Nullable.GetUnderlyingType(type) is not null || !type.IsValueType)
                return null;
            throw new InvalidDataException($"Null is not allowed for {type.FullName}.");
        }
        var nullableUnderlying = Nullable.GetUnderlyingType(type);
        if (nullableUnderlying is not null)
            return ToStorableCore(value, nullableUnderlying, seen);
        if (type == typeof(string))
        {
            if (value is not string)
                throw new InvalidDataException($"Invalid string value: {value.GetType().FullName}.");
            return value;
        }
        if (type == typeof(int))
        {
            if (value.GetType() == typeof(int))
                return value;
            throw new InvalidDataException($"Invalid int value: {value.GetType().FullName}.");
        }
        if (type == typeof(float))
        {
            if (value is not float number)
                throw new InvalidDataException($"Invalid float value: {value.GetType().FullName}.");
            if (!float.IsFinite(number))
                throw new InvalidDataException("A finite number is required.");
            return number;
        }
        if (type == typeof(double))
        {
            if (value is not double number)
                throw new InvalidDataException($"Invalid double value: {value.GetType().FullName}.");
            if (!double.IsFinite(number))
                throw new InvalidDataException("A finite number is required.");
            return number;
        }
        if (type == typeof(bool))
        {
            if (value.GetType() == typeof(bool))
                return value;
            throw new InvalidDataException($"Invalid bool value: {value.GetType().FullName}.");
        }
        if (type.IsEnum)
        {
            if (value.GetType() != type)
                throw new InvalidDataException($"Invalid enum value: {value.GetType().FullName}.");
            return value.ToString();
        }
        if (type == typeof(Vector2))
        {
            if (value is not Vector2 vector)
                throw new InvalidDataException($"Invalid Vector2 value: {value.GetType().FullName}.");
            if (!float.IsFinite(vector.X) || !float.IsFinite(vector.Y))
                throw new InvalidDataException("A finite number is required.");
            return new Dictionary<string, object?> { ["x"] = vector.X, ["y"] = vector.Y };
        }
        if (type == typeof(Vector3))
        {
            if (value is not Vector3 vector)
                throw new InvalidDataException($"Invalid Vector3 value: {value.GetType().FullName}.");
            if (!float.IsFinite(vector.X) || !float.IsFinite(vector.Y) || !float.IsFinite(vector.Z))
                throw new InvalidDataException("A finite number is required.");
            return new Dictionary<string, object?> { ["x"] = vector.X, ["y"] = vector.Y, ["z"] = vector.Z };
        }
        if (type == typeof(Vector4))
        {
            if (value is not Vector4 vector)
                throw new InvalidDataException($"Invalid Vector4 value: {value.GetType().FullName}.");
            if (!float.IsFinite(vector.X) || !float.IsFinite(vector.Y) || !float.IsFinite(vector.Z) || !float.IsFinite(vector.W))
                throw new InvalidDataException("A finite number is required.");
            return new Dictionary<string, object?> { ["x"] = vector.X, ["y"] = vector.Y, ["z"] = vector.Z, ["w"] = vector.W };
        }
        if (type == typeof(Quaternion))
        {
            if (value is not Quaternion quaternion)
                throw new InvalidDataException($"Invalid Quaternion value: {value.GetType().FullName}.");
            if (!float.IsFinite(quaternion.X) || !float.IsFinite(quaternion.Y) || !float.IsFinite(quaternion.Z) || !float.IsFinite(quaternion.W))
                throw new InvalidDataException("A finite number is required.");
            return new Dictionary<string, object?> { ["x"] = quaternion.X, ["y"] = quaternion.Y, ["z"] = quaternion.Z, ["w"] = quaternion.W };
        }
        if (type == typeof(Transform))
        {
            if (value is not Transform transform)
                throw new InvalidDataException($"Invalid Transform value: {value.GetType().FullName}.");
            return new Dictionary<string, object?>
            {
                ["LocalPosition"] = ToStorableCore(transform.LocalPosition, typeof(Vector3), seen),
                ["LocalRotation"] = ToStorableCore(transform.LocalRotation, typeof(Quaternion), seen),
                ["LocalScale"] = ToStorableCore(transform.LocalScale, typeof(Vector3), seen),
            };
        }
        if (type == typeof(Sprite))
        {
            if (value is not Sprite sprite)
                throw new InvalidDataException($"Invalid Sprite value: {value.GetType().FullName}.");
            var storable = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["imageId"] = sprite.ImageId.ToString("D"),
            };
            if (sprite.SourceRect is { } rect)
                storable.Add("sourceRect", new Dictionary<string, object?>
                {
                    ["x"] = rect.X,
                    ["y"] = rect.Y,
                    ["width"] = rect.Width,
                    ["height"] = rect.Height,
                });
            return storable;
        }
        if (type.IsArray)
        {
            var elementType = type.GetElementType()!;
            if (value.GetType() != type)
                throw new InvalidDataException($"Invalid array value: {value.GetType().FullName}.");
            var array = (Array)value;
            var storable = new List<object?>(array.Length);
            foreach (var element in array)
                storable.Add(ToStorableCore(element, elementType, seen));
            return storable;
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            var elementType = type.GetGenericArguments()[0];
            if (value.GetType() != type)
                throw new InvalidDataException($"Invalid List value: {value.GetType().FullName}.");
            var storable = new List<object?>();
            foreach (var element in (IEnumerable)value)
                storable.Add(ToStorableCore(element, elementType, seen));
            return storable;
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            var valueType = type.GetGenericArguments()[1];
            if (value.GetType() != type)
                throw new InvalidDataException($"Invalid Dictionary value: {value.GetType().FullName}.");
            var storable = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in (IDictionary)value)
            {
                if (entry.Key is not string key)
                    throw new InvalidDataException("Dictionary keys must be strings.");
                storable.Add(key, ToStorableCore(entry.Value, valueType, seen));
            }
            return storable;
        }
        if (IsCustomObjectShape(type))
            return ToStorableObject(value, type, seen);
        throw new InvalidDataException($"Unsupported Inspector value type: {type.FullName}");
    }

    /// <summary>自作クラスをメンバー名→保存形の対応へ変換する。派生型の混入と循環参照を拒否する。</summary>
    private static Dictionary<string, object?> ToStorableObject(object value, Type type, List<object> seen)
    {
        if (value.GetType() != type)
            throw new InvalidDataException($"Invalid {type.Name} value: {value.GetType().FullName}.");
        // 参照同一性で循環だけを検出する。値型のボックス化は毎回別参照になるため誤検出しない。
        if (seen.Contains(value, ReferenceEqualityComparer.Instance))
            throw new InvalidDataException($"Cyclic Inspector value: {type.FullName}.");
        seen.Add(value);
        try
        {
            var mapping = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var member in ComponentSchema.GetInspectorMembers(type))
            {
                var memberType = MemberType(member);
                mapping.Add(member.Name, ToStorableCore(GetObjectMember(value, member), memberType, seen));
            }
            return mapping;
        }
        finally
        {
            seen.Remove(value);
        }
    }

    /// <summary>Clone経路の型付き値とYAML経路の文字列・コレクションの両方から復元する。</summary>
    public static object? FromStorable(object? raw, Type type, string path)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ValidateType(type);
        if (raw is null)
        {
            if (type == typeof(string) || type == typeof(Transform) || type.IsArray
                || (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(List<>) || type.GetGenericTypeDefinition() == typeof(Dictionary<,>)))
                || Nullable.GetUnderlyingType(type) is not null || !type.IsValueType)
                return null;
            throw new InvalidDataException($"{path}: null is not allowed.");
        }
        var nullableUnderlying = Nullable.GetUnderlyingType(type);
        if (nullableUnderlying is not null)
            return FromStorable(raw, nullableUnderlying, path);
        if (type == typeof(string))
        {
            if (raw is string text)
                return text;
            throw new InvalidDataException($"{path}: invalid string value.");
        }
        if (type == typeof(int))
            return ReadInt(raw, path);
        if (type == typeof(float))
            return ReadFloat(raw, path);
        if (type == typeof(double))
            return ReadDouble(raw, path);
        if (type == typeof(bool))
        {
            if (raw.GetType() == typeof(bool))
                return raw;
            if (raw is string text && bool.TryParse(text, out var boolean))
                return boolean;
            throw new InvalidDataException($"{path}: invalid bool value.");
        }
        if (type.IsEnum)
            return ReadEnum(raw, type, path);
        if (type == typeof(Vector2))
        {
            if (raw.GetType() == typeof(Vector2))
                return raw;
            var mapping = ToStringKeyedMapping(raw, path);
            RequireKeys(mapping, ["x", "y"], path);
            return new Vector2(ReadFloat(mapping["x"], $"{path}.x"), ReadFloat(mapping["y"], $"{path}.y"));
        }
        if (type == typeof(Vector3))
        {
            if (raw.GetType() == typeof(Vector3))
                return raw;
            var mapping = ToStringKeyedMapping(raw, path);
            RequireKeys(mapping, ["x", "y", "z"], path);
            return new Vector3(ReadFloat(mapping["x"], $"{path}.x"), ReadFloat(mapping["y"], $"{path}.y"), ReadFloat(mapping["z"], $"{path}.z"));
        }
        if (type == typeof(Vector4))
        {
            if (raw.GetType() == typeof(Vector4))
                return raw;
            var mapping = ToStringKeyedMapping(raw, path);
            RequireKeys(mapping, ["x", "y", "z", "w"], path);
            return new Vector4(ReadFloat(mapping["x"], $"{path}.x"), ReadFloat(mapping["y"], $"{path}.y"), ReadFloat(mapping["z"], $"{path}.z"), ReadFloat(mapping["w"], $"{path}.w"));
        }
        if (type == typeof(Quaternion))
        {
            if (raw.GetType() == typeof(Quaternion))
                return raw;
            var mapping = ToStringKeyedMapping(raw, path);
            RequireKeys(mapping, ["x", "y", "z", "w"], path);
            var quaternion = new Quaternion(ReadFloat(mapping["x"], $"{path}.x"), ReadFloat(mapping["y"], $"{path}.y"), ReadFloat(mapping["z"], $"{path}.z"), ReadFloat(mapping["w"], $"{path}.w"));
            if (!float.IsFinite(quaternion.X) || !float.IsFinite(quaternion.Y) || !float.IsFinite(quaternion.Z) || !float.IsFinite(quaternion.W))
                throw new InvalidDataException($"{path}: a finite number is required.");
            return quaternion;
        }
        if (type == typeof(Transform))
        {
            if (raw is Transform existing)
                return new Transform { LocalPosition = existing.LocalPosition, LocalRotation = existing.LocalRotation, LocalScale = existing.LocalScale };
            var mapping = ToStringKeyedMapping(raw, path);
            RequireKeys(mapping, ["LocalPosition", "LocalRotation", "LocalScale"], path);
            return new Transform
            {
                LocalPosition = (Vector3)FromStorable(mapping["LocalPosition"], typeof(Vector3), $"{path}.LocalPosition")!,
                LocalRotation = (Quaternion)FromStorable(mapping["LocalRotation"], typeof(Quaternion), $"{path}.LocalRotation")!,
                LocalScale = (Vector3)FromStorable(mapping["LocalScale"], typeof(Vector3), $"{path}.LocalScale")!,
            };
        }
        if (type == typeof(Sprite))
        {
            if (raw is Sprite existingSprite)
                return new Sprite(existingSprite.ImageId, existingSprite.SourceRect);
            var spriteMapping = ToStringKeyedMapping(raw, path);
            if (!spriteMapping.TryGetValue("imageId", out var imageRaw) || spriteMapping.Count is not (1 or 2))
                throw new InvalidDataException($"{path}: requires imageId and optional sourceRect.");
            if (spriteMapping.Count == 2 && !spriteMapping.ContainsKey("sourceRect"))
                throw new InvalidDataException($"{path}: requires imageId and optional sourceRect.");
            if (imageRaw is not string imageText || !Guid.TryParse(imageText, out var imageId) || imageId == Guid.Empty)
                throw new InvalidDataException($"{path}.imageId: a non-empty image ID is required.");
            (int X, int Y, int Width, int Height)? sourceRect = null;
            if (spriteMapping.TryGetValue("sourceRect", out var rectRaw) && rectRaw is not null)
            {
                var rectMapping = ToStringKeyedMapping(rectRaw, $"{path}.sourceRect");
                RequireKeys(rectMapping, ["x", "y", "width", "height"], $"{path}.sourceRect");
                sourceRect = (ReadInt(rectMapping["x"], $"{path}.sourceRect.x"),
                    ReadInt(rectMapping["y"], $"{path}.sourceRect.y"),
                    ReadInt(rectMapping["width"], $"{path}.sourceRect.width"),
                    ReadInt(rectMapping["height"], $"{path}.sourceRect.height"));
            }
            try
            {
                return new Sprite(imageId, sourceRect);
            }
            catch (Exception error) when (error is ArgumentException or ArgumentOutOfRangeException)
            {
                throw new InvalidDataException($"{path}: invalid Sprite region.", error);
            }
        }
        if (type.IsArray)
        {
            var elementType = type.GetElementType()!;
            var items = ToSequence(raw, path);
            var array = Array.CreateInstance(elementType, items.Count);
            for (var i = 0; i < items.Count; i++)
                array.SetValue(FromStorable(items[i], elementType, $"{path}[{i}]"), i);
            return array;
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            var elementType = type.GetGenericArguments()[0];
            var items = ToSequence(raw, path);
            var list = (IList)Activator.CreateInstance(type)!;
            for (var i = 0; i < items.Count; i++)
                list.Add(FromStorable(items[i], elementType, $"{path}[{i}]"));
            return list;
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            var valueType = type.GetGenericArguments()[1];
            var mapping = ToStringKeyedMapping(raw, path);
            var dictionary = (IDictionary)Activator.CreateInstance(type)!;
            foreach (var (key, value) in mapping)
                dictionary.Add(key, FromStorable(value, valueType, $"{path}.{key}"));
            return dictionary;
        }
        if (IsCustomObjectShape(type))
            return FromStorableObject(raw, type, path);
        throw new InvalidDataException($"{path}: unsupported type {type.FullName}.");
    }

    /// <summary>自作クラスを対応→新しいインスタンスへ復元する。欠けた項目は初期値を保ち、未知の項目は読み飛ばす。</summary>
    private static object FromStorableObject(object? raw, Type type, string path)
    {
        if (raw is not null && raw.GetType() == type)
        {
            // Clone経路の型付き値：同じ型の新しいインスタンスへ深く複製する。
            var copy = CreateCustomInstance(type, path);
            foreach (var member in ComponentSchema.GetInspectorMembers(type))
            {
                var memberType = MemberType(member);
                SetObjectMember(copy, member, FromStorable(GetObjectMember(raw, member), memberType, $"{path}.{member.Name}"));
            }
            return copy;
        }
        var mapping = ToStringKeyedMapping(raw, path);
        var instance = CreateCustomInstance(type, path);
        foreach (var member in ComponentSchema.GetInspectorMembers(type))
        {
            if (!mapping.TryGetValue(member.Name, out var itemRaw))
                continue; // 後から追加されたメンバーはクラスの初期値を維持する。
            SetObjectMember(instance, member, FromStorable(itemRaw, MemberType(member), $"{path}.{member.Name}"));
        }
        return instance;
    }

    private static object CreateCustomInstance(Type type, string path)
    {
        try
        {
            return Activator.CreateInstance(type)
                ?? throw new InvalidDataException($"{path}: cannot create {type.Name}.");
        }
        catch (Exception error) when (error is not InvalidDataException)
        {
            throw new InvalidDataException($"{path}: cannot create {type.Name}.", error);
        }
    }

    private static int ReadInt(object? raw, string path)
    {
        if (raw is int integer)
            return integer;
        if (raw is string text && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        if (raw is long longValue && longValue >= int.MinValue && longValue <= int.MaxValue)
            return (int)longValue;
        if (raw is double doubleValue && doubleValue == Math.Truncate(doubleValue) && doubleValue >= int.MinValue && doubleValue <= int.MaxValue)
            return (int)doubleValue;
        if (raw is float floatValue && float.IsFinite(floatValue) && floatValue == MathF.Truncate(floatValue) && floatValue >= int.MinValue && floatValue <= int.MaxValue)
            return (int)floatValue;
        throw new InvalidDataException($"{path}: invalid int value.");
    }

    private static float ReadFloat(object? raw, string path)
    {
        if (raw is float finite && float.IsFinite(finite))
            return finite;
        if (raw is string text && float.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed) && float.IsFinite(parsed))
            return parsed;
        if (raw is int integer)
            return integer;
        if (raw is long longValue)
        {
            var converted = (float)longValue;
            if (float.IsFinite(converted))
                return converted;
        }
        if (raw is double doubleValue && double.IsFinite(doubleValue))
        {
            var converted = (float)doubleValue;
            if (float.IsFinite(converted))
                return converted;
        }
        throw new InvalidDataException($"{path}: a finite number is required.");
    }

    private static double ReadDouble(object? raw, string path)
    {
        if (raw is double finite && double.IsFinite(finite))
            return finite;
        if (raw is float floatValue && float.IsFinite(floatValue))
            return floatValue;
        if (raw is string text && double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed))
            return parsed;
        if (raw is int integer)
            return integer;
        if (raw is long longValue)
            return longValue;
        throw new InvalidDataException($"{path}: a finite number is required.");
    }

    private static object ReadEnum(object? raw, Type type, string path)
    {
        if (raw is not null && raw.GetType() == type)
            return raw;
        if (raw is string text)
        {
            if (Enum.TryParse(type, text.Trim(), ignoreCase: true, out var named))
                return named!;
            throw new InvalidDataException($"{path}: invalid {type.Name} value.");
        }
        var underlying = Enum.GetUnderlyingType(type);
        try
        {
            var converted = Convert.ChangeType(raw, underlying, CultureInfo.InvariantCulture);
            return Enum.ToObject(type, converted!);
        }
        catch (Exception error) when (error is InvalidCastException or FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidDataException($"{path}: invalid {type.Name} value.");
        }
    }

    private static Dictionary<string, object?> ToStringKeyedMapping(object? raw, string path)
    {
        if (raw is Dictionary<string, object?> typed)
            return new Dictionary<string, object?>(typed, StringComparer.Ordinal);
        if (raw is IDictionary dictionary)
        {
            var mapping = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key is not string key)
                    throw new InvalidDataException($"{path}: mapping keys must be strings.");
                if (mapping.ContainsKey(key))
                    throw new InvalidDataException($"{path}.{key}: duplicate key.");
                mapping.Add(key, entry.Value);
            }
            return mapping;
        }
        throw new InvalidDataException($"{path}: a mapping is required.");
    }

    private static void RequireKeys(Dictionary<string, object?> mapping, string[] required, string path)
    {
        if (mapping.Count != required.Length || required.Any(key => !mapping.ContainsKey(key)))
            throw new InvalidDataException($"{path}: requires {string.Join(", ", required)}.");
    }

    private static List<object?> ToSequence(object? raw, string path)
    {
        if (raw is List<object?> typed)
            return [.. typed];
        if (raw is IEnumerable enumerable and not string and not IDictionary)
        {
            List<object?> items = [];
            foreach (var element in enumerable)
                items.Add(element);
            return items;
        }
        throw new InvalidDataException($"{path}: a sequence is required.");
    }
}
