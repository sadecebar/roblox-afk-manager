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
    public bool AutoRecover { get => autoRecover; set { if (Set(ref autoRecover, value)) { Raise(nameof(LoginFixState)); Raise(nameof(LoginFixHint)); } } }
    public string LoginFixState => autoRecover ? "On" : "Off";
    public string LoginFixHint => autoRecover ? "Login restart is on: an account whose Roblox login was ended is closed and reopened. Click to turn it off." : "Login restart is off. Click to turn it on: an account whose Roblox login was ended is closed and reopened.";
    // A joined client that logs almost nothing for ten minutes (see SessionLogParser.IsQuiet) is closed and reopened.
    // Off by default: log volume varies a lot between games and servers (healthy AFK clients were seen at 48 to 500 lines per ten minutes).
    private bool autoRestartQuiet = false;
    public bool AutoRestartQuiet { get => autoRestartQuiet; set { if (Set(ref autoRestartQuiet, value)) { Raise(nameof(QuietFixState)); Raise(nameof(QuietFixHint)); } } }
    public string QuietFixState => autoRestartQuiet ? "On" : "Off";
    public string QuietFixHint => autoRestartQuiet ? "Quiet-log restart is on (experimental): a client that logs under 100 lines in 10 minutes is restarted. Click to turn it off." : "Quiet-log restart is off (experimental). Click to turn it on.";

    // The per-client Restart button: always allowed, no cooldown or hourly cap, because the person asked for it.
    public AsyncCommand RestartClientCommand { get; private set; } = null!;
    private bool CanRestart(object? parameter) => parameter is ClientTileViewModel { AccountId: { } id } && vault.Logins.Any(l => l.UserId == id) && gamePlaceId > 0 && !recovering && !reopening && !launching && !Busy;
    private async Task RestartClientAsync(object? parameter)
    {
        if (parameter is not ClientTileViewModel { AccountId: { } account } tile || !CanRestart(parameter)) return;
        recovering = true; recoveredAt[account] = DateTimeOffset.UtcNow;
        string label = tile.Label;
        try
        {
            string cookie = vault.Cookie(account) ?? throw new InvalidOperationException("The saved login could not be read.");
            string ticket = await launcher.PrepareAsync(cookie, GamePlaceId, CancellationToken.None);
            var identity = tile.Identity;
            var closed = await Task.Run(() => manager.CloseClient(identity));
            if (!closed.Success) throw new InvalidOperationException(closed.Message);
            int pid = await launcher.LaunchSafelyAsync(cookie, GamePlaceId, CancellationToken.None, ticket);
            launchedPids[pid] = account; launchedAt[account] = DateTimeOffset.UtcNow;
            AddActivity(label, "You restarted this account. Closed the client and reopened it from its saved login.", false, announce: false, info: true);
            Warning = false; Message = $"{label} was restarted.";
        }
        catch (Exception ex)
        {
            log.Write($"Restarting account {account} failed.", ex);
            AddActivity(label, "Restart failed: " + ex.Message, true, announce: false);
            Warning = true; Message = $"{label}: {ex.Message}";
        }
        finally { recovering = false; RefreshLogins(); }
    }

    private IncidentJournal? incidents;
    // Where the morning-after record of problems and fixes is kept (next to the other settings).
    public string IncidentLogPath => System.IO.Path.Combine(settingsFolder, "incidents.log");
    private void Journal(ActivityEntry entry) => (incidents ??= new(IncidentLogPath)).Write(entry);

    private void AddActivity(string label, string message, bool warning, bool announce = true, bool info = false)
    {
        var entry = new ActivityEntry(DateTimeOffset.UtcNow, label, message, warning);
        Journal(entry);
        Activity.Insert(0, entry); while (Activity.Count > 100) Activity.RemoveAt(Activity.Count - 1);
        if (warning) { unreadAlerts++; if (notificationsEnabled) AlertRaised?.Invoke(entry); }
        if (announce) Announce(entry, warning, info);
        RaiseUnread(); Raise(nameof(HasActivity)); SaveWorkspace();
    }

    private async Task RecoverSignedOutAsync()
    {
        if (!(AutoRecover || AutoRestartQuiet) || gamePlaceId == 0 || reopening || recovering || launching || Busy || disposed) return;
        var now = DateTimeOffset.UtcNow;
        var saved = vault.Logins.Select(l => l.UserId).ToHashSet();
        var victim = Clients.FirstOrDefault(c => !c.IsIgnored && c.AccountId is { } id && saved.Contains(id)
            && (c.Session is { AuthFailures: >= AuthFailureThreshold } || (AutoRestartQuiet && c.IsQuiet))
            && !(recoveredAt.TryGetValue(id, out var at) && now - at < RecoveryCooldown));
        if (victim?.AccountId is not { } account) return;
        bool quiet = victim.Session is not { AuthFailures: >= AuthFailureThreshold };
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
            AddActivity(label, quiet ? "Stopped doing anything for 10 minutes (probably stuck on an error box). Fix: closed the client and restarted it from its saved login."
                : "Roblox ended this account's login. Fix: closed the client and restarted it from its saved login.", false, info: true);
            Warning = false; Message = quiet ? $"{label} was stuck. Restarted it." : $"{label}'s login was lost. Restarted it.";
        }
        catch (Exception ex)
        {
            log.Write($"Recovering account {account} failed.", ex);
            AddActivity(label, (quiet ? "Stopped doing anything for 10 minutes and it could not be restarted: " : "Roblox ended this account's login and it could not be restarted: ") + ex.Message, true);
            Warning = true; Message = $"{label}: {ex.Message}";
        }
        finally { recovering = false; RefreshLogins(); }
    }
}
