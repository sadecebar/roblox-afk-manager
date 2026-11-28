using RobloxAfkManager.Core;

namespace RobloxAfkManager.App.ViewModels;

// An account sent to a private server that never gets in (full, waiting in line, link problem) is moved on instead of being retried
// forever: first to the other private server, then to the game's public server. A restart or reopen later starts again at the
// account's own private server. What happened is written to the activity log and the incident log.
public sealed partial class MainViewModel
{
    // How long a client started for a private server may take to join before it counts as stuck.
    public TimeSpan PrivateJoinPatience { get; set; } = TimeSpan.FromMinutes(2);
    private readonly HashSet<long> launchedPrivate = [];
    private readonly Dictionary<long, int> launchedStage = []; // 0 own server, 1 other server, 2 public
    private long lastKnownPlace;
    private bool fallingBack;

    // The public game: the game link if one is set, otherwise the place the private servers belong to.
    private long FallbackPlace => gamePlaceId > 0 ? gamePlaceId : lastKnownPlace;
    private string NameOf(long userId) => accountLabels.GetValueOrDefault(userId) ?? vault.Logins.FirstOrDefault(l => l.UserId == userId)?.Username ?? $"Account {userId}";

    private async Task FallbackStuckPrivateJoinsAsync()
    {
        if (fallingBack || launching || recovering || reopening || Busy || disposed || launchedPrivate.Count == 0 || FallbackPlace == 0) return;
        var now = DateTimeOffset.UtcNow;
        (long Id, ClientTileViewModel Tile)? stuck = null;
        foreach (var id in launchedPrivate.ToArray())
        {
            if (runningAccounts.Contains(id) || !launchedAt.TryGetValue(id, out var at)) { launchedPrivate.Remove(id); continue; } // joined
            var tile = Clients.FirstOrDefault(c => c.AccountId is null && launchedPids.Any(p => p.Value == id && p.Key == c.ProcessId));
            if (tile is null) { if (now - at > TimeSpan.FromMinutes(2)) launchedPrivate.Remove(id); continue; } // the window is gone: reopen handles that
            if (now - at >= PrivateJoinPatience) { stuck = (id, tile); break; }
        }
        if (stuck is not { } s) return;

        fallingBack = true;
        string label = NameOf(s.Id);
        int own = ServerNumberFor(s.Id);
        int stage = launchedStage.GetValueOrDefault(s.Id) + 1;
        int launchedServer = stage == 1 ? own : 3 - own;
        launchedPrivate.Remove(s.Id);
        try
        {
            string cookie = vault.Cookie(s.Id) ?? throw new InvalidOperationException("The saved login could not be read.");
            var plan = await PlanLaunchAsync(s.Id, cookie, stage);
            string ticket = await launcher.PrepareAsync(cookie, plan.PlaceId, CancellationToken.None);
            var closed = await Task.Run(() => manager.CloseClient(s.Tile.Identity));
            if (!closed.Success) throw new InvalidOperationException(closed.Message);
            int pid = await LaunchOneAsync(cookie, plan, ticket, s.Id, replacing: true);
            launchedPids[pid] = s.Id; launchedAt[s.Id] = DateTimeOffset.UtcNow;
            string waited = $"Could not get into private server {launchedServer} within {(int)Math.Ceiling(PrivateJoinPatience.TotalMinutes)} minutes (still loading or waiting in line, so it is probably full or its link has a problem).";
            string moved = plan.Server > 0 ? $"Fix: closed it and joined private server {plan.Server} instead. If that one is full too it joins the public server." : "Fix: closed it and joined the public server of the game instead.";
            AddActivity(label, $"{waited} {moved} A restart tries its own private server first again.", true, quiet: true);
            Warning = true; Message = $"{label} could not get into private server {launchedServer}; " + (plan.Server > 0 ? $"it joined private server {plan.Server}." : "it joined a public server.");
        }
        catch (Exception ex)
        {
            log.Write($"Server fallback for account {s.Id} failed.", ex);
            AddActivity(label, $"Could not get into private server {launchedServer} and moving the account on failed too: {ex.Message}", true);
            Warning = true; Message = $"{label}: {ex.Message}";
        }
        finally { fallingBack = false; RefreshLogins(); }
    }
}
