using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PureEngine.Core;

static class RuntimeBenchmarks
{
    public static void Run()
    {
        const int samples = 5, steps = 10_000;
        Console.WriteLine($"Runtime benchmark: {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"Release recommended; {samples} samples (median), {steps:N0} steps/sample, 500ms Step warmup/case. No rendering or game work.");
        Console.WriteLine("Preparation = clone + bind + Start; allocation = thread allocated bytes, not retained memory. Stop and source setup are excluded.");
        Console.WriteLine("objects,updates,first_prep_ms,warm_prep_ms,prep_bytes,step_us,bytes_per_step");
        foreach (var count in new[] { 0, 100, 1_000, 10_000 }) Measure(count, true);
        Measure(10_000, false);
        MeasureReferences();
        MeasureReferenceEdit();

        static void Measure(int count, bool withUpdates)
        {
            var registry = new ComponentRegistry();
            registry.Register<BenchmarkBehaviour>("bench.behaviour");
            registry.Register<BenchmarkData>("bench.data");
            var source = new Scene();
            for (var i = 0; i < count; i++)
            {
                var item = source.AddEmpty();
                item.Rename($"Benchmark {i}");
                item.Attach(withUpdates ? new BenchmarkBehaviour { Value = i } : new BenchmarkData { Value = i });
            }

            var firstStart = Stopwatch.GetTimestamp();
            using var first = new SceneRuntime(source, registry);
            first.Start();
            var firstMs = Stopwatch.GetElapsedTime(firstStart).TotalMilliseconds;
            first.Stop();

            var prepTimes = new double[samples];
            var prepBytes = new long[samples];
            for (var sample = 0; sample < samples; sample++)
            {
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var start = Stopwatch.GetTimestamp();
                using var prepared = new SceneRuntime(source, registry);
                prepared.Start();
                prepTimes[sample] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                prepBytes[sample] = GC.GetAllocatedBytesForCurrentThread() - allocated;
            }

            using var runtime = new SceneRuntime(source, registry);
            runtime.Start();
            var warmup = Stopwatch.StartNew();
            while (warmup.ElapsedMilliseconds < 500) runtime.Step(1f / 60);
            var stepTimes = new double[samples];
            var stepBytes = new long[samples];
            for (var sample = 0; sample < samples; sample++)
            {
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var start = Stopwatch.GetTimestamp();
                for (var step = 0; step < steps; step++) runtime.Step(1f / 60);
                stepTimes[sample] = Stopwatch.GetElapsedTime(start).TotalMicroseconds / steps;
                stepBytes[sample] = GC.GetAllocatedBytesForCurrentThread() - allocated;
            }
            Array.Sort(prepTimes);
            Array.Sort(prepBytes);
            Array.Sort(stepTimes);
            Array.Sort(stepBytes);
            Console.WriteLine(FormattableString.Invariant($"{count},{(withUpdates ? count : 0)},{firstMs:F3},{prepTimes[samples / 2]:F3},{prepBytes[samples / 2]},{stepTimes[samples / 2]:F3},{(double)stepBytes[samples / 2] / steps:F3}"));
            if (runtime.Errors.Count != 0) throw new InvalidOperationException("Benchmark lifecycle failed.");
        }

        static void MeasureReferences()
        {
            const int refSamples = 5, refSteps = 10_000;
            Console.WriteLine("reference steady: objects,refs,prep_ms,prep_bytes,yaml_chars,step_us,bytes_per_step,stop_ms,retained_bytes");
            Console.Out.Flush();
            foreach (var count in new[] { 1_000, 10_000 })
            {
                foreach (var withRefs in new[] { false, true })
                {
                    MeasureRefCase(count, withRefs, refSamples, refSteps);
                }
            }

            static void MeasureRefCase(int count, bool withRefs, int samples, int steps)
            {
                var registry = new ComponentRegistry();
                registry.Register<BenchmarkRefTarget>("bench.ref-target");
                registry.Register<BenchmarkRefHolder>("bench.ref-holder");
                var source = new Scene();
                var targets = new List<BenchmarkRefTarget>(count);
                for (var i = 0; i < count; i++)
                {
                    var targetObject = source.AddEmpty();
                    targetObject.Rename($"Target {i}");
                    var target = new BenchmarkRefTarget { Value = i };
                    targetObject.Attach(target);
                    targets.Add(target);
                }
                for (var i = 0; i < count; i++)
                {
                    var holderObject = source.AddEmpty();
                    holderObject.Rename($"Holder {i}");
                    holderObject.Attach(new BenchmarkRefHolder
                    {
                        Single = withRefs ? targets[i] : null,
                        Value = i,
                    });
                }
                var serializer = new SceneSerializer(registry);
                var yaml = serializer.Serialize(source);
                var prepTimes = new double[samples];
                var prepBytes = new long[samples];
                var stopTimes = new double[samples];
                for (var sample = 0; sample < samples; sample++)
                {
                    var allocated = GC.GetAllocatedBytesForCurrentThread();
                    var start = Stopwatch.GetTimestamp();
                    using var prepared = new SceneRuntime(source, registry);
                    prepared.Start();
                    prepTimes[sample] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    prepBytes[sample] = GC.GetAllocatedBytesForCurrentThread() - allocated;
                    var stopStart = Stopwatch.GetTimestamp();
                    prepared.Stop();
                    stopTimes[sample] = Stopwatch.GetElapsedTime(stopStart).TotalMilliseconds;
                }
                var retainedBytes = MeasureRetained(source, registry);
                using var runtime = new SceneRuntime(source, registry);
                runtime.Start();
                var warmup = Stopwatch.StartNew();
                while (warmup.ElapsedMilliseconds < 500) runtime.Step(1f / 60);
                var stepTimes = new double[samples];
                var stepBytes = new long[samples];
                for (var sample = 0; sample < samples; sample++)
                {
                    var allocated = GC.GetAllocatedBytesForCurrentThread();
                    var start = Stopwatch.GetTimestamp();
                    for (var step = 0; step < steps; step++) runtime.Step(1f / 60);
                    stepTimes[sample] = Stopwatch.GetElapsedTime(start).TotalMicroseconds / steps;
                    stepBytes[sample] = GC.GetAllocatedBytesForCurrentThread() - allocated;
                }
                Array.Sort(prepTimes);
                Array.Sort(prepBytes);
                Array.Sort(stepTimes);
                Array.Sort(stepBytes);
                Array.Sort(stopTimes);
                Console.WriteLine(FormattableString.Invariant($"{count * 2},{(withRefs ? count : 0)},{prepTimes[samples / 2]:F3},{prepBytes[samples / 2]},{yaml.Length},{stepTimes[samples / 2]:F3},{(double)stepBytes[samples / 2] / steps:F3},{stopTimes[samples / 2]:F3},{retainedBytes}"));
                GC.KeepAlive(source);
                if (runtime.Errors.Count != 0) throw new InvalidOperationException("Reference benchmark lifecycle failed.");
                if (BenchmarkRefHolder.Sink == long.MinValue) Console.WriteLine(BenchmarkRefHolder.Sink);
            }
        }

        static void MeasureReferenceEdit()
        {
            const int editSamples = 5;
            Console.WriteLine("reference edit: holders,affected,assign_us,clear_us,delete_ms");
            foreach (var count in new[] { 1_000, 10_000 })
                MeasureEditCase(count, editSamples);

            static void MeasureEditCase(int count, int samples)
            {
                var registry = new ComponentRegistry();
                registry.Register<BenchmarkRefTarget>("bench.ref-target");
                registry.Register<BenchmarkRefHolder>("bench.ref-holder");
                var assignTimes = new double[samples];
                var clearTimes = new double[samples];
                var deleteTimes = new double[samples];
                for (var sample = 0; sample < samples; sample++)
                {
                    var scene = new Scene();
                    var targetObject = scene.AddEmpty();
                    targetObject.Rename("Shared target");
                    var shared = new BenchmarkRefTarget();
                    targetObject.Attach(shared);
                    var holders = new List<BenchmarkRefHolder>(count);
                    for (var i = 0; i < count; i++)
                    {
                        var holderObject = scene.AddEmpty();
                        holderObject.Rename($"Holder {i}");
                        holderObject.Attach(new BenchmarkRefHolder());
                        holders.Add(holderObject.GetComponent<BenchmarkRefHolder>()!);
                    }
                    var assignStart = Stopwatch.GetTimestamp();
                    foreach (var holder in holders)
                        holder.Single = shared;
                    assignTimes[sample] = Stopwatch.GetElapsedTime(assignStart).TotalMicroseconds / count;
                    var clearStart = Stopwatch.GetTimestamp();
                    foreach (var holder in holders)
                        holder.Single = null;
                    clearTimes[sample] = Stopwatch.GetElapsedTime(clearStart).TotalMicroseconds / count;
                    foreach (var holder in holders)
                        holder.Single = shared;
                    var deleteStart = Stopwatch.GetTimestamp();
                    scene.Remove(targetObject);
                    deleteTimes[sample] = Stopwatch.GetElapsedTime(deleteStart).TotalMilliseconds;
                }
                Array.Sort(assignTimes);
                Array.Sort(clearTimes);
                Array.Sort(deleteTimes);
                Console.WriteLine(FormattableString.Invariant($"{count},{count},{assignTimes[samples / 2]:F3},{clearTimes[samples / 2]:F3},{deleteTimes[samples / 2]:F3}"));
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureRetained(Scene source, ComponentRegistry registry)
    {
        // Keep the caller's preparation samples outside this frame so both GCs see identical roots.
        var before = GC.GetTotalMemory(forceFullCollection: true);
        using var runtime = new SceneRuntime(source, registry);
        runtime.Start();
        var retained = GC.GetTotalMemory(forceFullCollection: true) - before;
        GC.KeepAlive(runtime);
        GC.KeepAlive(source);
        return retained;
    }
}

public sealed class BenchmarkBehaviour
{
    [Inspector] public int Value;
#pragma warning disable CA1822 // Reflection tests require these lifecycle/Inspector members to remain instance members.
    [Start] private void Begin() { }
    [Update, MethodImpl(MethodImplOptions.NoInlining)] private void Tick(float _) { }
    [Destroy] private void End() { }
#pragma warning restore CA1822
}

public sealed class BenchmarkData
{
    [Inspector] public int Value;
}

public sealed class BenchmarkRefTarget
{
    [Inspector] public int Value;
}

public sealed class BenchmarkRefHolder
{
    public static long Sink { get; set; }
    [Inspector] public BenchmarkRefTarget? Single { get; set; }
    [Inspector] public int Value;
#pragma warning disable CA1822 // Reflection tests require these lifecycle/Inspector members to remain instance members.
    [Start] private void Begin() { }
    [Update, MethodImpl(MethodImplOptions.NoInlining)] private void Tick(float _)
    {
        var local = 0L;
        var current = Single;
        if (current is not null)
        {
            local += current.Value;
            local += current.Value;
            local += current.Value;
            local += current.Value;
        }
        else
        {
            local += Value;
            local += Value;
            local += Value;
            local += Value;
        }
        Sink += local;
    }
    [Destroy] private void End() { }
#pragma warning restore CA1822
}
