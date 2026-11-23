using RobloxAfkManager.App.ViewModels;
using RobloxAfkManager.Core;

namespace RobloxAfkManager.Tests;

internal static class DashboardInteractionTests
{
    public static void Register(Action<string, Action> test)
    {
        test("CPU samples average total processor capacity and tolerate fast refreshes", () =>
        {
            var meter = new CpuUsageSampler(); var id = new ProcessIdentity(1, 10);
            Program.Check(meter.Sample(id, TimeSpan.FromSeconds(2), 100, 8) is null);
            Program.Check(meter.Sample(id, TimeSpan.FromSeconds(10), 110, 8) == 10);
            Program.Check(meter.Sample(id, TimeSpan.FromSeconds(10.1), 110.1, 8) == 10);
            Program.Check(meter.Sample(id, TimeSpan.FromSeconds(14), 120, 8) == 5);
        });
        test("CPU samples do not cross process lifetimes or fabricate unavailable readings", () =>
        {
            var meter = new CpuUsageSampler(); var id = new ProcessIdentity(1, 10);
            meter.Sample(id, TimeSpan.FromSeconds(10), 100, 4);
            Program.Check(meter.Sample(id with { StartTimeUtcTicks = 11 }, TimeSpan.FromSeconds(1), 110, 4) is null);
            meter.Retain([]);
            Program.Check(meter.Sample(id, TimeSpan.FromSeconds(11), 120, 4) is null);
            Program.Check(meter.Sample(id, TimeSpan.FromSeconds(1), 130, 4) is null);
            Program.Check(meter.Sample(id, TimeSpan.Zero, double.NaN, 4) is null);
            var tile = new ClientTileViewModel(new(new(1, 1, 1), "Test", new(0, 0, 10, 10), null, "Open"), 1);
            Program.Check(tile.Cpu == "—" && tile.Memory == "Unavailable" && tile.PlanLabel == "Default");
        });
        test("Swapping clients preserves their labels and survives detection without game mutations", () =>
        {
            var fake = new FakeWindowSystem(12); using var vm = new MainViewModel(fake, new TestLog());
            vm.RefreshAsync().GetAwaiter().GetResult();
            var before = vm.Clients.ToArray();
            Program.Check(vm.SwapClients(before[10], before[2]));
            vm.RefreshAsync().GetAwaiter().GetResult();
            Program.Check(ReferenceEquals(vm.VisibleClients[2], before[10]) && before[10].Slot == "11");
            Program.Check(ReferenceEquals(vm.VisibleClients[10], before[2]) && before[2].Slot == "03");
            Program.Check(vm.Clients.Where((c, i) => i != 2 && i != 10).SequenceEqual(before.Where((c, i) => i != 2 && i != 10)));
            Program.Check(fake.Focused.Count == 0 && fake.Moved.Count == 0 && fake.Restored.Count == 0 && fake.Prepared.Count == 0);
        });
        test("Swap order survives ignored clients and recreated windows but rejects stale tiles", () =>
        {
            var fake = new FakeWindowSystem(4); using var vm = new MainViewModel(fake, new TestLog());
            vm.RefreshAsync().GetAwaiter().GetResult();
            var first = vm.Clients[0]; var last = vm.Clients[3]; var hidden = vm.Clients[1];
            hidden.IsIgnored = true;
            Program.Check(!vm.SwapClients(first, hidden) && !vm.SwapClients(first, first));
            Program.Check(vm.SwapClients(first, last));
            var old = last.Identity; var replacement = old with { Handle = 999, Generation = 2 };
            fake.Closed.Add(old); fake.Bounds[replacement] = fake.Bounds[old]; fake.Placements[replacement] = fake.Placements[old];
            vm.RefreshAsync().GetAwaiter().GetResult();
            Program.Check(ReferenceEquals(vm.VisibleClients[0], last) && last.Identity == replacement && last.Slot == "04");
            vm.UnignoreClientCommand.Execute(hidden);
            Program.Check(ReferenceEquals(vm.VisibleClients[1], hidden));
            fake.Closed.Add(first.Identity); vm.RefreshAsync().GetAwaiter().GetResult();
            Program.Check(!vm.SwapClients(first, last));
            var newcomer = new WindowIdentity(800, 800, 800);
            fake.Bounds[newcomer] = new(0, 0, 800, 600); fake.Placements[newcomer] = new(0, 1, 0, 0, 0, 0, fake.Bounds[newcomer]);
            vm.RefreshAsync().GetAwaiter().GetResult();
            Program.Check(vm.VisibleClients[^1].Identity == newcomer && vm.VisibleClients[^1].Slot == "05");
        });
    }
}
