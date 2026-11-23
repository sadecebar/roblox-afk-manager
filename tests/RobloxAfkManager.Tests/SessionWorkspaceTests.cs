using System.IO;
using RobloxAfkManager.App.Services;
using RobloxAfkManager.App.ViewModels;
using RobloxAfkManager.Core;

namespace RobloxAfkManager.Tests;

internal static class SessionWorkspaceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly ProcessIdentity Process = new(123, Now.AddHours(-1).UtcTicks);
    private static string Line(DateTimeOffset at, string tag, string message) => $"{at:O},1,abc,6 [FLog::{tag}] {message}";
    private static string Join(DateTimeOffset at, long user = 77) => Line(at, "GameJoinLoadTime", $"Report game_join_loadtime: placeid:8, userid:{user}, universeid:18,");
    private static SessionReport Report(SessionPhase phase, DateTimeOffset? at = null) => new(77, 8, 18, phase, phase == SessionPhase.Joined ? at ?? Now : null, at ?? Now, Now);
    public static void Register(Action<string, Action> test)
    {
        test("Session parser uses explicit engine events, ignores game output and rejects stale/future timestamps", () =>
        {
            var joined = SessionLogParser.Read(Join(Now.AddMinutes(-8)), Process, Now);
            Program.Check(joined.UserId == 77 && joined.PlaceId == 8 && joined.UniverseId == 18 && joined.JoinedAt == Now.AddMinutes(-8) && joined.Phase == SessionPhase.Joined);
            string spoof = Line(Now, "Output", "[FLog::Network] Client:Disconnect");
            Program.Check(SessionLogParser.Read(Join(Now) + "\n" + spoof, Process, Now).Phase == SessionPhase.Joined);
            Program.Check(SessionLogParser.Read(Join(Now.AddHours(-2)) + "\n" + Join(Now.AddHours(2)), Process, Now).Phase == SessionPhase.Unknown);
            Program.Check(SessionLogParser.Read("userid:77 placeid:8", Process, Now).UserId is null);
        });
        test("Session parser resets duration on rejoin and distinguishes joining from a completed join", () =>
        {
            string text = Join(Now.AddMinutes(-20)) + "\n" + Line(Now.AddMinutes(-2), "Network", "Client:Disconnect");
            var lost = SessionLogParser.Read(text, Process, Now); Program.Check(lost.Phase == SessionPhase.Disconnected && lost.UserId == 77);
            text += "\n" + Line(Now.AddMinutes(-1), "GameJoinUtil", "GameJoinUtil::joinGamePostStandard: URL: omitted");
            var joining = SessionLogParser.Read(text, Process, Now); Program.Check(joining.Phase == SessionPhase.Joining && joining.JoinedAt is null && joining.PlaceId is null);
            var rejoined = SessionLogParser.Read(text + "\n" + Join(Now), Process, Now); Program.Check(rejoined.Phase == SessionPhase.Joined && rejoined.JoinedAt == Now);
        });
        test("Health alerts debounce hangs and window disappearance, and record recovery once", () =>
        {
            var tracker = new SessionHealthTracker(); var client = new ClientHealth(Process, "Fixture", false, "Not responding", null);
            Program.Check(tracker.Observe([client], Now).Count == 0);
            Program.Check(tracker.Observe([client], Now.AddSeconds(19)).Count == 0);
            Program.Check(tracker.Observe([client], Now.AddSeconds(20)).Single().Warning);
            Program.Check(tracker.Observe([client], Now.AddSeconds(30)).Count == 0);
            Program.Check(!tracker.Observe([client with { WindowStatus = "Open" }], Now.AddSeconds(40)).Single().Warning);
            Program.Check(tracker.Observe([], Now.AddSeconds(50)).Count == 0);
            Program.Check(tracker.Observe([client with { WindowStatus = "Open" }], Now.AddSeconds(55)).Count == 0);
            tracker.Observe([], Now.AddSeconds(60));
            Program.Check(tracker.Observe([], Now.AddSeconds(75)).Single().Message.Contains("no longer detected"));
            Program.Check(tracker.Observe([], Now.AddSeconds(90)).Count == 0);
        });
        test("Health alerts skip ignored clients and stale evidence; disconnect and possible-join alerts deduplicate", () =>
        {
            var tracker = new SessionHealthTracker(); var client = new ClientHealth(Process, "Fixture", true, "Open", Report(SessionPhase.Disconnected));
            Program.Check(tracker.Observe([client], Now).Count == 0 && tracker.Observe([], Now.AddMinutes(1)).Count == 0);
            client = client with { Ignored = false };
            Program.Check(tracker.Observe([client], Now).Count == 1 && tracker.Observe([client], Now.AddSeconds(30)).Count == 0);
            Program.Check(tracker.Observe([client with { Session = Report(SessionPhase.Joined, Now.AddSeconds(40)) }], Now.AddSeconds(40)).Single().Warning == false);
            var waiting = client with { Session = Report(SessionPhase.Joining, Now.AddMinutes(-3)) };
            Program.Check(tracker.Observe([waiting], Now).Single().Message.StartsWith("Possibly"));
            Program.Check(tracker.Observe([waiting], Now.AddSeconds(30)).Count == 0);
            var stale = client with { Process = new(124, Process.StartTimeUtcTicks), Session = Report(SessionPhase.Disconnected) with { CheckedAt = Now.AddMinutes(-5) } };
            Program.Check(tracker.Observe([waiting, stale], Now.AddSeconds(35)).Count == 0);
        });
        test("Workspace store persists bounded profiles/history and tolerates corrupt files", () =>
        {
            string folder = Path.GetFullPath(Path.Combine("artifacts", "workspace-test-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(folder);
            try
            {
                string path = Path.Combine(folder, "workspace.json"); var store = new WorkspaceStore(path);
                store.Write(new(Enumerable.Range(1, 150).Select(i => new AccountProfile(i, "Fixture", i, i == 1, ResourcePlan.Low)).ToArray(),
                    Enumerable.Range(0, 130).Select(i => new ActivityEntry(Now.AddSeconds(i), "Fixture", "Test event", true)).ToArray(), false));
                var saved = new WorkspaceStore(path).Read(); Program.Check(saved.Profiles.Count == 128 && saved.History.Count == 100 && !saved.Notifications && saved.Profiles[0].Ignored);
                Program.Check(!File.Exists(path + ".tmp"));
                File.WriteAllText(path, "{\"Profiles\":[null],\"History\":[null]}"); Program.Check(store.Read().Profiles.Count == 0 && store.Read().History.Count == 0);
                File.WriteAllText(path, "invalid"); Program.Check(store.Read().Profiles.Count == 0);
            }
            finally { Directory.Delete(folder, true); }
        });
        test("Saved profiles restore names/order/ignore/plan to new process identities without touching ignored clients", () =>
        {
            var store = new MemoryWorkspaceStore(); store.Write(new([new(77, "Main fixture", 1, true, ResourcePlan.High), new(88, "Alt fixture", 0, false, ResourcePlan.Low)], []));
            var fake = new FakeWindowSystem(2); var resources = new FakeProcessResources(fake); var memory = new FakeWorkingSets(fake);
            using var vm = new MainViewModel(fake, new TestLog(), resources, memory, new DetailsLookup(), workspaceStore: store);
            Ready(vm);
            Program.Check(vm.Clients[0].AccountId == 88 && vm.Clients[0].Label == "Alt fixture");
            Program.Check(vm.VisibleClients.Count == 1 && vm.IgnoredClients.Single().AccountId == 77);
            Program.Check(resources.Writes.All(w => w.Id.ProcessId == 102) && resources.Writes.Count == 1 && memory.Writes.Count == 0);
            Program.Check(resources.States[ProcessIdentity.From(vm.VisibleClients[0].Identity)].PriorityClass == 0x4000);
            Program.Check(vm.Remote.Feed.Read(false).Clients.Single().GameName == "Fixture game");
            Program.Check(fake.Moved.Count == 0 && fake.Focused.Count == 0);
        });
        test("Profile edits survive manager and Roblox restarts; normal shutdown keeps preferred plans", () =>
        {
            var store = new MemoryWorkspaceStore(); var fake = new FakeWindowSystem(2);
            var resources = new FakeProcessResources(fake);
            using (var vm = new MainViewModel(fake, new TestLog(), resources, new FakeWorkingSets(fake), new DetailsLookup(), workspaceStore: store))
            {
                Ready(vm); var first = vm.Clients[0]; var second = vm.Clients[1];
                vm.SetClientName(first, "Saved alias"); vm.SwapClients(first, second);
                vm.SelectedResourcePlan = ResourcePlan.Low; vm.ApplyResourcePlanCommand.Execute(null); Wait(() => !vm.Busy);
                Wait(() => store.Read().Profiles.All(p => p.PreferredPlan == ResourcePlan.Low));
                Program.Check(vm.StopResourcePlansAsync(false).GetAwaiter().GetResult());
            }
            var restarted = new FakeWindowSystem(2);
            foreach (var id in restarted.Placements.Keys.ToArray())
            {
                var next = id with { StartTimeUtcTicks = id.StartTimeUtcTicks + 5000 };
                restarted.Placements[next] = restarted.Placements[id]; restarted.Bounds[next] = restarted.Bounds[id]; restarted.Closed.Add(id);
            }
            using var reopened = new MainViewModel(restarted, new TestLog(), new FakeProcessResources(restarted), new FakeWorkingSets(restarted), new DetailsLookup(), workspaceStore: store);
            Ready(reopened);
            Program.Check(reopened.Clients[0].AccountId == 88 && reopened.Clients[1].Label == "Saved alias");
            Program.Check(reopened.Clients.All(c => c.PlanLabel == "Low"));
            Program.Check(reopened.StopResourcePlansAsync().GetAwaiter().GetResult());
            Program.Check(store.Read().Profiles.All(p => p.PreferredPlan is null));
        });
        test("Unidentified clients cannot leak to mobile or bulk resource actions before saved ignores are restored", () =>
        {
            var store = new MemoryWorkspaceStore(); store.Write(new([new(77, null, 0, true, null)], []));
            var lookup = new PendingDetails(); var fake = new FakeWindowSystem(2); var resources = new FakeProcessResources(fake);
            using var vm = new MainViewModel(fake, new TestLog(), resources, new FakeWorkingSets(fake), lookup, workspaceStore: store);
            vm.RefreshAsync().GetAwaiter().GetResult();
            Program.Check(vm.PendingProfileCount == 2 && vm.VisibleClients.Count == 0 && vm.Remote.Feed.Read(false).Clients.Count == 0);
            Program.Check(!vm.ApplyResourcePlanCommand.CanExecute(null) && !vm.PrepareDashboardCommand.CanExecute(null));
            lookup.Done.SetResult(DetailsLookup.Make(fake.Detect().Select(c => ProcessIdentity.From(c.Identity)).ToArray()));
            Wait(() => !vm.IsResolvingAccounts);
            Program.Check(vm.VisibleClients.Count == 1 && vm.IgnoredClients.Count == 1 && resources.Writes.Count == 0);
        });
        test("A manual name typed during an account lookup takes precedence over an older saved profile", () =>
        {
            var store = new MemoryWorkspaceStore(); store.Write(new([new(77, "Old alias", 0, false, null)], []));
            var lookup = new PendingDetails(); var fake = new FakeWindowSystem(1);
            using var vm = new MainViewModel(fake, new TestLog(), new FakeProcessResources(fake), new FakeWorkingSets(fake), lookup, workspaceStore: store);
            vm.RefreshAsync().GetAwaiter().GetResult(); vm.SetClientName(vm.Clients[0], "New alias");
            lookup.Done.SetResult(DetailsLookup.Make(fake.Detect().Select(c => ProcessIdentity.From(c.Identity)).ToArray())); Wait(() => !vm.IsResolvingAccounts);
            Program.Check(vm.Clients[0].Label == "New alias" && store.Read().Profiles.Single().CustomName == "New alias");
        });
        test("Cards distinguish log session time, unknown state, disconnected state and unavailable avatars", () =>
        {
            var fake = new FakeWindowSystem(1); var tile = new ClientTileViewModel(fake.Detect()[0], 1);
            tile.SetAccountDetails(new(Report(SessionPhase.Joined, Now.AddMinutes(-75)), "Fixture", "Fixture game", null)); tile.UpdateSessionClock(Now);
            Program.Check(tile.SessionDuration == "1h 15m" && tile.SessionStatus == "Joined (log)" && tile.Avatar is null && tile.ProfileHint.Contains("77"));
            tile.SetAccountDetails(new(Report(SessionPhase.Disconnected), "Fixture", "Fixture game", null)); tile.UpdateSessionClock(Now);
            Program.Check(tile.NeedsAttention && tile.SessionDuration == "" && tile.SessionStatus == "Disconnected (log)");
            tile.UpdateSessionClock(Now.AddMinutes(5)); Program.Check(tile.SessionStatus == "Session unknown");
        });
        test("Offline saved plans can be reset without being treated as active Windows changes", () =>
        {
            var store = new MemoryWorkspaceStore(); store.Write(new([new(77, null, 0, true, ResourcePlan.Low)], []));
            using var vm = new MainViewModel(new FakeWindowSystem(0), new TestLog(), workspaceStore: store);
            Program.Check(!vm.HasResourceChanges && vm.HasSavedPlans && vm.ResetResourcePlanCommand.CanExecute(null));
            Program.Check(vm.StopResourcePlansAsync().GetAwaiter().GetResult() && !vm.HasSavedPlans);
        });
        test("Offline roster lists recent, non-ignored, non-running accounts with name fallbacks", () =>
        {
            var profiles = new AccountProfile[]
            {
                new(1, "Custom", 2, false, null, "user1", "Game A", Now.AddMinutes(-14)),
                new(2, null, 1, false, null, "user2", null, Now.AddHours(-3)),
                new(3, null, 3, true, null, "ignored", null, Now.AddMinutes(-5)),
                new(4, null, 4, false, null, "running", null, Now),
                new(5, null, 5, false, null, "stale", null, Now.AddHours(-25)),
                new(6, null, 6, false, null, null, null, Now.AddMinutes(-1)),
                new(7, null, 7, false, null, "never", null, null),
            };
            var offline = AccountRoster.Offline(profiles, new HashSet<long> { 4 }, Now);
            Program.Check(offline.Select(o => o.UserId).SequenceEqual(new long[] { 2, 1, 6 }), "Offline roster selection or order is wrong");
            Program.Check(offline[1].Name == "Custom" && offline[0].Name == "user2" && offline[2].Name == "Account 6");
            Program.Check(AccountRoster.Ago(Now.AddMinutes(-14), Now) == "14 min ago" && AccountRoster.Ago(Now.AddSeconds(-20), Now) == "just now");
        });
        test("A closed account appears offline, survives restart, and Forget clears it until it runs again", () =>
        {
            var store = new MemoryWorkspaceStore(); var fake = new FakeWindowSystem(2);
            using (var vm = new MainViewModel(fake, new TestLog(), new FakeProcessResources(fake), new FakeWorkingSets(fake), new DetailsLookup(), workspaceStore: store))
            {
                Ready(vm);
                Program.Check(vm.OfflineCount == 0 && vm.HealthTone == "Good", "Running accounts must not be listed offline");
                var closed = vm.Clients.Single(c => c.AccountId == 77);
                fake.Closed.Add(closed.Identity); Ready(vm);
                Program.Check(vm.OfflineCount == 1 && vm.OfflineAccounts.Single().Name == "Fixture101", "Closed account was not listed offline");
                Program.Check(vm.OfflineAccounts.Single().Details.Contains("Fixture game") && vm.HealthSummary == "1 offline" && vm.HealthTone == "Warn");
                Program.Check(store.Read().Profiles.Single(p => p.UserId == 77) is { Username: "Fixture101", LastSeen: not null });
            }
            var reopenedWindows = new FakeWindowSystem(2); reopenedWindows.Closed.Add(reopenedWindows.Placements.Keys.Single(k => k.ProcessId == 101));
            using (var reopened = new MainViewModel(reopenedWindows, new TestLog(), new FakeProcessResources(reopenedWindows), new FakeWorkingSets(reopenedWindows), new DetailsLookup(), workspaceStore: store))
            {
                Ready(reopened);
                Program.Check(reopened.OfflineAccounts.Single().UserId == 77, "Offline account was not remembered across a manager restart");
                reopened.ForgetOfflineCommand.Execute(reopened.OfflineAccounts.Single());
                Program.Check(reopened.OfflineCount == 0 && store.Read().Profiles.Single(p => p.UserId == 77).LastSeen is null, "Forget did not clear the account");
                reopenedWindows.Closed.Clear();
                reopened.RefreshAsync(manual: true).GetAwaiter().GetResult(); Wait(() => !reopened.IsResolvingAccounts && !reopened.Busy && reopened.Clients.All(c => c.AccountId is not null));
                Program.Check(store.Read().Profiles.Single(p => p.UserId == 77).LastSeen is not null, "A forgotten account was not tracked again after running");
            }
        });
        test("Late account details cannot restore a profile onto a replacement process", () =>
        {
            var store = new MemoryWorkspaceStore(); store.Write(new([new(77, "Old profile", 0, true, ResourcePlan.Low)], []));
            var lookup = new PendingDetails(); var fake = new FakeWindowSystem(1); var resources = new FakeProcessResources(fake);
            using var vm = new MainViewModel(fake, new TestLog(), resources, new FakeWorkingSets(fake), lookup, workspaceStore: store);
            vm.RefreshAsync().GetAwaiter().GetResult(); var old = fake.Detect().Single().Identity;
            fake.Closed.Add(old); var replacement = old with { StartTimeUtcTicks = old.StartTimeUtcTicks + 1 };
            fake.Placements[replacement] = fake.Placements[old]; fake.Bounds[replacement] = fake.Bounds[old];
            vm.RefreshAsync().GetAwaiter().GetResult();
            lookup.Done.SetResult(DetailsLookup.Make([ProcessIdentity.From(old)])); Wait(() => !vm.IsResolvingAccounts);
            Program.Check(vm.Clients.Single().AccountId is null && !vm.Clients.Single().IsIgnored && resources.Writes.Count == 0);
            Program.Check(vm.VisibleClients.Count == 0, "Unidentified replacement should wait for its own profile match");
        });
        test("Avatar URLs and PNG dimensions are restricted before decoding", () =>
        {
            Program.Check(PublicAccountArtwork.IsAvatarUrl("https://tr.rbxcdn.com/test.png", out _));
            foreach (string url in new[] { "http://tr.rbxcdn.com/test", "https://rbxcdn.com.example/test", "https://localhost/test", "https://tr.rbxcdn.com:8443/test", new UriBuilder("https://tr.rbxcdn.com/test") { UserName = "fixture" }.Uri.AbsoluteUri })
                Program.Check(!PublicAccountArtwork.IsAvatarUrl(url, out _));
            Program.Check(!PublicAccountArtwork.ValidPng(new byte[24]));
            Program.Check(PublicAccountArtwork.Decode(new byte[24]) is null);
        });
    }
    private static void Ready(MainViewModel vm) { vm.RefreshAsync().GetAwaiter().GetResult(); Wait(() => !vm.IsResolvingAccounts && !vm.Busy); }
    public static void PublicArtwork()
    {
        var service = new PublicAccountArtwork();
        service.RefreshAsync([new(1, 1818, 13058, SessionPhase.Joined, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)], default).GetAwaiter().GetResult();
        Program.Check(service.Avatar(1) is { Length: > 0 }, "Public avatar endpoint did not return a completed image");
        Program.Check(PublicAccountArtwork.Decode(service.Avatar(1)) is { IsFrozen: true }, "Public avatar failed bounded decode");
        Program.Check(!string.IsNullOrWhiteSpace(service.Game(13058)), "Public game endpoint did not return a game name");
    }
    public static void ReadOnlyLiveDetails()
    {
        var windows = new Win32WindowSystem(new TestLog());
        var before = windows.Detect();
        Program.Check(before.Count > 0, "No live clients for the optional read-only account-details check");
        var placements = before.ToDictionary(c => c.Identity, c => windows.Capture(c.Identity));
        var provider = new LogAccountNameLookup();
        var details = provider.ResolveDetailsAsync(before.Select(c => ProcessIdentity.From(c.Identity)).ToArray(), default).GetAwaiter().GetResult();
        Program.Check(details.Count > 0, "No log could be uniquely matched to a live client");
        Program.Check(details.Values.Any(d => d.Session.UserId > 0 && d.Username is not null), "Live public username lookup returned no names");
        Program.Check(details.Values.Any(d => d.Session.PlaceId > 0 && d.GameName is not null), "Live game details were unavailable");
        foreach (var pair in placements.Where(p => windows.IsCurrent(p.Key))) Program.Check(windows.Capture(pair.Key) == pair.Value, "Read-only lookup moved a game");
        Console.WriteLine($"Read-only account lookup: {details.Count}/{before.Count} identified; {details.Values.Count(d => d.Username is not null)} names; {details.Values.Count(d => d.GameName is not null)} games; {details.Values.Count(d => d.AvatarPng is not null)} avatars. No names, IDs or log contents printed; no settings written.");
    }
    private static void Wait(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!ready() && DateTime.UtcNow < deadline) Thread.Sleep(10);
        Program.Check(ready(), "Timed out waiting for synthetic account refresh");
    }
    private sealed class DetailsLookup : IAccountDetailsLookup
    {
        public static IReadOnlyDictionary<ProcessIdentity, AccountDetails> Make(IReadOnlyList<ProcessIdentity> processes) => processes.ToDictionary(p => p, p =>
            new AccountDetails(new(p.ProcessId == 101 ? 77 : 88, 8, 18, SessionPhase.Joined, DateTimeOffset.UtcNow.AddMinutes(-3), DateTimeOffset.UtcNow.AddMinutes(-3), DateTimeOffset.UtcNow), "Fixture" + p.ProcessId, "Fixture game", null));
        public Task<IReadOnlyDictionary<ProcessIdentity, AccountDetails>> ResolveDetailsAsync(IReadOnlyList<ProcessIdentity> processes, CancellationToken cancellationToken) => Task.FromResult(Make(processes));
        public Task<IReadOnlyDictionary<ProcessIdentity, string>> ResolveAsync(IReadOnlyList<ProcessIdentity> processes, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class PendingDetails : IAccountDetailsLookup
    {
        public TaskCompletionSource<IReadOnlyDictionary<ProcessIdentity, AccountDetails>> Done { get; } = new();
        public Task<IReadOnlyDictionary<ProcessIdentity, AccountDetails>> ResolveDetailsAsync(IReadOnlyList<ProcessIdentity> processes, CancellationToken cancellationToken) => Done.Task;
        public Task<IReadOnlyDictionary<ProcessIdentity, string>> ResolveAsync(IReadOnlyList<ProcessIdentity> processes, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
