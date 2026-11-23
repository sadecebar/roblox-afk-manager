namespace RobloxAfkManager.Core;

// Read-only samples, taken by the existing serialized discovery loop. No extra timer.
public sealed class CpuUsageSampler
{
    private readonly Dictionary<ProcessIdentity, (TimeSpan Cpu, double Time, double? Percent)> samples = [];

    public double? Sample(ProcessIdentity process, TimeSpan cpu, double monotonicSeconds, int processorCount)
    {
        if (!double.IsFinite(monotonicSeconds) || processorCount <= 0 || cpu < TimeSpan.Zero) return null;
        double? percent = null;
        if (samples.TryGetValue(process, out var previous))
        {
            double elapsed = monotonicSeconds - previous.Time;
            // Window events can cause closely spaced reads; keep a useful averaging interval.
            if (elapsed >= 0 && elapsed < 0.5 && cpu >= previous.Cpu) return previous.Percent;
            if (elapsed > 0 && cpu >= previous.Cpu)
                percent = Math.Clamp((cpu - previous.Cpu).TotalSeconds / elapsed / processorCount * 100, 0, 100);
        }
        samples[process] = (cpu, monotonicSeconds, percent);
        return percent;
    }

    public void Retain(IEnumerable<ProcessIdentity> liveProcesses)
    {
        var live = liveProcesses.ToHashSet();
        foreach (var process in samples.Keys.Where(p => !live.Contains(p)).ToArray()) samples.Remove(process);
    }
}
