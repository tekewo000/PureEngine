using System.Runtime.CompilerServices;

namespace PureEngine.Core;

/// <summary>A lifecycle failure with the original exception and the identity of its component.</summary>
public sealed record SceneRuntimeError(Guid ObjectId, string ObjectName, Type ComponentType,
    string MethodName, Exception Exception);

/// <summary>
/// Single-threaded lifecycle execution over an independent copy of authoring data.
/// Start once, Step explicitly, and Stop/Dispose. Create a new runtime to play again.
/// </summary>
public sealed class SceneRuntime : IDisposable
{
    private sealed class RuntimeObject(SceneObject item)
    {
        public SceneObject Item { get; } = item;
        public List<Invocation> Components { get; } = [];
        public bool Removed;
    }

    private sealed class Invocation(RuntimeObject owner, object component)
    {
        public RuntimeObject Owner { get; } = owner;
        public object Component { get; } = component;
        public ComponentSchema.LifecycleMethods Methods { get; } = ComponentSchema.GetLifecycle(component.GetType());
        public Action? Start;
        public Action? Update;
        public Action<float>? UpdateWithDelta;
        public Action? Destroy;
        public int StartPriority;
        public int UpdatePriority;
        public int DestroyPriority;
        public bool Destroyed;
    }

    private static readonly Comparer<Invocation> StartOrder =
        Comparer<Invocation>.Create(static (a, b) => a.StartPriority.CompareTo(b.StartPriority));
    private static readonly Comparer<Invocation> UpdateOrder =
        Comparer<Invocation>.Create(static (a, b) => a.UpdatePriority.CompareTo(b.UpdatePriority));
    private static readonly Comparer<Invocation> DestroyOrder =
        Comparer<Invocation>.Create(static (a, b) => a.DestroyPriority.CompareTo(b.DestroyPriority));

    private readonly Dictionary<SceneObject, RuntimeObject> _objects = [];
    // Remember lifetime ownership without retaining deleted components for the rest of the session.
    private static readonly object AcceptedComponent = new();
    private readonly ConditionalWeakTable<object, object> _instances = new();
    private readonly List<Invocation> _pendingStarts = [];
    private readonly List<Invocation> _updates = [];
    private readonly List<RuntimeObject> _removals = [];
    private readonly List<SceneRuntimeError> _errors = [];
    private bool _started;
    private bool _stopped;
    private bool _stopRequested;
    private bool _busy;
    private bool _destroying;

    public Scene Scene { get; }
    public bool IsRunning => _started && !_stopRequested && !_stopped;
    public IReadOnlyList<SceneRuntimeError> Errors { get; }

    /// <summary>Validates declarations and copies the current Inspector data; does not call Start.</summary>
    public SceneRuntime(Scene source, ComponentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(registry);
        foreach (var item in source.Objects)
            foreach (var component in item.Components)
                ComponentSchema.GetLifecycle(component.GetType());

        Scene = new SceneSerializer(registry).Clone(source);
        Errors = _errors.AsReadOnly();
        foreach (var item in Scene.Objects)
        {
            RegisterObject(item);
            foreach (var component in item.Components) RegisterComponent(item, component);
            item.Runtime = this;
        }
        Scene.Runtime = this;
    }

    /// <summary>Starts the initial batch. Additions made by its callbacks wait until the next Step.</summary>
    public void Start()
    {
        if (_busy || _started || _stopped)
            throw new InvalidOperationException("A runtime can only be started once.");
        _started = true;
        _busy = true;
        try { StartPending(); }
        finally { FinishStep(); }
    }

    /// <summary>Runs one frame. Delta is finite, non-negative seconds. Reentrant steps are rejected.</summary>
    public void Step(float dt)
    {
        if (_busy || !IsRunning)
            throw new InvalidOperationException("Step requires a running runtime and cannot be nested.");
        if (!float.IsFinite(dt) || dt < 0) throw new ArgumentOutOfRangeException(nameof(dt));
        _busy = true;
        try
        {
            StartPending();
            for (var i = 0; i < _updates.Count && !_stopRequested; i++)
            {
                var entry = _updates[i];
                if (entry.Owner.Removed) continue;
                try
                {
                    if (entry.UpdateWithDelta is not null) entry.UpdateWithDelta(dt);
                    else entry.Update!();
                }
                catch (Exception error)
                {
                    Report(entry, entry.Methods.Update!.Name, error);
                    _stopRequested = true;
                }
            }
        }
        finally { FinishStep(); }
    }

    /// <summary>Stops after the current callback, then destroys every accepted component, even before Start.</summary>
    public void Stop()
    {
        if (_stopped) return;
        _stopRequested = true;
        if (_busy) return;
        _busy = true;
        FinishStep();
    }

    public void Dispose() => Stop();

    internal void EnsureMutationAllowed()
    {
        if (_stopped || _stopRequested || _destroying)
            throw new InvalidOperationException("Cannot add, attach or remove during destruction or after Stop.");
    }

    internal void RegisterObject(SceneObject item)
    {
        EnsureMutationAllowed();
        _objects.Add(item, new RuntimeObject(item));
    }

