using System.Collections;
using System.Reflection;

namespace PureEngine.Core;

/// <summary>Updates surviving Inspector slots at removal boundaries, never during steady updates.</summary>
internal static class SceneReferenceNuller
{
    internal static void NullReferencesToComponent(Scene scene, object removedComponent, Guid removedId) =>
        NullReferencesToSubtree(scene, [], [(removedComponent, removedId)]);

    internal static void NullReferencesToSubtree(Scene scene, List<SceneObject> removedObjects, List<(object Component, Guid Id)> removedComponents)
    {
        var removed = new Dictionary<object, Guid>(ReferenceEqualityComparer.Instance);
        foreach (var item in removedObjects) removed.Add(item, item.Id);
        foreach (var (component, id) in removedComponents) removed.Add(component, id);
        if (removed.Count == 0) return;

        var chain = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var item in scene.Objects)
        {
            if (removed.ContainsKey(item)) continue;
            foreach (var component in item.Components)
            {
                if (removed.ContainsKey(component)) continue;
                var ownerId = item.GetComponentId(component);
                VisitMembers(component, "", ownerId);
            }
        }

        void VisitMembers(object container, string prefix, Guid ownerId)
        {
            if (!chain.Add(container)) return;
            try
            {
                foreach (var member in ComponentSchema.GetInspectorMembers(container.GetType()))
                {
                    var value = member is FieldInfo field ? field.GetValue(container) : ((PropertyInfo)member).GetValue(container);
                    Visit(value, prefix + member.Name, ownerId, updated =>
                    {
                        if (member is FieldInfo field) field.SetValue(container, updated);
                        else ((PropertyInfo)member).SetValue(container, updated);
                    });
                }
            }
            finally { chain.Remove(container); }
        }

        void Visit(object? value, string path, Guid ownerId, Action<object?> write)
        {
            if (value is null) return;
            if (removed.TryGetValue(value, out var targetId))
            {
                write(null);
                scene.References.SetMissing(ownerId, path, targetId);
                return;
            }
            if (value is SceneObject or string or Sprite or Transform || SceneObject.IsOwned(value)
                || DataAssetStore.IsAssetType(value.GetType()))
                return;
            if (value is Array array)
            {
                foreach (var indices in InspectorArrayShape.Indices(array))
                {
                    Visit(array.GetValue(indices), $"{path}[{string.Join(",", indices)}]", ownerId,
                        updated => array.SetValue(updated, indices));
                }
            }
            else if (value is IList list)
            {
                for (var i = 0; i < list.Count; i++)
                {
                    var index = i;
                    Visit(list[index], $"{path}[{index}]", ownerId, updated => list[index] = updated);
                }
            }
            else if (value is IDictionary dictionary)
            {
                foreach (var key in dictionary.Keys.OfType<string>().ToList())
                    Visit(dictionary[key], SceneReferenceStore.DictionaryPath(path, key), ownerId, updated => dictionary[key] = updated);
            }
            else if (!value.GetType().IsValueType || InspectorValueTypes.IsCustomObjectShape(value.GetType()))
            {
                VisitMembers(value, path + ".", ownerId);
                // Reflection edits a boxed struct; propagate that box through every parent slot.
                if (value.GetType().IsValueType)
                    write(value);
            }
        }
    }
}
