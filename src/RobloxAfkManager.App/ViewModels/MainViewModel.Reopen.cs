using RobloxAfkManager.Core;

namespace RobloxAfkManager.App.ViewModels;

// While the person is away, an account that crashed (its window closed) or was kicked (disconnected, window still open)
// is started again from its saved login. At the PC nothing happens, so closing a client yourself is always respected.
// Off until switched on. Limits keep a crash loop or a Roblox outage from turning into a stream of launches.
public sealed partial class MainViewModel
{
    private sealed record Pending(string Label, DateTimeOffset Since, bool CloseFirst, WindowIdentity? Identity);
    private const int MaxReopensPerAccountPerHour = 3, MaxReopensPerHour = 24;
    private static readonly TimeSpan KickedDelay = TimeSpan.FromSeconds(60);
    // How long an account must stay gone before it is reopened, so a window that blinks out for a moment is left alone.
    public TimeSpan ReopenAfter { get; set; } = TimeSpan.FromSeconds(25);
    private bool autoReopen, reopening;
    private DateTimeOffset lastReopenAt;
    private HashSet<long> previouslyRunning = [];
    private readonly Dictionary<long, string> accountLabels = [];
    private readonly Dictionary<long, Pending> pendingReopen = [];
    private readonly Dictionary<long, Queue<DateTimeOffset>> reopenTimes = [];
    private readonly Queue<DateTimeOffset> reopenLog = new();
    private readonly HashSet<long> gaveUp = [];
    private string ReopenFile => System.IO.Path.Combine(settingsFolder, "reopen.txt");

    public bool AutoReopen
    {
        get => autoReopen;
        set
        {
            if (!Set(ref autoReopen, value)) return;
            try { System.IO.Directory.CreateDirectory(settingsFolder); System.IO.File.WriteAllText(ReopenFile, value ? "1" : "0"); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { Warning = true; Message = "Could not save the setting: " + ex.Message; }
            if (!value) pendingReopen.Clear();
            Raise(nameof(ShowAwayChip)); RaiseIndicators();
        }
    }
    // The Away indicator matters whenever something depends on it.
    public bool ShowAwayChip => webhook.Configured || autoReopen;

    private void InitializeReopen()
    {
        try { autoReopen = System.IO.File.Exists(ReopenFile) && System.IO.File.ReadAllText(ReopenFile).Trim() == "1"; }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { autoReopen = false; }
    }

    // Runs on every refresh: notices accounts that disappeared or were kicked, and only counts them when the person is away.
    private void TrackReopen()
    {
        var current = Clients.Where(c => !c.IsIgnored && c.AccountId is not null).GroupBy(c => c.AccountId!.Value).ToDictionary(g => g.Key, g => g.First());
        var now = DateTimeOffset.UtcNow;
        if (autoReopen && IsAway)
        {
            var saved = vault.Logins.Select(l => l.UserId).ToHashSet();
            foreach (var id in previouslyRunning.Where(id => !current.ContainsKey(id) && saved.Contains(id) && !pendingReopen.ContainsKey(id) && !gaveUp.Contains(id)))
                pendingReopen[id] = new(accountLabels.GetValueOrDefault(id, "Account"), now, false, null);
            foreach (var (id, tile) in current)
                if (saved.Contains(id) && !pendingReopen.ContainsKey(id) && !gaveUp.Contains(id)
                    && tile.Session is { Phase: SessionPhase.Disconnected, EventAt: { } at } && now - at >= KickedDelay)
                    pendingReopen[id] = new(tile.Label, now, true, tile.Identity);
        }
        // An account that is running again (and not disconnected) needs nothing more.
        foreach (var id in pendingReopen.Keys.ToArray())
            if (current.TryGetValue(id, out var tile) && !pendingReopen[id].CloseFirst) pendingReopen.Remove(id);
            else if (current.TryGetValue(id, out var shown) && pendingReopen[id].CloseFirst && shown.Session?.Phase != SessionPhase.Disconnected) pendingReopen.Remove(id);
        foreach (var id in gaveUp.Where(current.ContainsKey).ToArray()) gaveUp.Remove(id);
        foreach (var (id, tile) in current) accountLabels[id] = tile.Label;
        previouslyRunning = current.Keys.ToHashSet();
    }

    private async Task ReopenAwayAsync()
    {
        if (!autoReopen || reopening || recovering || launching || Busy || disposed || gamePlaceId == 0 || pendingReopen.Count == 0) return;
        if (!IsAway) { pendingReopen.Clear(); return; } // you're back: leave it to you
        var now = DateTimeOffset.UtcNow;
        if (now - lastReopenAt < LaunchSpacing) return;
        var due = pendingReopen.Where(p => now - p.Value.Since >= (p.Value.CloseFirst ? TimeSpan.Zero : ReopenAfter) && !IsActive(p.Key, now)).Select(p => p.Key).FirstOrDefault();
        if (due == 0) return;
        var pending = pendingReopen[due];

        while (reopenLog.Count > 0 && now - reopenLog.Peek() > TimeSpan.FromHours(1)) reopenLog.Dequeue();
        if (!reopenTimes.TryGetValue(due, out var own)) reopenTimes[due] = own = new();
        while (own.Count > 0 && now - own.Peek() > TimeSpan.FromHours(1)) own.Dequeue();
        if (own.Count >= MaxReopensPerAccountPerHour)
        {
            pendingReopen.Remove(due); gaveUp.Add(due);
            AddActivity(pending.Label, $"Gave up reopening this account: it was reopened {MaxReopensPerAccountPerHour} times in the last hour and keeps closing. Check it.", true);
            return;
        }
        if (reopenLog.Count >= MaxReopensPerHour) return; // wait: the hourly total is used up

        reopening = true; lastReopenAt = now; own.Enqueue(now); reopenLog.Enqueue(now); recoveredAt[due] = now;
        try
        {
            string cookie = vault.Cookie(due) ?? throw new InvalidOperationException("The saved login could not be read.");
            // Check the login works before touching a running client, so a dead login never costs a client.
            string ticket = await launcher.PrepareAsync(cookie, gamePlaceId, CancellationToken.None);
            if (pending.CloseFirst && pending.Identity is { } identity)
            {
                var closed = await Task.Run(() => manager.CloseClient(identity));
                if (!closed.Success) throw new InvalidOperationException(closed.Message);
            }
            int pid = await launcher.LaunchSafelyAsync(cookie, gamePlaceId, CancellationToken.None, ticket);
            launchedPids[pid] = due; launchedAt[due] = DateTimeOffset.UtcNow;
            // Not announced to Discord as "back": the alert is edited when the account has really rejoined.
            AddActivity(pending.Label, (pending.CloseFirst ? "Was kicked, so it was reopened" : "Closed while you were away, so it was reopened") + " automatically.", false, announce: false);
            Warning = false; Message = $"{pending.Label} was reopened.";
        }
        catch (Exception ex)
        {
            log.Write($"Reopening account {due} failed.", ex);
            AddActivity(pending.Label, "Could not be reopened automatically: " + ex.Message, true);
            Warning = true; Message = $"{pending.Label}: {ex.Message}";
        }
        finally { pendingReopen.Remove(due); reopening = false; RefreshLogins(); }
    }
}
