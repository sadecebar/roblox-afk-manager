using RobloxAfkManager.App.ViewModels;
using RobloxAfkManager.Core;

namespace RobloxAfkManager.Tests;

internal static class WorkingSetCapTests
{
    public static void Register(Action<string, Action> test)
    {
        test("Resident RAM limits validate sizes and explicitly disable the old minimum guarantee", () =>
        {
            var original = new WorkingSetLimits(800UL * 1048576, 2000UL * 1048576, 1);
            var capped = original.WithCap(500);
            Program.Check(capped.MaximumBytes == 500UL * 1048576 && capped.MinimumBytes == 64UL * 1048576 && capped.Flags == 4 && capped.NativeFlags == 6);
            Program.Check(new WorkingSetLimits(204800, 1413120, 0).NativeFlags == 10);
            foreach (int value in new[] { -1, 0, 255, 8193, int.MaxValue })
            {
                bool rejected = false; try { _ = original.WithCap(value); } catch (ArgumentOutOfRangeException) { rejected = true; }
                Program.Check(rejected);
            }
        });
        test("Resident cap preserves first originals across changes and clears enforcement on Reset", () =>
        {
            var f = new FakeWorkingSets(new FakeWindowSystem(2)); var manager = new WorkingSetCapManager(f, new TestLog());
            var before = f.States.ToDictionary();
            Program.Check(manager.Apply(500, f.States.Keys.ToArray()).Success);
            Program.Check(manager.Apply(1024, f.States.Keys.ToArray()).Success);
            Program.Check(manager.Reset().Success && manager.ActiveMiB is null && manager.SavedCount == 0);
            Program.Check(before.All(p => p.Value == f.States[p.Key]));
        });
        test("Resident cap partial failure rolls back; failed resets remain retryable", () =>
        {
            var f = new FakeWorkingSets(new FakeWindowSystem(2)); var manager = new WorkingSetCapManager(f, new TestLog());
            var ids = f.States.Keys.ToArray(); var before = f.States[ids[0]]; f.FailOnce = ids[0];
            Program.Check(!manager.Apply(500, ids).Success && f.States[ids[0]] == before && manager.SavedCount == 1);
            f.FailAlways.Add(ids[1]); Program.Check(!manager.Reset().Success && manager.SavedCount == 1);
            f.FailAlways.Clear(); Program.Check(manager.Reset().Success && manager.SavedCount == 0);
        });
        test("Resident cap retries only explicitly, follows HWND recreation and refuses PID reuse on restore", () =>
        {
            var f = new FakeWorkingSets(new FakeWindowSystem(2)); var manager = new WorkingSetCapManager(f, new TestLog());
            var ids = f.States.Keys.ToArray(); f.Denied.Add(ids[0]);
            Program.Check(!manager.Apply(500, ids).Success); int writes = f.Writes.Count;
            Program.Check(manager.Synchronize(ids) is null && f.Writes.Count == writes);
            f.Denied.Clear(); Program.Check(manager.Apply(500, ids).Success);
            f.States.Remove(ids[0]); var replacement = ids[0] with { StartTimeUtcTicks = 99999 }; f.States[replacement] = FakeWorkingSets.Original;
            writes = f.Writes.Count; Program.Check(manager.Restore(ids[0]).Success && f.Writes.Count == writes);
            Program.Check(manager.Synchronize([ids[1], replacement])?.Success == true);
            Program.Check(manager.Reset().Success && f.States[replacement] == FakeWorkingSets.Original);
        });
        test("Resident cap excludes ignored clients and restores before hiding or manager disposal", () =>
        {
            var windows = new FakeWindowSystem(9); var f = new FakeWorkingSets(windows);
            var vm = new MainViewModel(windows, new TestLog(), new FakeProcessResources(windows), f);
            vm.RefreshAsync().GetAwaiter().GetResult(); var ignored = vm.Clients[0]; ignored.IsIgnored = true;
            var id = ProcessIdentity.From(ignored.Identity);
            vm.MemoryCapInput = "abc"; Program.Check(!vm.ApplyMemoryCapCommand.CanExecute(null)); vm.MemoryCapInput = "500";
            ResourceProfileTests.Execute(vm, () => vm.ApplyMemoryCapCommand.Execute(null));
            Program.Check(!vm.Warning && f.Writes.Count == 8 && f.Writes.All(w => w != id));
            var managed = vm.VisibleClients[0]; var managedId = ProcessIdentity.From(managed.Identity);
            f.FailAlways.Add(managedId); ResourceProfileTests.Execute(vm, () => managed.IsIgnored = true);
            Program.Check(!managed.IsIgnored && vm.Warning);
            f.FailAlways.Clear(); ResourceProfileTests.Execute(vm, () => managed.IsIgnored = true);
            Program.Check(managed.IsIgnored && f.States[managedId] == FakeWorkingSets.Original);
            vm.Dispose(); Program.Check(f.States.Values.All(s => s == FakeWorkingSets.Original));
        });
    }
}

internal sealed class FakeWorkingSets : IWorkingSetSystem
{
    public static readonly WorkingSetLimits Original = new(204800, 1413120, 0);
    public Dictionary<ProcessIdentity, WorkingSetLimits> States { get; }
    public HashSet<ProcessIdentity> Denied { get; } = [];
    public HashSet<ProcessIdentity> FailAlways { get; } = [];
    public ProcessIdentity? FailOnce { get; set; }
    public List<ProcessIdentity> Writes { get; } = [];
    public FakeWorkingSets(FakeWindowSystem windows) => States = windows.Detect().ToDictionary(c => ProcessIdentity.From(c.Identity), _ => Original);
    public bool IsAlive(ProcessIdentity id) => States.ContainsKey(id);
    public IWorkingSetHandle OpenWorkingSet(ProcessIdentity id)
    {
        if (!IsAlive(id)) throw new ProcessGoneException("Closed process");
        if (Denied.Contains(id)) throw new UnauthorizedAccessException("Access denied");
        return new Handle(this, id);
    }
    private sealed class Handle(FakeWorkingSets owner, ProcessIdentity id) : IWorkingSetHandle
    {
        public WorkingSetLimits ReadLimits() => owner.States[id];
        public void WriteLimits(WorkingSetLimits limits)
        {
            owner.Writes.Add(id); owner.States[id] = limits;
            if (owner.FailOnce == id || owner.FailAlways.Contains(id)) { owner.FailOnce = null; throw new InvalidOperationException("Synthetic limit failure"); }
        }
        public void Dispose() { }
    }
}