    internal void RegisterComponent(SceneObject item, object component)
    {
        EnsureMutationAllowed();
        if (!_objects.TryGetValue(item, out var owner) || owner.Removed)
            throw new InvalidOperationException("Cannot attach to a removed object.");
        if (!component.GetType().IsClass || _instances.TryGetValue(component, out _))
            throw new InvalidOperationException("Runtime components must be distinct class instances.");
        // Validate and bind before accepting ownership, so a rejected attach leaves no partial entry.
        var entry = new Invocation(owner, component);
        entry.Start = entry.Methods.Start?.CreateDelegate<Action>(component);
        entry.Destroy = entry.Methods.Destroy?.CreateDelegate<Action>(component);
        if (entry.Methods.Update is { } update)
        {
            if (update.GetParameters().Length == 0) entry.Update = update.CreateDelegate<Action>(component);
            else entry.UpdateWithDelta = update.CreateDelegate<Action<float>>(component);
        }
        var (start, updatePriority, destroy) = item.ReadAttachedPriorities(component);
        entry.StartPriority = start;
        entry.UpdatePriority = updatePriority;
        entry.DestroyPriority = destroy;
        owner.Components.Add(entry);
        _instances.Add(component, AcceptedComponent);
        if (entry.Start is not null || entry.Methods.Update is not null) _pendingStarts.Add(entry);
    }

    internal void SetComponentPriority(SceneObject item, object component, ComponentLifecycle kind, int priority)
    {
        EnsureMutationAllowed();
        if (!_objects.TryGetValue(item, out var owner))
            throw new InvalidOperationException("Component is not part of this runtime.");
        Invocation? found = null;
        foreach (var candidate in owner.Components)
        {
            if (ReferenceEquals(candidate.Component, component))
            {
                found = candidate;
                break;
            }
        }
        if (found is null)
            throw new InvalidOperationException("Component is not part of this runtime.");
        if (kind == ComponentLifecycle.Destroy)
        {
            if (found.Destroyed)
                throw new InvalidOperationException("Destroy Priority can only be changed before Destroy runs.");
            found.DestroyPriority = priority;
            return;
        }
        if (owner.Removed)
            throw new InvalidOperationException($"{kind} Priority can only be changed before removal and the first Start.");
        var pending = false;
        foreach (var candidate in _pendingStarts)
        {
            if (ReferenceEquals(candidate, found))
            {
                pending = true;
                break;
            }
        }
        if (!pending)
            throw new InvalidOperationException($"{kind} Priority can only be changed before the first Start.");
        if (kind == ComponentLifecycle.Start) found.StartPriority = priority;
        else found.UpdatePriority = priority;
    }

    internal bool Remove(SceneObject item)
    {
        EnsureMutationAllowed();
        if (!_objects.TryGetValue(item, out var owner) || owner.Removed) return false;
        owner.Removed = true;
        _removals.Add(owner);
        return true;
    }

    private void StartPending()
    {
        // Snapshot only the count: callbacks append to this list without joining the current batch.
        var count = _pendingStarts.Count;
        // Order the current batch by Start Priority; additions during the batch wait for the next Step.
        if (count > 1) _pendingStarts.Sort(0, count, StartOrder);
        var updatesBefore = _updates.Count;
        for (var i = 0; i < count && !_stopRequested; i++)
        {
            var entry = _pendingStarts[i];
            if (entry.Owner.Removed) continue;
            try { entry.Start?.Invoke(); }
            catch (Exception error)
            {
                Report(entry, entry.Methods.Start!.Name, error);
                _stopRequested = true;
            }
            if (!entry.Owner.Removed && !_stopRequested && entry.Methods.Update is not null) _updates.Add(entry);
        }
        _pendingStarts.RemoveRange(0, count);
        // Keep the whole update list ordered by Update Priority; steady frames without additions skip this.
        if (_updates.Count > updatesBefore && _updates.Count > 1) _updates.Sort(UpdateOrder);
    }

    private void FinishStep()
    {
        try
        {
            if (!_stopRequested && _removals.Count != 0)
            {
                _destroying = true;
                DestroyRemoved();
                _removals.Clear();
                _updates.RemoveAll(static entry => entry.Owner.Removed);
                _pendingStarts.RemoveAll(static entry => entry.Owner.Removed);
                _destroying = false;
            }
            if (_stopRequested)
            {
                _stopped = true;
                _destroying = true;
                DestroyRemaining();
                _objects.Clear();
                _instances.Clear();
                _pendingStarts.Clear();
                _updates.Clear();
                _removals.Clear();
            }
        }
        finally
        {
            _destroying = false;
            _busy = false;
        }
    }

    private void DestroyRemoved()
    {
        // Order the whole frame's deletions by Destroy Priority across objects.
        var targets = new List<Invocation>();
        foreach (var owner in _removals)
            targets.AddRange(owner.Components);
        if (targets.Count > 1) targets.Sort(DestroyOrder);
        foreach (var entry in targets)
        {
            if (entry.Destroyed) continue;
            entry.Destroyed = true;
            try { entry.Destroy?.Invoke(); }
            catch (Exception error) { Report(entry, entry.Methods.Destroy!.Name, error); }
        }
        foreach (var owner in _removals)
        {
            Scene.RemoveImmediately(owner.Item);
            _objects.Remove(owner.Item);
        }
    }

    private void DestroyRemaining()
    {
        // Stop destroys everything still accepted, ordered by Destroy Priority across objects.
        var targets = new List<Invocation>();
        foreach (var owner in _objects.Values)
            targets.AddRange(owner.Components);
        if (targets.Count > 1) targets.Sort(DestroyOrder);
        foreach (var entry in targets)
        {
            if (entry.Destroyed) continue;
            entry.Destroyed = true;
            try { entry.Destroy?.Invoke(); }
            catch (Exception error) { Report(entry, entry.Methods.Destroy!.Name, error); }
        }
        foreach (var owner in _objects.Values)
            Scene.RemoveImmediately(owner.Item);
    }

    private void Report(Invocation entry, string method, Exception error) =>
        _errors.Add(new SceneRuntimeError(entry.Owner.Item.Id, entry.Owner.Item.Name,
            entry.Component.GetType(), method, error));
}
