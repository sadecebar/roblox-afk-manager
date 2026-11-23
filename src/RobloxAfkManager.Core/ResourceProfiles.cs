namespace RobloxAfkManager.Core;

public enum ResourcePlan { Low, Balanced, High }
public readonly record struct ProcessIdentity(int ProcessId, long StartTimeUtcTicks)
{
    public static ProcessIdentity From(WindowIdentity window) => new(window.ProcessId, window.StartTimeUtcTicks);
}
public readonly record struct ResourceState(uint PriorityClass, uint MemoryPriority, uint PowerControl, uint PowerState)
{
    // Only explicit power choices are stable; Windows owns any bits outside ControlMask.
    public ResourceState Normalized => this with { PowerState = PowerState & PowerControl };
    public ResourceState ForPlan(ResourcePlan plan) => plan switch
    {
        ResourcePlan.Low => new(0x4000, 2, PowerControl | 1u, PowerState | 1u),
        ResourcePlan.Balanced => new(0x4000, 4, PowerControl & ~1u, PowerState & ~1u),
        ResourcePlan.High => new(0x20, 5, PowerControl | 1u, PowerState & ~1u),
        _ => throw new ArgumentOutOfRangeException(nameof(plan))
    };
}
public sealed class ProcessGoneException(string message) : Exception(message);
public interface IProcessResourceHandle : IDisposable
{
    ResourceState Read();
    void Write(ResourceState state);
}
public interface IProcessResourceSystem
{
    IProcessResourceHandle Open(ProcessIdentity process);
    bool IsAlive(ProcessIdentity process);
}

// Invoked serially by the view model. No trimming, caps, process suspension or game configuration.
public sealed class ResourceProfileManager(IProcessResourceSystem system, IAppLog log)
{
    private readonly Dictionary<ProcessIdentity, ResourceState> originals = [];
    private readonly Dictionary<ProcessIdentity, ResourcePlan> applied = [];
    private readonly Dictionary<ProcessIdentity, string> errors = [];
    private readonly HashSet<ProcessIdentity> attempted = [];
    public ResourcePlan? ActivePlan { get; private set; }
    public int SavedCount => originals.Count;
    public int AppliedCount => applied.Count;
    public ResourcePlan? AppliedPlan(ProcessIdentity id) => applied.TryGetValue(id, out var plan) ? plan : null;
    public OperationResult ApplySaved(ResourcePlan plan, ProcessIdentity id) => ApplyMany([id], plan);
    public string Status(ProcessIdentity id) => errors.TryGetValue(id, out var error) ? error : applied.TryGetValue(id, out var plan) ? $"{plan} applied" : "Original settings";

    public OperationResult Apply(ResourcePlan plan, IEnumerable<ProcessIdentity> included)
    {
        ActivePlan = plan;
        return ApplyMany(included.Distinct().ToArray(), plan);
    }
    public OperationResult? Synchronize(IEnumerable<ProcessIdentity> included)
    {
        foreach (var id in originals.Keys.Concat(attempted).Distinct().ToArray())
            if (!system.IsAlive(id)) Forget(id);
        if (ActivePlan is not { } plan) return null;
        var newlyIncluded = included.Distinct().Where(id => !attempted.Contains(id)).ToArray();
        return newlyIncluded.Length == 0 ? null : ApplyMany(newlyIncluded, plan);
    }
    private OperationResult ApplyMany(IReadOnlyList<ProcessIdentity> included, ResourcePlan plan)
    {
        int successful = 0; var failures = new List<string>();
        foreach (var id in included)
        {
            attempted.Add(id);
            try
            {
                using var process = system.Open(id); // Handle pins the checked process for read, write and rollback.
                var before = process.Read().Normalized;
                bool savedAlready = originals.ContainsKey(id);
                originals.TryAdd(id, before);
                try
                {
                    var target = before.ForPlan(plan).Normalized;
                    if (before != target) process.Write(target);
                    if (process.Read().Normalized != target) throw new InvalidOperationException("Windows did not retain the requested settings.");
                }
                catch
                {
                    try
                    {
                        process.Write(before);
                        if (process.Read().Normalized != before) throw new InvalidOperationException("Rollback verification failed.");
                        if (!savedAlready) originals.Remove(id);
                    }
                    catch (Exception rollback) { log.Write($"Resource rollback failed for PID {id.ProcessId}; original retained for retry.", rollback); }
                    throw;
                }
                applied[id] = plan; errors.Remove(id); successful++;
            }
            catch (ProcessGoneException) { Forget(id); failures.Add($"PID {id.ProcessId} closed/restarted"); }
            catch (Exception ex)
            {
                applied.Remove(id); errors[id] = "Plan failed · retry or reset";
                failures.Add($"PID {id.ProcessId}: {ex.Message}"); log.Write($"Resource plan failed for PID {id.ProcessId}.", ex);
            }
        }
        return new(failures.Count == 0, $"{plan}: {successful}/{included.Count} clients applied. Ignored clients skipped." + (failures.Count == 0 ? "" : " " + string.Join("; ", failures)));
    }
    public OperationResult Restore(ProcessIdentity id)
    {
        if (!originals.TryGetValue(id, out var original)) { Forget(id); return new(true, "Original resource settings retained."); }
        try
        {
            using var process = system.Open(id);
            process.Write(original);
            if (process.Read().Normalized != original) throw new InvalidOperationException("Windows did not retain the original settings.");
            Forget(id); return new(true, $"PID {id.ProcessId}: original resource settings restored.");
        }
        catch (ProcessGoneException) { Forget(id); return new(true, "The original process closed; no replacement was changed."); }
        catch (Exception ex)
        {
            applied.Remove(id); errors[id] = "Reset failed · retry";
            log.Write($"Resource restore failed for PID {id.ProcessId}.", ex);
            return new(false, $"PID {id.ProcessId}: could not restore resource settings. {ex.Message}");
        }
    }
    public OperationResult Reset()
    {
        ActivePlan = null; attempted.Clear();
        var failures = originals.Keys.ToArray().Select(Restore).Where(r => !r.Success).Select(r => r.Message).ToArray();
        if (failures.Length == 0) { applied.Clear(); errors.Clear(); }
        return new(failures.Length == 0, failures.Length == 0 ? "Resource plans off. Original settings restored." : string.Join("; ", failures));
    }
    private void Forget(ProcessIdentity id) { originals.Remove(id); applied.Remove(id); errors.Remove(id); attempted.Remove(id); }
}
