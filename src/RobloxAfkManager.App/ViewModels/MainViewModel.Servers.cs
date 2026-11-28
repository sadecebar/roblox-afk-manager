using RobloxAfkManager.App.Services;
using RobloxAfkManager.Core;

namespace RobloxAfkManager.App.ViewModels;

// Two private servers: the first N accounts (in dashboard order) join server 1, the rest join server 2. Every way of
// starting an account goes through PlanLaunchAsync, so opening, restarting and reopening all land on the right server.
// Reset servers closes every running account that can be reopened, waits a minute so the private servers empty and
// reset, then opens every saved account that is not running.
public sealed partial class MainViewModel
{
    private sealed record StoredServers(string? Server1, string? Server2, int FirstCount, Dictionary<long, int>? Assignments = null, long Place = 0);
    private sealed record LaunchPlan(long PlaceId, string? LinkCode, int Server);
    private static readonly TimeSpan ResolvedServerLife = TimeSpan.FromMinutes(30);

    private string? serverCode1, serverCode2;
    private int firstServerCount = ServerAssignment.DefaultFirstCount;
    private Dictionary<long, int> serverAssignments = [];
    private readonly Dictionary<string, (PrivateServer Server, DateTimeOffset At)> resolvedServers = [];
    private readonly HashSet<long> reconnectingIds = [];
    private readonly HashSet<string> reconnectingLabels = [];
    private string ServersFile => System.IO.Path.Combine(settingsFolder, "servers.json");

    // How long Reset servers waits, after the last account has left, for the private servers to empty and reset.
    public TimeSpan ResetDelay { get; set; } = TimeSpan.FromMinutes(1);
    public AsyncCommand ResetServersCommand { get; private set; } = null!;
    public AsyncCommand ResplitServersCommand { get; private set; } = null!;
    // The window asks "close N accounts and reset the servers?" before anything is closed. Not set in tests, which proceed.
    public Func<int, bool>? ConfirmReset { get; set; }

    // Something to open: a public game, or at least one private server.
    public bool HasLaunchTarget => gamePlaceId > 0 || serverCode1 is not null || serverCode2 is not null;

    public string PrivateServer1Link { get => LinkFor(serverCode1); set => SetServerLink(ref serverCode1, value, 1); }
    public string PrivateServer2Link { get => LinkFor(serverCode2); set => SetServerLink(ref serverCode2, value, 2); }
    private static string LinkFor(string? code) => code is null ? "" : $"https://www.roblox.com/share?code={code}&type=Server";
    public int AccountsOnServer1
    {
        get => firstServerCount;
        set { int clamped = Math.Clamp(value, 1, 128); if (clamped != firstServerCount) { firstServerCount = clamped; serverAssignments.Clear(); SaveServers(); } RaiseServers(); RefreshLogins(); }
    }
    public string ServerSummary => serverCode1 is null && serverCode2 is null ? "No private servers set: accounts open the game above on a public server."
        : serverCode1 is null ^ serverCode2 is null ? $"One private server set: every account joins private server {(serverCode1 is null ? 2 : 1)}."
        : $"The first {firstServerCount} accounts (in dashboard order) join private server 1, the rest join private server 2.";

    private void SetServerLink(ref string? field, string? value, int number)
    {
        if (string.IsNullOrWhiteSpace(value)) { field = null; Warning = false; Message = $"Private server {number} link removed."; }
        else if (PrivateServerLink.ParseCode(value) is { } code) { field = code; Warning = false; Message = $"Private server {number} saved."; }
        else { Warning = true; Message = "That isn't a private server share link (it looks like roblox.com/share?code=…&type=Server)."; RaiseServers(); return; }
        resolvedServers.Clear(); privateFailedAt.Clear(); SaveServers(); RaiseServers(); RefreshLogins();
    }

