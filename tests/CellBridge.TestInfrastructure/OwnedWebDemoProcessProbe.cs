using System.Diagnostics;

namespace CellBridge.TestInfrastructure;

public sealed record OwnedProcessSample(int ProcessId, DateTime StartUtc, DateTime CapturedUtc,
    long TimestampMilliseconds, double CpuMilliseconds, long WorkingSetBytes);

/// <summary>Reads only the executable belonging to the fixture's Aspire resource.</summary>
public sealed class OwnedWebDemoProcessProbe
{
    private readonly (int Id, DateTime StartUtc)[] ancestry;
    public int ResourceProcessId => ancestry[0].Id;
    public int ProcessId => ancestry[^1].Id;
    public DateTime StartUtc => ancestry[^1].StartUtc;

    internal OwnedWebDemoProcessProbe(int resourceProcessId)
    {
        if (OperatingSystem.IsWindows())
        {
            using var process = Process.GetProcessById(resourceProcessId);
            var executable = process.MainModule?.FileName;
            if (Path.GetFileName(executable) is not "CellBridge.WebDemo.exe")
                throw new PlatformNotSupportedException("The Aspire WebDemo resource does not expose a directly verifiable WebDemo executable on Windows.");
            var expectedAssembly = Path.Combine(Path.GetDirectoryName(executable)!, "CellBridge.WebDemo.dll");
            if (!File.Exists(expectedAssembly))
                throw new InvalidOperationException("The Aspire WebDemo executable has no adjacent CellBridge.WebDemo assembly.");
            ancestry = [(resourceProcessId, process.StartTime.ToUniversalTime())];
            Read();
            return;
        }
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Owned WebDemo process verification requires Linux or Windows.");
        // Aspire may expose a launcher. Follow only its explicit children, never a system process search.
        var pending = new Queue<(int Id, (int Id, DateTime StartUtc)[] Parents)>();
        pending.Enqueue((resourceProcessId, []));
        var matches = new List<(int Id, DateTime StartUtc)[]>();
        var visited = new HashSet<int>();
        while (pending.TryDequeue(out var entry))
        {
            if (!visited.Add(entry.Id)) continue;
            if (visited.Count > 128 || entry.Parents.Length >= 8)
                throw new InvalidOperationException("The owned WebDemo process tree exceeds the discovery bound.");
            using var process = Process.GetProcessById(entry.Id);
            var identity = (entry.Id, process.StartTime.ToUniversalTime());
            var chain = entry.Parents.Append(identity).ToArray();
            var arguments = File.ReadAllText($"/proc/{entry.Id}/cmdline").Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (arguments.Any(argument => Path.GetFileName(argument) is "CellBridge.WebDemo.dll" or "CellBridge.WebDemo"))
            {
                matches.Add(chain);
                continue;
            }
            foreach (var thread in Directory.EnumerateDirectories($"/proc/{entry.Id}/task"))
            {
                string children;
                try { children = File.ReadAllText(Path.Combine(thread, "children")); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                foreach (var child in children.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    pending.Enqueue((int.Parse(child, System.Globalization.CultureInfo.InvariantCulture), chain));
            }
        }
        ancestry = matches.Count == 1 ? matches[0]
            : throw new InvalidOperationException("The Aspire resource must identify exactly one WebDemo application process.");
        Read();
    }

    public OwnedProcessSample Read()
    {
        foreach (var ancestor in ancestry)
        {
            using var process = Process.GetProcessById(ancestor.Id);
            if (process.HasExited || process.StartTime.ToUniversalTime() != ancestor.StartUtc)
                throw new InvalidOperationException("The owned WebDemo process identity changed during measurement.");
            if (ancestor.Id == ProcessId)
                return new(ProcessId, StartUtc, DateTime.UtcNow, Environment.TickCount64,
                    process.TotalProcessorTime.TotalMilliseconds, process.WorkingSet64);
        }
        throw new InvalidOperationException("Missing owned application process.");
    }
}
