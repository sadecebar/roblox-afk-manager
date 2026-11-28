using RobloxAfkManager.Core;

namespace RobloxAfkManager.App.ViewModels;

// An account sent to a private server that never gets in (full, waiting in line, link problem) is moved to the game's
// public server instead of being retried forever. What happened is written to the activity log and the incident log.
public sealed partial class MainViewModel
{
    // How long a client started for a private server may take to join before it counts as stuck.
    public TimeSpan PrivateJoinPatience { get; set; } = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan PublicFallbackLife = TimeSpan.FromMinutes(30);
    private readonly HashSet<long> launchedPrivate = [];
    private readonly Dictionary<long, DateTimeOffset> privateFailedAt = [];
    private long lastKnownPlace;
    private bool fallingBack;

    private bool UsePublicFor(long userId, DateTimeOffset now) => privateFailedAt.TryGetValue(userId, out var at) && now - at < PublicFallbackLife;
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
        int server = ServerNumberFor(s.Id);
        launchedPrivate.Remove(s.Id); privateFailedAt[s.Id] = now;
        try
        {
            string cookie = vault.Cookie(s.Id) ?? throw new InvalidOperationException("The saved login could not be read.");
            var plan = await PlanLaunchAsync(s.Id, cookie);
            string ticket = await launcher.PrepareAsync(cookie, plan.PlaceId, CancellationToken.None);
            var closed = await Task.Run(() => manager.CloseClient(s.Tile.Identity));
            if (!closed.Success) throw new InvalidOperationException(closed.Message);
            int pid = await LaunchOneAsync(cookie, plan, ticket, s.Id, replacing: true);
            launchedPids[pid] = s.Id; launchedAt[s.Id] = DateTimeOffset.UtcNow;
            AddActivity(label, $"Could not get into private server {server} within {(int)Math.Ceiling(PrivateJoinPatience.TotalMinutes)} minutes (still loading or waiting in line, so it is probably full or its link has a problem). Fix: closed it and joined the public server of the game instead. It tries the private server again in {(int)PublicFallbackLife.TotalMinutes} minutes, or after Reset servers.", true, quiet: true);
            Warning = true; Message = $"{label} could not get into private server {server}; it joined a public server instead.";
        }
        catch (Exception ex)
        {
            log.Write($"Public fallback for account {s.Id} failed.", ex);
            AddActivity(label, $"Could not get into private server {server} and the public fallback failed too: {ex.Message}", true);
            Warning = true; Message = $"{label}: {ex.Message}";
        }
        finally { fallingBack = false; RefreshLogins(); }
    }
}
