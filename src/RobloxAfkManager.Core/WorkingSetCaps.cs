namespace RobloxAfkManager.Core;

public readonly record struct WorkingSetLimits(ulong MinimumBytes, ulong MaximumBytes, uint Flags)
{
    // Compare enforcement state, not the mutually exclusive enable/disable request flags.
    public WorkingSetLimits Normalized => this with { Flags = Flags & 5 };
    public uint NativeFlags => ((Flags & 1) != 0 ? 1u : 2u) | ((Flags & 4) != 0 ? 4u : 8u);
    public WorkingSetLimits WithCap(int mebibytes)
    {
        if (mebibytes is < 256 or > 8192) throw new ArgumentOutOfRangeException(nameof(mebibytes), "Choose a resident RAM limit from 256 to 8192 MiB.");
        ulong maximum = (ulong)mebibytes * 1048576;
        return new(Math.Clamp(MinimumBytes, 81920UL, Math.Min(64UL * 1048576, maximum / 2)), maximum, 4);
    }
}
public interface IWorkingSetHandle : IDisposable
{
    WorkingSetLimits ReadLimits();
    void WriteLimits(WorkingSetLimits limits);
}
public interface IWorkingSetSystem
{
    IWorkingSetHandle OpenWorkingSet(ProcessIdentity process);
    bool IsAlive(ProcessIdentity process);
}

// A resident-page limit, never a commit/allocation cap. No EmptyWorkingSet or repeated trim loop.
public sealed class WorkingSetCapManager(IWorkingSetSystem system, IAppLog log)
{
    private readonly Dictionary<ProcessIdentity, WorkingSetLimits> originals = [];
    private readonly HashSet<ProcessIdentity> attempted = [];
    private readonly Dictionary<ProcessIdentity, string> status = [];
    public int? ActiveMiB { get; private set; }
    public int SavedCount => originals.Count;
    public int AppliedCount => ActiveMiB is { } cap ? status.Values.Count(value => value == $"Resident cap {cap} MiB") : 0;
    public string Status(ProcessIdentity id) => status.GetValueOrDefault(id, "Resident cap off");
    public OperationResult Apply(int mebibytes, IEnumerable<ProcessIdentity> included)
    {
        _ = default(WorkingSetLimits).WithCap(mebibytes);
        ActiveMiB = mebibytes;
        return ApplyMany(mebibytes, included.Distinct().ToArray());
    }
    public OperationResult? Synchronize(IEnumerable<ProcessIdentity> included)
    {
        foreach (var id in originals.Keys.Concat(attempted).Distinct().ToArray()) if (!system.IsAlive(id)) Forget(id);
        if (ActiveMiB is not { } cap) return null;
        var fresh = included.Distinct().Where(id => !attempted.Contains(id)).ToArray();
        return fresh.Length == 0 ? null : ApplyMany(cap, fresh);
    }
    private OperationResult ApplyMany(int cap, IReadOnlyList<ProcessIdentity> included)
    {
        int successful = 0; var failures = new List<string>();
        foreach (var id in included)
        {
            attempted.Add(id);
            try
            {
                using var process = system.OpenWorkingSet(id);
                var before = process.ReadLimits().Normalized;
                bool hadOriginal = originals.ContainsKey(id); originals.TryAdd(id, before);
                try
                {
                    var target = before.WithCap(cap);
                    process.WriteLimits(target);
                    if (process.ReadLimits().Normalized != target) throw new InvalidOperationException("Windows did not retain the resident RAM limit.");
                }
                catch
                {
                    try
                    {
                        process.WriteLimits(before);
                        if (process.ReadLimits().Normalized != before) throw new InvalidOperationException("Resident cap rollback verification failed.");
                        if (!hadOriginal) originals.Remove(id);
                    }
                    catch (Exception ex) { log.Write($"Resident cap rollback failed for PID {id.ProcessId}; original retained.", ex); }
                    throw;
                }
                successful++; status[id] = $"Resident cap {cap} MiB";
            }
            catch (ProcessGoneException) { Forget(id); failures.Add($"PID {id.ProcessId} closed/restarted"); }
            catch (Exception ex) { status[id] = "Resident cap failed · retry/reset"; failures.Add($"PID {id.ProcessId}: {ex.Message}"); log.Write($"Resident cap failed for PID {id.ProcessId}.", ex); }
        }
        return new(failures.Count == 0, $"Resident RAM limit {cap} MiB: {successful}/{included.Count} clients applied; ignored clients skipped. Total allocated memory is not capped." + (failures.Count == 0 ? "" : " " + string.Join("; ", failures)));
    }
    public OperationResult Restore(ProcessIdentity id)
    {
        if (!originals.TryGetValue(id, out var original)) { Forget(id); return new(true, "Resident cap unchanged."); }
        try
        {
            using var process = system.OpenWorkingSet(id); process.WriteLimits(original);
            if (process.ReadLimits().Normalized != original) throw new InvalidOperationException("Resident limit restoration was not retained.");
            Forget(id); return new(true, "Original resident RAM limits restored.");
        }
        catch (ProcessGoneException) { Forget(id); return new(true, "Original process closed; replacement untouched."); }
        catch (Exception ex) { status[id] = "Resident reset failed · retry"; log.Write($"Resident cap reset failed for PID {id.ProcessId}.", ex); return new(false, $"PID {id.ProcessId}: {ex.Message}"); }
    }
    public OperationResult Reset()
    {
        ActiveMiB = null; attempted.Clear();
        var failures = originals.Keys.ToArray().Select(Restore).Where(r => !r.Success).Select(r => r.Message).ToArray();
        if (failures.Length == 0) status.Clear();
        return new(failures.Length == 0, failures.Length == 0 ? "Resident RAM limits off; originals restored." : string.Join("; ", failures));
    }
    private void Forget(ProcessIdentity id) { originals.Remove(id); attempted.Remove(id); status.Remove(id); }
}
