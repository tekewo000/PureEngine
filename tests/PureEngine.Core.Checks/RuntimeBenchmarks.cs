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
