using System.Diagnostics;
using System.IO;
using RobloxAfkManager.App.Remote;
using RobloxAfkManager.App.Services;
using RobloxAfkManager.App.ViewModels;
using RobloxAfkManager.Core;

namespace RobloxAfkManager.Tests;

internal static class AccountNameTests
{
    private static string JoinLine(long id) => $"2026-01-01T00:00:00Z,1,abc,6 [FLog::GameJoinLoadTime] Report game_join_loadtime: placeid:8, userid:{id}, time:3";
    public static void Register(Action<string, Action> test)
    {
        test("Account parser accepts only local join reports and uses the latest complete report", () =>
        {
            Program.Check(AccountNames.ReadLocalUserId("Other player userid:77\n[FLog::Network] userid:88") is null);
            Program.Check(AccountNames.ReadLocalUserId("[FLog::Output] " + JoinLine(77)) is null);
            Program.Check(AccountNames.ReadLocalUserId(JoinLine(77)) == 77);
            Program.Check(AccountNames.ReadLocalUserId(JoinLine(77) + "\r\n" + JoinLine(88)) == 88);
            Program.Check(AccountNames.ReadLocalUserId(JoinLine(0)) is null);
            Program.Check(AccountNames.ReadLocalUserId(JoinLine(77).Replace("userid:77,", "userid:77bad,")) is null);
        });
        test("Account lookup requires a unique process owner, excludes reused PIDs and caches public names", () =>
        {
            using var fixture = new NameFixture();
            var process = new ProcessIdentity(123, DateTime.UtcNow.AddHours(-1).Ticks);
            File.WriteAllText(fixture.Log, JoinLine(77));
            int requests = 0;
            IReadOnlyList<ProcessIdentity> owner = [process];
            var resolver = new LogAccountNameLookup(fixture.Directory, _ => owner, (ids, _) =>
            {
                requests++; Program.Check(ids.SequenceEqual([77L]));
                return Task.FromResult<IReadOnlyDictionary<long, string>>(new Dictionary<long, string> { [77] = "FixtureRanger" });
            });
            var names = resolver.ResolveAsync([process], default).GetAwaiter().GetResult();
            Program.Check(names[process] == "FixtureRanger" && requests == 1);
            resolver.ResolveAsync([process], default).GetAwaiter().GetResult(); Program.Check(requests == 1);
            owner = [process, new(124, process.StartTimeUtcTicks)];
            Program.Check(resolver.ResolveAsync(owner, default).GetAwaiter().GetResult().Count == 0);
            owner = [process];
            Program.Check(resolver.ResolveAsync([process with { StartTimeUtcTicks = process.StartTimeUtcTicks + 1 }], default).GetAwaiter().GetResult().Count == 0);
            Program.Check(requests == 1, "Ambiguous or stale process triggered a lookup");
        });
        test("A disconnect in the middle of a huge log is still found, and a later rejoin replaces it", () =>
        {
            using var fixture = new NameFixture(); var process = new ProcessIdentity(123, DateTime.UtcNow.AddHours(-1).Ticks);
            string Stamp(int minutesAgo) => DateTime.UtcNow.AddMinutes(-minutesAgo).ToString("O");
            string Noise() => string.Concat(Enumerable.Repeat("2026-01-01T00:00:00.000Z,1,abc,6 [FLog::Output] ordinary game output line that is not a session event" + (char)10, 9000));
            string lost = Stamp(30) + ",1,abc,7 [FLog::Network] Client has been disconnected with reason: Lost connection to the game server, please reconnect";
            File.WriteAllText(fixture.Log, JoinLine(77) + "\n" + Noise() + "\n" + lost + "\n" + Noise() + "\n");
            var resolver = new LogAccountNameLookup(fixture.Directory, _ => [process], (ids, _) => Task.FromResult<IReadOnlyDictionary<long, string>>(new Dictionary<long, string> { [77] = "Fixture" }));
            var seen = resolver.ResolveDetailsAsync([process], default).GetAwaiter().GetResult()[process].Session;
            Program.Check(seen.Phase == SessionPhase.Disconnected && seen.Reason is { } why && why.StartsWith("Lost connection"), "disconnect hidden by the middle of the log: " + seen.Phase);
            File.AppendAllText(fixture.Log, Stamp(1) + ",1,abc,6 [FLog::GameJoinLoadTime] Report game_join_loadtime: placeid:8, userid:77, universeid:18,\n" + Noise() + "\n");
            Program.Check(resolver.ResolveDetailsAsync([process], default).GetAwaiter().GetResult()[process].Session.Phase == SessionPhase.Joined, "rejoin after the disconnect was not seen");
        });
        test("Account lookup reads bounded log tails and tolerates offline public lookup", () =>
        {
            using var fixture = new NameFixture(); var process = new ProcessIdentity(123, DateTime.UtcNow.AddHours(-1).Ticks);
            File.WriteAllText(fixture.Log, JoinLine(77) + "\n" + new string('x', 400000) + "\n" + JoinLine(88));
            var resolver = new LogAccountNameLookup(fixture.Directory, _ => [process], (ids, _) =>
            {
                Program.Check(ids.SequenceEqual([88L]));
                throw new System.Net.Http.HttpRequestException("Synthetic offline response");
            });
            Program.Check(resolver.ResolveAsync([process], default).GetAwaiter().GetResult().Count == 0);
            File.WriteAllText(fixture.Log, "No local account identity");
            Program.Check(resolver.ResolveAsync([process], default).GetAwaiter().GetResult().Count == 0);
        });
        test("Custom account names persist only for the same process lifetime and clear cleanly", () =>
        {
            using var fixture = new NameFixture(); string path = Path.Combine(fixture.Directory, "names.json");
            var process = new ProcessIdentity(321, 10000); var store = new ClientNameStore(path);
            store.Set(process, "  Fixture Ranger  ");
            var reloaded = new ClientNameStore(path);
            Program.Check(reloaded.Get(process) == "Fixture Ranger");
            Program.Check(reloaded.Get(process with { StartTimeUtcTicks = 10001 }) is null);
            reloaded.Set(process, null);
            Program.Check(new ClientNameStore(path).Get(process) is null);
            File.WriteAllText(path, "invalid JSON"); Program.Check(new ClientNameStore(path).Get(process) is null);
            File.WriteAllText(path, "[null]"); Program.Check(new ClientNameStore(path).Get(process) is null);
        });
        test("Account labels follow swaps, refresh and ignore, update mobile labels, and allow automatic reset", () =>
        {
            var fake = new FakeWindowSystem(2); var store = new MemoryClientNameStore();
            using var vm = new MainViewModel(fake, new TestLog(), accountNameLookup: new ImmediateLookup(), clientNameStore: store);
            vm.RefreshAsync().GetAwaiter().GetResult(); var first = vm.Clients[0]; var second = vm.Clients[1];
            Program.Check(first.DetectedName == "FixtureRanger", "Automatic result was not applied to the view model");
            Program.Check(vm.SetClientName(first, "My farming account").Success);
            Program.Check(first.Label == "My farming account" && vm.Remote.Feed.Read(false).Clients.Single(c => c.ProcessId == first.ProcessId).Label == first.Label);
            vm.SwapClients(first, second); vm.RefreshAsync().GetAwaiter().GetResult();
            Program.Check(ReferenceEquals(vm.Clients[1], first) && first.Label == "My farming account");
            first.IsIgnored = true; Program.Check(vm.IgnoredClients.Single().Label == "My farming account");
            Program.Check(vm.Remote.Feed.Read(false).Clients.All(c => c.ProcessId != first.ProcessId));
            vm.UnignoreClientCommand.Execute(first);
            Program.Check(vm.SetClientName(first, "").Success && first.Label == "FixtureRanger");
            Program.Check(!vm.SetClientName(first, new string('x', 65)).Success);
            Program.Check(!vm.SetClientName(first, "line\nbreak").Success);
            Program.Check(!vm.SetClientName(first, "hidden\u202Eformat").Success);
            fake.Closed.Add(first.Identity); vm.RefreshAsync().GetAwaiter().GetResult();
            Program.Check(!vm.SetClientName(first, "Closed client").Success);
        });
        test("Late automatic account results cannot name a replacement process", () =>
        {
            var fake = new FakeWindowSystem(1); var lookup = new PendingLookup();
            using var vm = new MainViewModel(fake, new TestLog(), accountNameLookup: lookup);
            vm.RefreshAsync().GetAwaiter().GetResult();
            var old = vm.Clients.Single().Identity;
            fake.Closed.Add(old); var replacement = old with { StartTimeUtcTicks = old.StartTimeUtcTicks + 1 };
            fake.Bounds[replacement] = fake.Bounds[old]; fake.Placements[replacement] = fake.Placements[old];
            vm.RefreshAsync().GetAwaiter().GetResult();
            lookup.Completion.SetResult(new Dictionary<ProcessIdentity, string> { [ProcessIdentity.From(old)] = "OldAccount" });
            Program.Check(vm.Clients.Single().Label == "Client 02" && vm.Clients.Single().DetectedName is null);
        });
    }

