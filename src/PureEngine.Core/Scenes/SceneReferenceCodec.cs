using System.Collections;
using System.Reflection;

namespace PureEngine.Core;

/// <summary>
/// Persists references in scalar slots, arrays, lists, dictionaries, and nested values.
/// Runs only at preparation, save, and clone boundaries, using caller-owned identity indexes.
/// </summary>
internal static class SceneReferenceCodec
{
    internal static object? Encode(
        object? value,
        Type declaredType,
        ComponentRegistry registry,
        Guid ownerId,
        string path,
        Dictionary<object, Guid> componentToId,
        Scene scene,
        bool forSave)
    {
        if (SceneReferenceTypes.IsSingleReference(declaredType, registry))
            return EncodeSingle(value, declaredType, ownerId, path, componentToId, scene, forSave);
        if (value is null)
        {
            RequireNullable(declaredType, path);
            return null;
        }
        declaredType = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        if (!SceneReferenceTypes.ContainsReference(declaredType, registry))
            return InspectorValueTypes.ToStorable(value, declaredType);
        if (declaredType.IsArray)
            return InspectorArrayShape.Capture((Array)value, path, (element, elementPath) =>
                Encode(element, declaredType.GetElementType()!, registry, ownerId, elementPath, componentToId, scene, forSave));
        if (declaredType.IsGenericType && declaredType.GetGenericTypeDefinition() == typeof(List<>))
            return EncodeSequence(value, declaredType.GetGenericArguments()[0], registry, ownerId, path, componentToId, scene, forSave);
        if (declaredType.IsGenericType && declaredType.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            return EncodeMapping(value, declaredType.GetGenericArguments()[1], registry, ownerId, path, componentToId, scene, forSave);
        return EncodeCustom(value, declaredType, registry, ownerId, path, componentToId, scene, forSave);
    }

    private static void RequireNullable(Type type, string path)
    {
        if (type.IsValueType && Nullable.GetUnderlyingType(type) is null)
            throw new InvalidDataException($"{path}: null is not allowed.");
    }

    private static Dictionary<string, object?>? EncodeSingle(
        object? value,
        Type declaredType,
        Guid ownerId,
        string path,
        Dictionary<object, Guid> componentToId,
        Scene scene,
        bool forSave)
    {
        if (value is not null)
        {
            if (PrefabReferenceStore.TryGetIdentity(value, out var prefab))
                return EncodePrefab(prefab!.PrefabId, prefab.TargetId);
            var targetId = ResolveTargetId(value, declaredType, componentToId, scene);
            scene.References.ClearMissing(ownerId, path);
            scene.References.ClearLegacy(ownerId, path);
            return new Dictionary<string, object?> { ["ref"] = targetId.ToString("D") };
        }
        if (scene.References.TryGetMissing(ownerId, path, out var missing))
            return scene.References.TryGetMissingPrefab(ownerId, path, out var prefabId)
                ? EncodePrefab(prefabId, missing)
                : new Dictionary<string, object?> { ["ref"] = missing.ToString("D") };
        if (scene.References.TryGetLegacy(ownerId, path, out _))
        {
            if (forSave)
                throw new InvalidDataException($"{path}: legacy inline values must be reassigned or explicitly discarded before saving.");
            return null;
        }
        return null;
    }

    private static Dictionary<string, object?> EncodePrefab(Guid prefabId, Guid targetId) =>
        new() { ["prefab"] = prefabId.ToString("D"), ["target"] = targetId.ToString("D") };

    private static Guid ResolveTargetId(
        object value,
        Type declaredType,
        Dictionary<object, Guid> componentToId,
        Scene scene)
    {
        if (DataAssetStore.IsAssetType(declaredType))
        {
            if (!declaredType.IsInstanceOfType(value) || !scene.DataAssets.TryGetId(value, out var assetId))
                throw new InvalidDataException("Asset reference must belong to the scene's asset snapshot.");
            return assetId;
        }
        if (declaredType == typeof(SceneObject))
        {
            if (value is not SceneObject target)
                throw new InvalidDataException($"Invalid SceneObject reference: {value.GetType().FullName}.");
            if (!ReferenceEquals(target.OwnerScene, scene))
                throw new InvalidDataException("Reference target must belong to the same scene.");
            if (!declaredType.IsAssignableFrom(target.GetType()))
                throw new InvalidDataException($"Reference type mismatch: {target.GetType().FullName}.");
            return target.Id;
        }
        var actualType = value.GetType();
        if (!declaredType.IsAssignableFrom(actualType))
            throw new InvalidDataException($"Reference type mismatch: {actualType.FullName} is not assignable to {declaredType.FullName}.");
        if (!componentToId.TryGetValue(value, out var id))
            throw new InvalidDataException("Reference target is not attached to the same scene.");
        return id;
    }

    private static List<object?>? EncodeSequence(
        object? value,
        Type elementType,
        ComponentRegistry registry,
        Guid ownerId,
        string path,
        Dictionary<object, Guid> componentToId,
        Scene scene,
        bool forSave)
    {
        if (value is null)
            return null;
        var list = value is IEnumerable enumerable ? enumerable.Cast<object?>().ToList() : throw new InvalidDataException("Invalid sequence value.");
        List<object?> storable = [];
        for (var i = 0; i < list.Count; i++)
        {
            var elementPath = $"{path}[{i}]";
            var element = list[i];
            storable.Add(Encode(element, elementType, registry, ownerId, elementPath, componentToId, scene, forSave));
        }
        return storable;
    }

    private static Dictionary<string, object?>? EncodeMapping(
        object? value,
        Type valueType,
        ComponentRegistry registry,
        Guid ownerId,
        string path,
        Dictionary<object, Guid> componentToId,
        Scene scene,
        bool forSave)
    {
        if (value is null)
            return null;
        if (value is not IDictionary dictionary)
            throw new InvalidDataException("Invalid dictionary value.");
        var storable = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Key is not string key)
                throw new InvalidDataException("Dictionary keys must be strings.");
            var elementPath = SceneReferenceStore.DictionaryPath(path, key);
            var element = entry.Value;
            storable.Add(key, Encode(element, valueType, registry, ownerId, elementPath, componentToId, scene, forSave));
        }
        return storable;
    }

    private static Dictionary<string, object?> EncodeCustom(
        object value,
        Type declaredType,
        ComponentRegistry registry,
        Guid ownerId,
        string path,
        Dictionary<object, Guid> componentToId,
        Scene scene,
        bool forSave)
    {
        if (value.GetType() != declaredType)
            throw new InvalidDataException($"Invalid {declaredType.Name} value: {value.GetType().FullName}.");
        var mapping = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var member in ComponentSchema.GetInspectorMembers(declaredType))
        {
            var memberType = MemberType(member);
            var memberPath = $"{path}.{member.Name}";
            var memberValue = GetValue(value, member);
            if (SceneReferenceTypes.IsSingleReference(memberType, registry))
            {
                mapping.Add(member.Name, EncodeSingle(memberValue, memberType, ownerId, memberPath, componentToId, scene, forSave));
            }
            else if (SceneReferenceTypes.ContainsReference(memberType, registry))
            {
                mapping.Add(member.Name, Encode(memberValue, memberType, registry, ownerId, memberPath, componentToId, scene, forSave));
            }
            else
            {
                mapping.Add(member.Name, InspectorValueTypes.ToStorable(memberValue, memberType));
            }
        }
        return mapping;
    }

    private static Type MemberType(MemberInfo member) => member is FieldInfo field
        ? field.FieldType : ((PropertyInfo)member).PropertyType;

    private static object? GetValue(object owner, MemberInfo member) => member switch
    {
        FieldInfo field => field.GetValue(owner),
        PropertyInfo property => property.GetValue(owner),
        _ => throw new NotSupportedException($"Unsupported member: {member.Name}"),
    };

    internal static object? Decode(
        object? raw,
        Type declaredType,
        ComponentRegistry registry,
        Guid ownerId,
        string path,
        Dictionary<Guid, SceneObject> objectsById,
        Dictionary<Guid, object> componentsById,
        Scene scene,
        int version,
        string displayPath,
        ref bool membersChanged)
    {
        if (SceneReferenceTypes.IsSingleReference(declaredType, registry))
            return DecodeSingle(raw, declaredType, registry, ownerId, path, objectsById, componentsById, scene, version, displayPath, ref membersChanged);
        if (raw is null)
        {
            RequireNullable(declaredType, displayPath);
            return null;
        }
        declaredType = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        if (!SceneReferenceTypes.ContainsReference(declaredType, registry))
            return InspectorValueTypes.FromStorable(raw, declaredType, displayPath);
        if (raw.GetType() == declaredType)
            return DecodeLiveContainer(raw, declaredType, registry, ownerId, path, objectsById, componentsById, scene, displayPath);
        if (declaredType.IsArray)
        {
            var changed = membersChanged;
            var array = InspectorArrayShape.Restore(raw, declaredType, path, (element, elementPath) =>
                Decode(element, declaredType.GetElementType()!, registry, ownerId, elementPath, objectsById, componentsById,
                    scene, version, displayPath + elementPath[path.Length..], ref changed));
            membersChanged = changed;
            return array;
        }
        if (declaredType.IsGenericType && declaredType.GetGenericTypeDefinition() == typeof(List<>))
            return DecodeSequence(raw, declaredType.GetGenericArguments()[0], registry, ownerId, path, objectsById, componentsById, scene, version, displayPath, declaredType, ref membersChanged);
        if (declaredType.IsGenericType && declaredType.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            return DecodeMapping(raw, declaredType.GetGenericArguments()[1], registry, ownerId, path, objectsById, componentsById, scene, version, displayPath, declaredType, ref membersChanged);
        return DecodeCustom(raw, declaredType, registry, ownerId, path, objectsById, componentsById, scene, version, displayPath, ref membersChanged);
    }

    private static object? DecodeSingle(
        object? raw,
        Type declaredType,
        ComponentRegistry registry,
        Guid ownerId,
        string path,
        Dictionary<Guid, SceneObject> objectsById,
        Dictionary<Guid, object> componentsById,
        Scene scene,
        int version,
        string displayPath,
        ref bool membersChanged)
    {
        if (raw is null)
            return null;
        if (raw is IDictionary mapping && mapping.Contains("prefab"))
        {
            if (mapping.Count != 2 || mapping["prefab"] is not string prefabText || !Guid.TryParse(prefabText, out var prefabId)
                || prefabId == Guid.Empty || mapping["target"] is not string targetText || !Guid.TryParse(targetText, out var prefabTargetId)
                || prefabTargetId == Guid.Empty || DataAssetStore.IsAssetType(declaredType))
                throw new InvalidDataException($"{displayPath}: a valid prefab and target identity is required.");
            var target = scene.Prefabs.Resolve(prefabId, prefabTargetId, declaredType, registry, scene.DataAssets);
            if (target is null) scene.References.SetMissingPrefab(ownerId, path, prefabId, prefabTargetId);
            return target;
        }
        if (TryReadRef(raw, out var targetId))
        {
            if (targetId == Guid.Empty)
                throw new InvalidDataException($"{displayPath}: reference ID must not be empty.");
            if (DataAssetStore.IsAssetType(declaredType))
            {
                var asset = scene.DataAssets.Find(targetId, declaredType);
                if (asset is null) scene.References.SetMissing(ownerId, path, targetId);
                return asset;
            }
            if (declaredType == typeof(SceneObject))
            {
                if (objectsById.TryGetValue(targetId, out var targetObject))
                    return targetObject;
                if (componentsById.ContainsKey(targetId))
                    throw new InvalidDataException($"{displayPath}: reference type mismatch.");
                scene.References.SetMissing(ownerId, path, targetId);
                return null;
            }
            if (componentsById.TryGetValue(targetId, out var targetComponent))
            {
                if (!declaredType.IsAssignableFrom(targetComponent.GetType()))
                    throw new InvalidDataException($"{displayPath}: reference type mismatch.");
                return targetComponent;
            }
            if (objectsById.ContainsKey(targetId))
                throw new InvalidDataException($"{displayPath}: reference type mismatch.");
            scene.References.SetMissing(ownerId, path, targetId);
            return null;
        }
        if (version is 1 or 2)
        {
            scene.References.SetLegacy(ownerId, path, raw);
            membersChanged = true;
            return null;
        }
        throw new InvalidDataException($"{displayPath}: a reference {{ ref: <id> }} or null is required.");
    }

    private static object? DecodeSequence(
        object? raw,
        Type elementType,
        ComponentRegistry registry,
        Guid ownerId,
        string path,
        Dictionary<Guid, SceneObject> objectsById,
        Dictionary<Guid, object> componentsById,
        Scene scene,
        int version,
        string displayPath,
        Type declaredType,
        ref bool membersChanged)
    {
        if (raw is null)
            return null;
        var items = ToSequence(raw, displayPath);
        var list = (IList)Activator.CreateInstance(declaredType)!;
        for (var i = 0; i < items.Count; i++)
        {
            var elementPath = $"{path}[{i}]";
            var elementDisplay = $"{displayPath}[{i}]";
            list.Add(Decode(items[i], elementType, registry, ownerId, elementPath, objectsById, componentsById, scene, version, elementDisplay, ref membersChanged));
        }
        return list;
    }

    private static object? DecodeMapping(
        object? raw,
        Type valueType,
        ComponentRegistry registry,
        Guid ownerId,
        string path,
        Dictionary<Guid, SceneObject> objectsById,
        Dictionary<Guid, object> componentsById,
        Scene scene,
        int version,
        string displayPath,
        Type declaredType,
        ref bool membersChanged)
    {
        if (raw is null)
            return null;
        var mapping = ToStringKeyedMapping(raw, displayPath);
        var dictionary = (IDictionary)Activator.CreateInstance(declaredType)!;
        foreach (var (key, value) in mapping)
        {
            var elementPath = SceneReferenceStore.DictionaryPath(path, key);
            var elementDisplay = $"{displayPath}.{key}";
            dictionary.Add(key, Decode(value, valueType, registry, ownerId, elementPath, objectsById, componentsById, scene, version, elementDisplay, ref membersChanged));
        }
        return dictionary;
    }

    private static object DecodeCustom(
        object? raw,
        Type declaredType,
        ComponentRegistry registry,
        Guid ownerId,
        string path,
        Dictionary<Guid, SceneObject> objectsById,
        Dictionary<Guid, object> componentsById,
        Scene scene,
        int version,
        string displayPath,
        ref bool membersChanged)
    {
        var map = ToStringKeyedMapping(raw, displayPath);
        var names = ComponentSchema.GetInspectorMemberNames(declaredType);
        Dictionary<MemberInfo, object?> values = [];
        foreach (var (name, value) in map)
        {
            if (!names.TryGetValue(name, out var member))
            {
                membersChanged = true;
                continue;
            }
            if (name != member.Name) membersChanged = true;
            if (!values.TryAdd(member, value))
                throw new InvalidDataException($"{displayPath}.{member.Name}: multiple saved names refer to the same Inspector member.");
        }
        var instance = Activator.CreateInstance(declaredType)
            ?? throw new InvalidDataException($"{displayPath}: cannot create {declaredType.Name}.");
        foreach (var member in ComponentSchema.GetInspectorMembers(declaredType))
        {
            if (!values.TryGetValue(member, out var itemRaw))
            {
                membersChanged = true;
                continue;
            }
            var memberType = MemberType(member);
            var memberPath = $"{path}.{member.Name}";
            var memberDisplay = $"{displayPath}.{member.Name}";
            object? resolved;
            if (SceneReferenceTypes.IsSingleReference(memberType, registry))
                resolved = DecodeSingle(itemRaw, memberType, registry, ownerId, memberPath, objectsById, componentsById, scene, version, memberDisplay, ref membersChanged);
            else if (SceneReferenceTypes.ContainsReference(memberType, registry))
                resolved = Decode(itemRaw, memberType, registry, ownerId, memberPath, objectsById, componentsById, scene, version, memberDisplay, ref membersChanged);
            else
                resolved = InspectorValueTypes.FromStorable(itemRaw, memberType, memberDisplay);
            SetValue(instance, member, resolved);
        }
        return instance;
    }

    private static object? ResolveLiveReference(
        object? liveValue,
        Type declaredType,
        Guid ownerId,
        string path,
        Dictionary<Guid, SceneObject> objectsById,
        Dictionary<Guid, object> componentsById,
        Scene scene,
        string displayPath)
    {
        if (liveValue is null)
        {
            if (scene.References.TryGetMissing(ownerId, path, out var missing))
            {
                if (DataAssetStore.IsAssetType(declaredType))
                    return scene.DataAssets.Find(missing, declaredType);
                if (objectsById.TryGetValue(missing, out var targetObject) && declaredType == typeof(SceneObject))
                    return targetObject;
                if (componentsById.TryGetValue(missing, out var targetComponent) && declaredType.IsAssignableFrom(targetComponent.GetType()))
                    return targetComponent;
            }
            return null;
        }
        if (DataAssetStore.IsAssetType(declaredType))
        {
            if (scene.DataAssets.TryGetId(liveValue, out var assetId))
                return scene.DataAssets.Find(assetId, declaredType);
            throw new InvalidDataException($"{displayPath}: asset reference is outside the scene snapshot.");
        }
        if (PrefabReferenceStore.TryGetIdentity(liveValue, out _)) return liveValue;
        if (liveValue is SceneObject liveObject)
        {
            foreach (var (_, candidate) in objectsById)
            {
                if (ReferenceEquals(candidate, liveObject))
                    return candidate;
            }
            throw new InvalidDataException($"{displayPath}: reference target is outside the scene.");
        }
        foreach (var (id, candidate) in componentsById)
        {
            if (ReferenceEquals(candidate, liveValue))
            {
                if (!declaredType.IsAssignableFrom(candidate.GetType()))
                    throw new InvalidDataException($"{displayPath}: reference type mismatch.");
                return candidate;
            }
        }
        throw new InvalidDataException($"{displayPath}: reference target is outside the scene.");
    }

    private static object? DecodeLiveContainer(
        object? liveValue,
        Type declaredType,
        ComponentRegistry registry,
        Guid ownerId,
        string path,
        Dictionary<Guid, SceneObject> objectsById,
        Dictionary<Guid, object> componentsById,
        Scene scene,
        string displayPath)
    {
        if (SceneReferenceTypes.IsSingleReference(declaredType, registry))
            return ResolveLiveReference(liveValue, declaredType, ownerId, path, objectsById, componentsById, scene, displayPath);
        if (liveValue is null)
        {
            RequireNullable(declaredType, displayPath);
            return null;
        }
        declaredType = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        if (!SceneReferenceTypes.ContainsReference(declaredType, registry))
            return CloneCustomValue(liveValue, declaredType, displayPath);
        if (declaredType.IsArray)
        {
            var elementType = declaredType.GetElementType()!;
            var source = (Array)liveValue;
            return InspectorArrayShape.Restore(source, declaredType, path, (element, elementPath) =>
                DecodeLiveContainer(element, elementType, registry, ownerId, elementPath, objectsById, componentsById,
                    scene, displayPath + elementPath[path.Length..]));
        }
        if (declaredType.IsGenericType && declaredType.GetGenericTypeDefinition() == typeof(List<>))
        {
            var elementType = declaredType.GetGenericArguments()[0];
            var copy = (IList)Activator.CreateInstance(declaredType)!;
            foreach (var element in (IEnumerable)liveValue)
            {
                var index = copy.Count;
                var elementPath = $"{path}[{index}]";
                var elementDisplay = $"{displayPath}[{index}]";
                copy.Add(DecodeLiveContainer(element, elementType, registry, ownerId, elementPath, objectsById, componentsById, scene, elementDisplay));
            }
            return copy;
        }
        if (declaredType.IsGenericType && declaredType.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            var valueType = declaredType.GetGenericArguments()[1];
            var copy = (IDictionary)Activator.CreateInstance(declaredType)!;
            foreach (DictionaryEntry entry in (IDictionary)liveValue)
            {
                var key = (string)entry.Key;
                var elementPath = SceneReferenceStore.DictionaryPath(path, key);
                var elementDisplay = $"{displayPath}.{key}";
                var element = entry.Value;
                copy.Add(key, DecodeLiveContainer(element, valueType, registry, ownerId, elementPath, objectsById, componentsById, scene, elementDisplay));
            }
            return copy;
        }
        if (liveValue.GetType() != declaredType)
            throw new InvalidDataException($"{displayPath}: invalid {declaredType.Name} value.");
        var instance = Activator.CreateInstance(declaredType)
            ?? throw new InvalidDataException($"{displayPath}: cannot create {declaredType.Name}.");
        foreach (var member in ComponentSchema.GetInspectorMembers(declaredType))
        {
            var memberType = MemberType(member);
            var memberPath = $"{path}.{member.Name}";
            var memberDisplay = $"{displayPath}.{member.Name}";
            var memberValue = GetValue(liveValue, member);
            var resolved = DecodeLiveContainer(memberValue, memberType, registry, ownerId, memberPath, objectsById, componentsById, scene, memberDisplay);
            SetValue(instance, member, resolved);
        }
        return instance;
    }

    private static object? CloneCustomValue(object? value, Type type, string path)
    {
        if (value is null)
        {
            RequireNullable(type, path);
            return null;
        }
        return InspectorValueTypes.FromStorable(InspectorValueTypes.ToStorable(value, type), type, path);
    }

    private static void SetValue(object owner, MemberInfo member, object? value)
    {
        switch (member)
        {
            case FieldInfo field: field.SetValue(owner, value); break;
            case PropertyInfo property: property.SetValue(owner, value); break;
            default: throw new NotSupportedException($"Unsupported member: {member.Name}");
        }
    }

    private static bool TryReadRef(object? raw, out Guid targetId)
    {
        targetId = Guid.Empty;
        Dictionary<string, object?> mapping;
        if (raw is Dictionary<string, object?> typed)
        {
            mapping = typed;
        }
        else if (raw is IDictionary dictionary)
        {
            mapping = [with(StringComparer.Ordinal)];
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key is not string key)
                    return false;
                if (mapping.ContainsKey(key))
                    return false;
                mapping.Add(key, entry.Value);
            }
        }
        else
        {
            return false;
        }
        if (mapping.Count != 1 || !mapping.TryGetValue("ref", out var refRaw))
            return false;
        if (refRaw is string text && Guid.TryParse(text, out var parsed))
        {
            targetId = parsed;
            return true;
        }
        return false;
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
}