    private void InitializeServers()
    {
        try
        {
            if (System.IO.File.Exists(ServersFile) && new System.IO.FileInfo(ServersFile).Length < 4096 &&
                System.Text.Json.JsonSerializer.Deserialize<StoredServers>(System.IO.File.ReadAllText(ServersFile)) is { } stored)
            {
                serverCode1 = PrivateServerLink.ParseCode(stored.Server1); serverCode2 = PrivateServerLink.ParseCode(stored.Server2);
                firstServerCount = Math.Clamp(stored.FirstCount, 1, 128);
                serverAssignments = (stored.Assignments ?? []).Where(p => p.Value is 1 or 2).ToDictionary(p => p.Key, p => p.Value);
                lastKnownPlace = stored.Place > 0 ? stored.Place : 0;
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        ResetServersCommand = new(ResetServersAsync, CanResetServers, HandleError);
        ResplitServersCommand = new(() => { ResplitAccounts(); return Task.CompletedTask; }, () => !launching, HandleError);
    }
    private void SaveServers()
    {
        try
        {
            System.IO.Directory.CreateDirectory(settingsFolder);
            System.IO.File.WriteAllText(ServersFile, System.Text.Json.JsonSerializer.Serialize(new StoredServers(serverCode1, serverCode2, firstServerCount, serverAssignments, lastKnownPlace)));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { Warning = true; Message = "Could not save the private servers: " + ex.Message; }
    }
    private void RaiseServers()
    {
        Raise(nameof(PrivateServer1Link)); Raise(nameof(PrivateServer2Link)); Raise(nameof(AccountsOnServer1)); Raise(nameof(ServerSummary)); Raise(nameof(HasLaunchTarget));
        Raise(nameof(HasClosedLogins)); Raise(nameof(OpenClosedLabel)); OpenClosedCommand?.Notify(); OpenSelectedCommand?.Notify(); OpenLoginCommand?.Notify(); ResetServersCommand?.Notify();
    }

    // Every saved account, in the order the dashboard shows them (running ones as they appear on screen, the rest by saved order).
    private List<long> DashboardOrder()
    {
        var saved = vault.Logins.Select(l => l.UserId).ToHashSet();
        var shown = Clients.Where(c => c.AccountId is { } id && saved.Contains(id)).Select(c => c.AccountId!.Value).Distinct().ToList();
        shown.AddRange(saved.Except(shown).OrderBy(id => profiles.TryGetValue(id, out var p) ? p.Order : int.MaxValue).ThenBy(id => id));
        return shown;
    }

    // Gives every saved account its server (kept for good once an account is launched) and returns it. Without commit nothing is
    // remembered, so the list can show a preview before the first launch decides.
    private IReadOnlyDictionary<long, int> Assignments(bool commit)
    {
        var assigned = ServerAssignment.Assign(DashboardOrder(), firstServerCount, serverAssignments);
        if (commit && !assigned.OrderBy(p => p.Key).SequenceEqual(serverAssignments.OrderBy(p => p.Key)))
        { serverAssignments = new(assigned); SaveServers(); }
        return assigned;
    }

    // Starts the split over from the dashboard order as it is now.
    private void ResplitAccounts()
    {
        serverAssignments.Clear(); privateFailedAt.Clear(); Assignments(commit: true); RaiseServers(); RefreshLogins();
        var counts = serverAssignments.Values.GroupBy(v => v).ToDictionary(g => g.Key, g => g.Count());
        Warning = false; Message = $"Accounts split again: {counts.GetValueOrDefault(1)} on private server 1, {counts.GetValueOrDefault(2)} on private server 2.";
    }

    // 0 when no private server is configured (the public game is used).
    private int ServerNumberFor(long userId, bool commit = false)
    {
        if (serverCode1 is null && serverCode2 is null) return 0;
        if (serverCode1 is null) return 2;
        if (serverCode2 is null) return 1;
        return Assignments(commit).GetValueOrDefault(userId, 2);
    }

    // Where this account should go. Asks Roblox to resolve a private server's share link the first time (and every 30 minutes).
    private async Task<LaunchPlan> PlanLaunchAsync(long userId, string cookie)
    {
        int server = ServerNumberFor(userId, commit: true);
        if (server == 0)
        {
            if (gamePlaceId > 0) return new(gamePlaceId, null, 0);
            throw new InvalidOperationException("Set a game or a private server link first (Settings → Accounts).");
        }
        string code = (server == 1 ? serverCode1 : serverCode2)!;
        var now = DateTimeOffset.UtcNow;
        // After a private join failed for this account it joins the game's public server for a while instead of trying again.
        if (FallbackPlace > 0 && UsePublicFor(userId, now)) return new(FallbackPlace, null, 0);
        if (!resolvedServers.TryGetValue(code, out var known) || now - known.At > ResolvedServerLife)
        {
            try
            {
                var resolved = await launcher.ResolveShareLinkAsync(cookie, code, CancellationToken.None);
                resolvedServers[code] = known = (resolved, now);
                if (lastKnownPlace != resolved.PlaceId) { lastKnownPlace = resolved.PlaceId; SaveServers(); }
            }
            catch (Exception ex) when (FallbackPlace > 0 && ex is not OperationCanceledException)
            {
                privateFailedAt[userId] = now;
                AddActivity(NameOf(userId), $"Could not use private server {server}: {ex.Message} Fix: it joins the public server of the game instead, and tries the private server again in {(int)PublicFallbackLife.TotalMinutes} minutes.", true, announce: true, quiet: true);
                return new(FallbackPlace, null, 0);
            }
        }
        return new(known.Server.PlaceId, known.Server.LinkCode, server);
    }

    // Two accounts that start at the same moment can get mixed up in the game (one is logged in as the other), so every
    // launch, from any button or automatic fix, waits until the previous one finished and the spacing has passed.
    // The account is checked again after the wait: it may have been started by something else in the meantime.
    private readonly SemaphoreSlim launchGate = new(1, 1);
    private DateTimeOffset lastLaunchEnd = DateTimeOffset.MinValue;
    private async Task<int> LaunchOneAsync(string cookie, LaunchPlan plan, string? ticket, long account, bool replacing = false)
    {
        await launchGate.WaitAsync();
        try
        {
            var wait = lastLaunchEnd + LaunchSpacing - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
            if (!replacing && IsActive(account, DateTimeOffset.UtcNow)) throw new InvalidOperationException("It is already running or still starting, so it was not started a second time.");
            int started = await launcher.LaunchSafelyAsync(cookie, plan.PlaceId, CancellationToken.None, ticket, plan.LinkCode);
            if (plan.LinkCode is not null) launchedPrivate.Add(account); else launchedPrivate.Remove(account);
            return started;
        }
        finally { lastLaunchEnd = DateTimeOffset.UtcNow; launchGate.Release(); }
    }

    private bool CanResetServers() => HasLaunchTarget && !launching && !recovering && !reopening && !Busy && !disposed
        && Clients.Any(c => !c.IsIgnored && c.AccountId is { } id && vault.Logins.Any(l => l.UserId == id));

    private async Task ResetServersAsync()
    {
        if (!CanResetServers()) return;
        var saved = vault.Logins.Select(l => l.UserId).ToHashSet();
        var candidates = Clients.Where(c => !c.IsIgnored && c.AccountId is { } id && saved.Contains(id))
            .Select(c => (Id: c.AccountId!.Value, c.Label, c.Identity)).ToArray();
        if (ConfirmReset is { } ask && !ask(candidates.Length)) return;
        launching = true; privateFailedAt.Clear(); RaiseServers();
        var targets = new List<(long Id, string Label, WindowIdentity Identity)>(); var skipped = new List<string>();
        try
        {
            // An account is only closed if it can certainly be reopened: its login works and its server link resolves.
            foreach (var c in candidates)
            {
                try
                {
                    string cookie = vault.Cookie(c.Id) ?? throw new InvalidOperationException("The saved login could not be read.");
                    await PlanLaunchAsync(c.Id, cookie);
                    targets.Add(c);
                }
                catch (Exception ex) { skipped.Add($"{c.Label}: {ex.Message}"); log.Write($"Reset servers skipped account {c.Id}.", ex); }
            }
            if (targets.Count == 0)
            {
                Warning = true; Message = "Reset servers did nothing: " + (skipped.Count > 0 ? skipped[0] : "no running account has a saved login.");
                if (skipped.Count > 0) AddActivity("Reset servers", "Cancelled, nothing was closed. " + skipped[0], true, announce: false);
                return;
            }

            reconnectingIds.UnionWith(targets.Select(t => t.Id)); reconnectingLabels.UnionWith(targets.Select(t => t.Label));
            var closedIds = new HashSet<long>(); var failures = new List<string>();
            foreach (var t in targets)
            {
                Message = $"Reset servers: closing {t.Label}…"; Warning = false;
                var closed = await Task.Run(() => manager.CloseClient(t.Identity));
                if (closed.Success) closedIds.Add(t.Id); else failures.Add($"{t.Label} could not be closed: {closed.Message}");
            }
            // A private server only shuts down and starts fresh when everyone has left it, so anyone still inside defeats the wait.
            int stillOpen = Clients.Count - closedIds.Count;
            string staying = stillOpen > 0 ? $" {stillOpen} other client(s) stay open (ignored, no saved login or could not be closed): a private server with someone still in it will not reset." : "";
            AddActivity("Reset servers", $"Closed {closedIds.Count} accounts. Once everyone has left, the private servers reset; the accounts rejoin in {(int)Math.Ceiling(ResetDelay.TotalSeconds)} seconds." + (skipped.Count > 0 ? $" Left alone ({skipped.Count}): {string.Join("; ", skipped.Take(3))}." : "") + staying, stillOpen > 0, announce: false);

            var until = DateTimeOffset.UtcNow + ResetDelay;
            while (DateTimeOffset.UtcNow < until && !disposed)
            {
                var left = until - DateTimeOffset.UtcNow;
                Message = $"Reset servers: {closedIds.Count} accounts closed, rejoining in {Math.Ceiling(left.TotalSeconds):0}s…";
                await Task.Delay(left < TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1));
            }
            if (disposed) return;

            // The accounts just closed, plus any other saved account that is not running, rejoin one at a time.
            var now = DateTimeOffset.UtcNow;
            var toOpen = vault.Logins.Where(l => closedIds.Contains(l.UserId) || !IsActive(l.UserId, now)).ToArray();
            var (opened, openFailures) = await LaunchCoreAsync(toOpen, closedIds);
            failures.AddRange(openFailures);
            string summary = failures.Count == 0 ? $"Servers reset. {opened} accounts rejoined." : $"Servers reset. {opened} rejoined; {failures.Count} problem(s): {failures[0]}";
            AddActivity("Reset servers", summary, failures.Count > 0);
            Warning = failures.Count > 0; Message = summary;
        }
        finally
        {
            reconnectingIds.Clear(); reconnectingLabels.Clear(); launching = false; RefreshLogins(); RaiseServers();
        }
    }
}