    public static void NativeOwnership()
    {
        using var fixture = new NameFixture();
        using var stream = new FileStream(fixture.Log, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        using var process = Process.GetCurrentProcess();
        var expected = new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks);
        Program.Check(LogFileOwners.Read(fixture.Log).Contains(expected), "Restart Manager did not match the held log to its exact process identity");
        var bytes = System.Text.Encoding.UTF8.GetBytes(JoinLine(77)); stream.Write(bytes); stream.Flush();
        var resolver = new LogAccountNameLookup(fixture.Directory, usernames: (ids, _) =>
            Task.FromResult<IReadOnlyDictionary<long, string>>(ids.ToDictionary(id => id, _ => "FixtureRanger")));
        Program.Check(resolver.ResolveAsync([expected], default).GetAwaiter().GetResult().GetValueOrDefault(expected) == "FixtureRanger",
            "Native owner-to-account lookup did not resolve the synthetic fixture");
    }
    public static void PublicLookup()
    {
        var names = PublicRobloxUsers.LookupAsync([1], default).GetAwaiter().GetResult();
        Program.Check(names.TryGetValue(1, out string? name) && !string.IsNullOrWhiteSpace(name), "Public Roblox username endpoint returned no result");
    }
    private sealed class PendingLookup : IAccountNameLookup
    {
        public TaskCompletionSource<IReadOnlyDictionary<ProcessIdentity, string>> Completion { get; } = new();
        public Task<IReadOnlyDictionary<ProcessIdentity, string>> ResolveAsync(IReadOnlyList<ProcessIdentity> processes, CancellationToken cancellationToken) => Completion.Task;
    }
    private sealed class ImmediateLookup : IAccountNameLookup
    {
        public Task<IReadOnlyDictionary<ProcessIdentity, string>> ResolveAsync(IReadOnlyList<ProcessIdentity> processes, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<ProcessIdentity, string>>(processes.ToDictionary(p => p, _ => "FixtureRanger"));
    }
    internal sealed class EmptyLookup : IAccountNameLookup
    {
        public Task<IReadOnlyDictionary<ProcessIdentity, string>> ResolveAsync(IReadOnlyList<ProcessIdentity> processes, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<ProcessIdentity, string>>(new Dictionary<ProcessIdentity, string>());
    }
    private sealed class NameFixture : IDisposable
    {
        public string Directory { get; } = Path.GetFullPath(Path.Combine("artifacts", "name-test-" + Guid.NewGuid().ToString("N")));
        public string Log => Path.Combine(Directory, "Fixture_Player_test_last.log");
        public NameFixture() => System.IO.Directory.CreateDirectory(Directory);
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
