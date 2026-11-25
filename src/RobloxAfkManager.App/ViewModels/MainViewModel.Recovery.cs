using RobloxAfkManager.Core;

namespace RobloxAfkManager.App.ViewModels;

// When Roblox ends an account's login while its game keeps running, the client freezes and eventually kicks.
// The log shows it first as a burst of HTTP 401s. This restarts that one account from its saved login.
public sealed partial class MainViewModel
{
    private const int AuthFailureThreshold = 30;
    private static readonly TimeSpan RecoveryCooldown = TimeSpan.FromMinutes(20);
    private readonly Dictionary<long, DateTimeOffset> recoveredAt = [];
    private bool recovering, autoRecover = false, capWarned;
    // A Roblox outage or a misread log must never turn into a burst of restarts: at most this many an hour, in total.
    private const int MaxRecoveriesPerHour = 4;
    private readonly Queue<DateTimeOffset> recoveryLog = new();
    // Only accounts with a saved login can be restarted, and each at most once per cooldown.
    public bool AutoRecover { get => autoRecover; set => Set(ref autoRecover, value); }

    private void AddActivity(string label, string message, bool warning)
    {
        var entry = new ActivityEntry(DateTimeOffset.UtcNow, label, message, warning);
        Activity.Insert(0, entry); while (Activity.Count > 100) Activity.RemoveAt(Activity.Count - 1);
        if (warning) { unreadAlerts++; if (notificationsEnabled) AlertRaised?.Invoke(entry); }
        Announce(entry, warning);
        RaiseUnread(); Raise(nameof(HasActivity)); SaveWorkspace();
    }

    private async Task RecoverSignedOutAsync()
    {
        if (!AutoRecover || gamePlaceId == 0 || recovering || launching || Busy || disposed) return;
        var now = DateTimeOffset.UtcNow;
        var saved = vault.Logins.Select(l => l.UserId).ToHashSet();
        var victim = Clients.FirstOrDefault(c => !c.IsIgnored && c.AccountId is { } id && saved.Contains(id)
            && c.Session is { AuthFailures: >= AuthFailureThreshold }
            && !(recoveredAt.TryGetValue(id, out var at) && now - at < RecoveryCooldown));
        if (victim?.AccountId is not { } account) return;
        while (recoveryLog.Count > 0 && now - recoveryLog.Peek() > TimeSpan.FromHours(1)) recoveryLog.Dequeue();
        if (recoveryLog.Count >= MaxRecoveriesPerHour)
        {
            if (!capWarned) { capWarned = true; AddActivity("Auto-restart", $"Paused: {MaxRecoveriesPerHour} accounts were already restarted in the last hour. If many accounts lose their login at once, Roblox itself may be having trouble. Restart accounts by hand or wait.", true); }
            return;
        }
        capWarned = false;
        recoveryLog.Enqueue(now);
        recovering = true; recoveredAt[account] = now;
        string label = victim.Label;
        try
        {
            string cookie = vault.Cookie(account) ?? throw new InvalidOperationException("The saved login could not be read.");
            // Check the login works before touching the running client, so a dead cookie never costs a client.
            string ticket = await launcher.PrepareAsync(cookie, GamePlaceId, CancellationToken.None);
            var identity = victim.Identity;
            var closed = await Task.Run(() => manager.CloseClient(identity));
            if (!closed.Success) throw new InvalidOperationException(closed.Message);
            int pid = await launcher.LaunchSafelyAsync(cookie, GamePlaceId, CancellationToken.None, ticket);
            launchedPids[pid] = account; launchedAt[account] = DateTimeOffset.UtcNow;
            AddActivity(label, "Roblox ended this account's login, so it was restarted from its saved login.", false);
            Warning = false; Message = $"{label}'s login was lost. Restarted it.";
        }
        catch (Exception ex)
        {
            log.Write($"Recovering account {account} failed.", ex);
            AddActivity(label, "Roblox ended this account's login and it could not be restarted: " + ex.Message, true);
            Warning = true; Message = $"{label}: {ex.Message}";
        }
        finally { recovering = false; RefreshLogins(); }
    }
}
