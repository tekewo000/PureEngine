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
                    Visit(value, prefix + member.Name, ownerId, () =>
                    {
                        if (member is FieldInfo field) field.SetValue(container, null);
                        else ((PropertyInfo)member).SetValue(container, null);
                    });
                }
            }
            finally { chain.Remove(container); }
        }

        void Visit(object? value, string path, Guid ownerId, Action clear)
        {
            if (value is null) return;
            if (removed.TryGetValue(value, out var targetId))
            {
                clear();
                scene.References.SetMissing(ownerId, path, targetId);
                return;
            }
            if (value is SceneObject or string or Sprite or Transform || value.GetType().IsValueType || SceneObject.IsOwned(value))
                return;
            if (value is Array array)
            {
                if (array.Rank != 1) return;
                for (var i = 0; i < array.Length; i++)
                {
                    var index = i;
                    Visit(array.GetValue(index), $"{path}[{index}]", ownerId, () => array.SetValue(null, index));
                }
            }
            else if (value is IList list)
            {
                for (var i = 0; i < list.Count; i++)
                {
                    var index = i;
                    Visit(list[index], $"{path}[{index}]", ownerId, () => list[index] = null);
                }
            }
            else if (value is IDictionary dictionary)
            {
                foreach (var key in dictionary.Keys.OfType<string>().ToArray())
                    Visit(dictionary[key], SceneReferenceStore.DictionaryPath(path, key), ownerId, () => dictionary[key] = null);
            }
            else
            {
                VisitMembers(value, path + ".", ownerId);
            }
        }
    }
}
