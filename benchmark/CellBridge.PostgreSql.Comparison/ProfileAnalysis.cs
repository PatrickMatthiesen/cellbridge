using System.Text.Json;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

internal static class ProfileAnalysis
{
    public static void Run(string path)
    {
        var etlx = TraceLog.CreateFromEventPipeDataFile(path);
        using var log = new TraceLog(etlx);
        var active = false;
        var batches = 0;
        var allocated = new Dictionary<string, long>();
        var allocationStacks = new Dictionary<string, long>();
        var cpu = new Dictionary<string, int>();
        foreach (var data in log.Events)
        {
            if (data.ProviderName == "CellBridge-MutationProfile")
            {
                if ((int)data.ID == 1) { active = true; batches++; }
                if ((int)data.ID == 2) active = false;
                continue;
            }
            if (!active) continue;
            if (data is GCAllocationTickTraceData allocation)
            {
                allocated[allocation.TypeName] = allocated.GetValueOrDefault(allocation.TypeName) + allocation.AllocationAmount64;
                var frames = Frames(data.CallStack());
                allocationStacks[frames] = allocationStacks.GetValueOrDefault(frames) + allocation.AllocationAmount64;
            }
            if (data.ProviderName == "Microsoft-DotNETCore-SampleProfiler")
            {
                var frames = Frames(data.CallStack());
                cpu[frames] = cpu.GetValueOrDefault(frames) + 1;
            }
        }
        File.WriteAllText(path + ".analysis.json", JsonSerializer.Serialize(new { batches,
            note = "GC allocation ticks estimate allocated bytes by type/stack, not retained heap. CPU stacks are samples within batch regions on all threads, including profiler overhead. Async stacks show the current continuation only.",
            allocationTypes = allocated.OrderByDescending(p => p.Value).Select(p => new { type = p.Key, bytes = p.Value }),
            allocationStacks = allocationStacks.OrderByDescending(p => p.Value).Select(p => new { stack = p.Key, bytes = p.Value }),
            cpuStacks = cpu.OrderByDescending(p => p.Value).Select(p => new { stack = p.Key, samples = p.Value })
        }, new JsonSerializerOptions { WriteIndented = true }));
        File.Delete(etlx);
    }
    private static string Frames(TraceCallStack? stack)
    {
        var frames = new List<string>();
        for (; stack is not null; stack = stack.Caller)
            frames.Add(stack.CodeAddress.FullMethodName);
        return string.Join("\n", frames);
    }
}
