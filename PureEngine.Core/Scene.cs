using System.Collections.ObjectModel;

namespace PureEngine.Core;

public sealed class Scene
{
    private readonly ObservableCollection<SceneObject> _objects = [];

    public Scene() => Objects = new ReadOnlyObservableCollection<SceneObject>(_objects);

    public ReadOnlyObservableCollection<SceneObject> Objects { get; }

    public SceneObject AddEmpty()
    {
        var name = "Empty";
        for (var suffix = 1; _objects.Any(item => item.Name == name); suffix++)
            name = $"Empty ({suffix})";

        var item = new SceneObject(name);
        _objects.Add(item);
        return item;
    }

    public bool Remove(SceneObject item) => _objects.Remove(item);
}
