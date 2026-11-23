using RobloxAfkManager.App.ViewModels;
using RobloxAfkManager.Core;

namespace RobloxAfkManager.Tests;

internal static class ResourceProfileTests
{
    public static void Register(Action<string, Action> test)
    {
        test("Resource plans preserve unrelated power bits and never use High/Realtime CPU priority", () =>
        {
            var original = new ResourceState(0x80, 5, 2, 2);
            var low = original.ForPlan(ResourcePlan.Low); var balanced = low.ForPlan(ResourcePlan.Balanced); var high = balanced.ForPlan(ResourcePlan.High);
            Program.Check(low == new ResourceState(0x4000, 2, 3, 3));
            Program.Check(balanced == new ResourceState(0x4000, 4, 2, 2));
            Program.Check(high == new ResourceState(0x20, 5, 3, 2));
        });
        test("Resource plans save first originals across switches and restore them exactly", () =>
        {
            var f = new FakeProcessResources(new FakeWindowSystem(2)); var manager = new ResourceProfileManager(f, new TestLog());
            var before = f.States.ToDictionary();
            foreach (var plan in Enum.GetValues<ResourcePlan>()) Program.Check(manager.Apply(plan, f.States.Keys.ToArray()).Success);
            Program.Check(manager.SavedCount == 2 && manager.AppliedCount == 2);
            Program.Check(manager.Reset().Success && before.All(p => f.States[p.Key] == p.Value));
            Program.Check(manager.ActivePlan is null && manager.SavedCount == 0);
        });
        test("Resource sync applies only new processes and does not retry failures on every refresh", () =>
        {
            var f = new FakeProcessResources(new FakeWindowSystem(2)); var manager = new ResourceProfileManager(f, new TestLog());
            var ids = f.States.Keys.ToArray(); f.Denied.Add(ids[1]);
            Program.Check(!manager.Apply(ResourcePlan.Low, ids).Success && manager.AppliedCount == 1);
            int writes = f.Writes.Count, opens = f.Opens;
            Program.Check(manager.Synchronize(ids) is null && f.Writes.Count == writes && f.Opens == opens);
            f.Denied.Clear(); Program.Check(manager.Apply(ResourcePlan.Low, ids).Success);
            var replacement = ids[1] with { StartTimeUtcTicks = 20000 }; f.States.Remove(ids[1]); f.States[replacement] = new(0x20, 5, 0, 0);
            Program.Check(manager.Synchronize([ids[0], replacement])?.Success == true && manager.AppliedCount == 2);
            Program.Check(manager.Reset().Success && f.States[replacement] == new ResourceState(0x20, 5, 0, 0));
        });
        test("A partial resource failure rolls back and keeps successful clients independently managed", () =>
        {
            var f = new FakeProcessResources(new FakeWindowSystem(2)); var manager = new ResourceProfileManager(f, new TestLog());
            var ids = f.States.Keys.ToArray(); var before = f.States[ids[0]]; f.FailOnce = ids[0];
            Program.Check(!manager.Apply(ResourcePlan.Low, ids).Success);
            Program.Check(f.States[ids[0]] == before && manager.SavedCount == 1 && manager.AppliedCount == 1);
            Program.Check(manager.Reset().Success);
        });
        test("Failed rollback/reset retains originals for retry without changing a reused PID", () =>
        {
            var f = new FakeProcessResources(new FakeWindowSystem(2)); var manager = new ResourceProfileManager(f, new TestLog());
            var ids = f.States.Keys.ToArray(); var before = f.States[ids[0]]; f.FailAlways.Add(ids[0]);
            Program.Check(!manager.Apply(ResourcePlan.Low, [ids[0]]).Success && manager.SavedCount == 1);
            Program.Check(!manager.Reset().Success && manager.ActivePlan is null && manager.SavedCount == 1);
            f.FailAlways.Clear(); Program.Check(manager.Reset().Success && f.States[ids[0]] == before);
            Program.Check(manager.Apply(ResourcePlan.Low, [ids[1]]).Success);
            f.States.Remove(ids[1]); var replacement = ids[1] with { StartTimeUtcTicks = 20000 }; f.States[replacement] = new(0x20, 5, 0, 0);
            int writes = f.Writes.Count; Program.Check(manager.Reset().Success && f.Writes.Count == writes);
        });
        test("View model excludes ignored clients, restores on ignore, and reapplies after showing again", () =>
        {
            var windows = new FakeWindowSystem(9); var f = new FakeProcessResources(windows);
            using var vm = new MainViewModel(windows, new TestLog(), f); vm.RefreshAsync().GetAwaiter().GetResult();
            var ignored = vm.Clients[0]; ignored.IsIgnored = true; var ignoredId = ProcessIdentity.From(ignored.Identity);
            Program.Check(f.Writes.Count == 0 && !vm.HasResourceChanges);
            vm.SelectedResourcePlan = ResourcePlan.Low; Execute(vm, () => vm.ApplyResourcePlanCommand.Execute(null));
            Program.Check(!vm.Warning && f.Writes.Count == 8 && f.Writes.All(w => w.Id != ignoredId));
            var next = vm.VisibleClients[0]; var nextId = ProcessIdentity.From(next.Identity);
            Execute(vm, () => next.IsIgnored = true);
            Program.Check(next.IsIgnored && !vm.VisibleClients.Contains(next) && f.States[nextId] == FakeProcessResources.Original);
            vm.UnignoreClientCommand.Execute(next); vm.RefreshAsync().GetAwaiter().GetResult();
            Program.Check(f.States[nextId].MemoryPriority == 2 && f.States[ignoredId] == FakeProcessResources.Original);
            Program.Check(vm.StopResourcePlansAsync().GetAwaiter().GetResult() && f.States.Values.All(v => v == FakeProcessResources.Original));
        });
        test("A client cannot become ignored while original resource restoration has failed", () =>
        {
            var windows = new FakeWindowSystem(2); var f = new FakeProcessResources(windows);
            using var vm = new MainViewModel(windows, new TestLog(), f); vm.RefreshAsync().GetAwaiter().GetResult();
            Execute(vm, () => vm.ApplyResourcePlanCommand.Execute(null));
            var client = vm.Clients[0]; var id = ProcessIdentity.From(client.Identity); f.FailAlways.Add(id); Execute(vm, () => client.IsIgnored = true);
            Program.Check(!client.IsIgnored && vm.VisibleClients.Contains(client) && vm.Warning && vm.Message.Contains("remains visible"));
            f.FailAlways.Clear(); Execute(vm, () => client.IsIgnored = true); Program.Check(client.IsIgnored && f.States[id] == FakeProcessResources.Original);
        });
        test("Disposing a manager restores every saved resource state", () =>
        {
            var windows = new FakeWindowSystem(2); var f = new FakeProcessResources(windows);
            var vm = new MainViewModel(windows, new TestLog(), f); vm.RefreshAsync().GetAwaiter().GetResult();
            Execute(vm, () => vm.ApplyResourcePlanCommand.Execute(null)); vm.Dispose();
            Program.Check(f.States.Values.All(v => v == FakeProcessResources.Original));
        });
    }
    internal static void Execute(MainViewModel vm, Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        System.ComponentModel.PropertyChangedEventHandler listener = (_, e) => { if (e.PropertyName == nameof(vm.Busy) && !vm.Busy) done.TrySetResult(); };
        vm.PropertyChanged += listener;
        try { action(); done.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
        finally { vm.PropertyChanged -= listener; }
    }
}

internal sealed class FakeProcessResources : IProcessResourceSystem
{
    public static readonly ResourceState Original = new(0x20, 5, 2, 2);
    public Dictionary<ProcessIdentity, ResourceState> States { get; }
    public HashSet<ProcessIdentity> Denied { get; } = [];
    public HashSet<ProcessIdentity> FailAlways { get; } = [];
    public ProcessIdentity? FailOnce { get; set; }
    public List<(ProcessIdentity Id, ResourceState State)> Writes { get; } = [];
    public int Opens { get; private set; }
    public FakeProcessResources(FakeWindowSystem windows) => States = windows.Detect().ToDictionary(c => ProcessIdentity.From(c.Identity), _ => Original);
    public bool IsAlive(ProcessIdentity id) => States.ContainsKey(id);
    public IProcessResourceHandle Open(ProcessIdentity id)
    {
        Opens++;
        if (!IsAlive(id)) throw new ProcessGoneException("Closed/reused PID");
        if (Denied.Contains(id)) throw new UnauthorizedAccessException("Access denied");
        return new Handle(this, id);
    }
    private sealed class Handle(FakeProcessResources owner, ProcessIdentity id) : IProcessResourceHandle
    {
        public ResourceState Read() => owner.States[id];
        public void Write(ResourceState state)
        {
            owner.Writes.Add((id, state));
            owner.States[id] = owner.States[id] with { PriorityClass = state.PriorityClass };
            if (owner.FailOnce == id || owner.FailAlways.Contains(id)) { owner.FailOnce = null; throw new InvalidOperationException("Synthetic partial write failure"); }
            owner.States[id] = state;
        }
        public void Dispose() { }
    }
}
