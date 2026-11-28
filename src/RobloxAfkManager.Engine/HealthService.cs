using System.IO;
using RobloxAfkManager.Core;

namespace RobloxAfkManager.Engine;

// Keeps accounts running without anyone at the PC:
// - Auto-reopen: while the person is away, an account that crashed (window closed) or was kicked (disconnected, window still
//   open) is started again from its saved login. At the PC nothing happens, so closing a client yourself is always respected.
// - Login fix / quiet fix: an account whose Roblox login was ended (a burst of HTTP 401s) or whose log went quiet is restarted.
// - A disconnected second window of an account that has a healthy one is closed, never restarted.
// - The per-client Restart button.
// Limits keep a crash loop or a Roblox outage from turning into a stream of launches.
public sealed class HealthService
{
    private sealed record Pending(string Label, DateTimeOffset Since, bool CloseFirst, WindowIdentity? Identity, string? Reason = null);
    private const int MaxReopensPerAccountPerHour = 3, MaxReopensPerHour = 24;
    public static readonly TimeSpan KickedDelay = TimeSpan.FromMinutes(2);
    private const int AuthFailureThreshold = 30;
    private static readonly TimeSpan RecoveryCooldown = TimeSpan.FromMinutes(20);
    // A Roblox outage or a misread log must never turn into a burst of restarts: at most this many an hour, in total.
    private const int MaxRecoveriesPerHour = 4;

    private readonly IEngineHost host;
    private readonly LaunchService launch;
    private readonly LoginVault vault;
    private readonly IAppLog log;
    private readonly string folder;

    public HealthService(IEngineHost host, LaunchService launch, string settingsFolder, IAppLog log)
    {
        this.host = host; this.launch = launch; vault = launch.Vault; this.log = log; folder = settingsFolder;
        try { autoReopen = File.Exists(ReopenFile) && File.ReadAllText(ReopenFile).Trim() == "1"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { autoReopen = false; }
    }

    public bool Recovering => recovering;
    public bool Reopening => reopening;
    public bool Working => recovering || reopening;

    // ---- settings -------------------------------------------------------------------------------------------------------------
    private string ReopenFile => Path.Combine(folder, "reopen.txt");
    private bool autoReopen, reopening, recovering, capWarned;
    public bool AutoReopen
    {
        get => autoReopen;
        set
        {
            if (autoReopen == value) return;
            autoReopen = value;
            try { Directory.CreateDirectory(folder); File.WriteAllText(ReopenFile, value ? "1" : "0"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { host.SetStatus("Could not save the setting: " + ex.Message, true); }
            if (!value) pendingReopen.Clear();
        }
    }
    // Only accounts with a saved login can be restarted, and each at most once per cooldown.
    public bool AutoRecover { get; set; }
    // A joined client that logs almost nothing for ten minutes (see SessionLogParser.IsQuiet) is closed and reopened.
    // Off by default: log volume varies a lot between games and servers (healthy AFK clients were seen at 48 to 500 lines per ten minutes).
    public bool AutoRestartQuiet { get; set; }
    // How long an account must stay gone before it is reopened, so a window that blinks out for a moment is left alone.
    public TimeSpan ReopenAfter { get; set; } = TimeSpan.FromSeconds(25);

    // ---- what is being watched -------------------------------------------------------------------------------------------------
    private DateTimeOffset lastReopenAt;
    private HashSet<long> previouslyRunning = [];
    private readonly Dictionary<long, string> accountLabels = [];
    private readonly Dictionary<long, Pending> pendingReopen = [];
    private readonly Dictionary<long, Queue<DateTimeOffset>> reopenTimes = [];
    private readonly Queue<DateTimeOffset> reopenLog = new();
    private readonly HashSet<long> gaveUp = [];
    private readonly Dictionary<long, DateTimeOffset> recoveredAt = [];
    private readonly Queue<DateTimeOffset> recoveryLog = new();

    // The last card label of each account seen running, so a closed client can still be named.
    public IReadOnlyDictionary<long, string> AccountLabels => accountLabels;
    public bool IsPendingReopen(long userId) => pendingReopen.ContainsKey(userId);

    // Runs on every refresh: notices accounts that disappeared or were kicked, and only counts them when the person is away.
    public void Track(bool away)
    {
        // If an account has two windows, the one that is not disconnected speaks for it: restarting the other would knock the healthy one off.
        var current = host.Clients.Where(c => !c.IsIgnored && c.AccountId is not null).GroupBy(c => c.AccountId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Session?.Phase == SessionPhase.Disconnected ? 1 : 0).First());
        var now = DateTimeOffset.UtcNow;
        if (autoReopen && away)
        {
            var saved = vault.Logins.Select(l => l.UserId).ToHashSet();
            foreach (var id in previouslyRunning.Where(id => !current.ContainsKey(id) && saved.Contains(id) && !pendingReopen.ContainsKey(id) && !gaveUp.Contains(id) && !launch.IsReconnecting(id)))
                pendingReopen[id] = new(accountLabels.GetValueOrDefault(id, "Account"), now, false, null);
            foreach (var (id, tile) in current)
                if (saved.Contains(id) && !pendingReopen.ContainsKey(id) && !gaveUp.Contains(id) && !launch.IsReconnecting(id)
                    && tile.Session is { Phase: SessionPhase.Disconnected, EventAt: { } at } && now - at >= KickedDelay)
                    pendingReopen[id] = new(tile.Label, now, true, tile.Identity, tile.Session.Reason);
        }
        // An account that is running again (and not disconnected) needs nothing more.
        foreach (var id in pendingReopen.Keys.ToArray())
            if (current.TryGetValue(id, out _) && !pendingReopen[id].CloseFirst) pendingReopen.Remove(id);
            else if (current.TryGetValue(id, out var shown) && pendingReopen[id].CloseFirst && shown.Session?.Phase != SessionPhase.Disconnected) pendingReopen.Remove(id);
        foreach (var id in gaveUp.Where(current.ContainsKey).ToArray()) gaveUp.Remove(id);
        foreach (var (id, tile) in current) accountLabels[id] = tile.Label;
        previouslyRunning = current.Keys.ToHashSet();
    }

    // A disconnected window of an account that has another, healthy window is only closed, never restarted: starting it again
    // would log the account in a second time and each window would keep kicking the other ("joined from another device").
    private bool closingDuplicates;
    public async Task CloseDuplicateWindowsAsync(bool away)
    {
        if (!autoReopen || closingDuplicates || reopening || recovering || launch.Launching || host.Busy || host.Disposed || !away) return;
        var now = DateTimeOffset.UtcNow;
        var clients = host.Clients;
        var stale = clients.Where(c => !c.IsIgnored && c.AccountId is { } id && c.Session is { Phase: SessionPhase.Disconnected, EventAt: { } at } && now - at >= KickedDelay
            && clients.Any(o => !ReferenceEquals(o, c) && o.AccountId == id && o.Session?.Phase != SessionPhase.Disconnected)).ToArray();
        if (stale.Length == 0) return;
        closingDuplicates = true;
        try
        {
            foreach (var tile in stale)
            {
                var closed = await Task.Run(() => host.CloseClient(tile.Identity));
                host.AddActivity(tile.Label, closed.Success
                    ? "A second window of this account was disconnected" + (tile.Session?.Reason is { } why ? $" ({why.TrimEnd('.')})" : "") + ". Fix: closed that window. The other window of the account keeps running."
                    : "A second, disconnected window of this account could not be closed: " + closed.Message, !closed.Success, announce: false, info: true);
            }
        }
        finally { closingDuplicates = false; }
    }

    public async Task ReopenAwayAsync(bool away)
    {
        if (!autoReopen || reopening || recovering || launch.Launching || host.Busy || host.Disposed || !launch.HasLaunchTarget || pendingReopen.Count == 0) return;
        if (!away) { pendingReopen.Clear(); return; } // you're back: leave it to you
        var now = DateTimeOffset.UtcNow;
        if (now - lastReopenAt < launch.LaunchSpacing) return;
        // A disconnected client still has its window open, so "running" must not block it: only a launch that just happened does.
        bool Blocked(KeyValuePair<long, Pending> p) => p.Value.CloseFirst ? launch.IsStarting(p.Key, now) : launch.IsActive(p.Key, now);
        var due = pendingReopen.Where(p => now - p.Value.Since >= (p.Value.CloseFirst ? TimeSpan.Zero : ReopenAfter) && !Blocked(p)).Select(p => p.Key).FirstOrDefault();
        if (due == 0) return;
        var pending = pendingReopen[due];

        while (reopenLog.Count > 0 && now - reopenLog.Peek() > TimeSpan.FromHours(1)) reopenLog.Dequeue();
        if (!reopenTimes.TryGetValue(due, out var own)) reopenTimes[due] = own = new();
        while (own.Count > 0 && now - own.Peek() > TimeSpan.FromHours(1)) own.Dequeue();
        if (own.Count >= MaxReopensPerAccountPerHour)
        {
            pendingReopen.Remove(due); gaveUp.Add(due);
            host.AddActivity(pending.Label, $"Gave up reopening this account: it was reopened {MaxReopensPerAccountPerHour} times in the last hour and keeps closing. Check it.", true);
            return;
        }
        if (reopenLog.Count >= MaxReopensPerHour) return; // wait: the hourly total is used up

        reopening = true; lastReopenAt = now; own.Enqueue(now); reopenLog.Enqueue(now); recoveredAt[due] = now;
        try
        {
            // The login is checked before a running client is touched, so a dead login never costs a client.
            await launch.ReplaceAsync(due, pending.CloseFirst ? pending.Identity : null, replacing: pending.CloseFirst);
            // Sent to Discord as a plain "what was done" message; the problem alert itself turns green when the account has really rejoined.
            host.AddActivity(pending.Label, (pending.CloseFirst
                ? $"Stayed disconnected for {(int)KickedDelay.TotalMinutes} minutes{(pending.Reason is { } why ? $" ({why.TrimEnd('.')})" : "")}. Fix: closed the stuck client and reopened it from its saved login."
                : "Window closed while you were away. Fix: reopened it from its saved login."), false, info: true);
            host.SetStatus($"{pending.Label} was reopened.", false);
        }
        catch (Exception ex)
        {
            log.Write($"Reopening account {due} failed.", ex);
            host.AddActivity(pending.Label, (pending.CloseFirst ? "Stayed disconnected" + (pending.Reason is { } r ? $" ({r.TrimEnd('.')})" : "") + ". " : "Window closed while you were away. ") + "Fix failed, it could not be restarted: " + ex.Message, true);
            host.SetStatus($"{pending.Label}: {ex.Message}", true);
        }
        finally { pendingReopen.Remove(due); reopening = false; host.LaunchStateChanged(); }
    }

    // ---- the Restart button ---------------------------------------------------------------------------------------------------
    // Always allowed, no cooldown or hourly cap, because the person asked for it.
    public bool CanRestart(IClientView? tile) => tile is { AccountId: { } id } && vault.Logins.Any(l => l.UserId == id) && launch.HasLaunchTarget
        && !recovering && !reopening && !launch.Launching && !host.Busy;

    public async Task RestartAsync(IClientView tile)
    {
        if (tile.AccountId is not { } account || !CanRestart(tile)) return;
        recovering = true; recoveredAt[account] = DateTimeOffset.UtcNow;
        string label = tile.Label;
        try
        {
            await launch.ReplaceAsync(account, tile.Identity);
            host.AddActivity(label, "You restarted this account. Closed the client and reopened it from its saved login.", false, announce: false, info: true);
            host.SetStatus($"{label} was restarted.", false);
        }
        catch (Exception ex)
        {
            log.Write($"Restarting account {account} failed.", ex);
            host.AddActivity(label, "Restart failed: " + ex.Message, true, announce: false);
            host.SetStatus($"{label}: {ex.Message}", true);
        }
        finally { recovering = false; host.LaunchStateChanged(); }
    }

    // ---- login fix and quiet fix ------------------------------------------------------------------------------------------------
    // When Roblox ends an account's login while its game keeps running, the client freezes and eventually kicks. The log shows it
    // first as a burst of HTTP 401s. This restarts that one account from its saved login.
    public async Task RecoverSignedOutAsync()
    {
        if (!(AutoRecover || AutoRestartQuiet) || !launch.HasLaunchTarget || reopening || recovering || launch.Launching || host.Busy || host.Disposed) return;
        var now = DateTimeOffset.UtcNow;
        var saved = vault.Logins.Select(l => l.UserId).ToHashSet();
        var clients = host.Clients;
        var victim = clients.FirstOrDefault(c => !c.IsIgnored && c.AccountId is { } id && saved.Contains(id)
            && (c.Session is { AuthFailures: >= AuthFailureThreshold } || (AutoRestartQuiet && SessionLogParser.IsQuiet(c.Session, now)))
            && !(recoveredAt.TryGetValue(id, out var at) && now - at < RecoveryCooldown)
            && !(launch.LaunchedAt(id) is { } launched && now - launched < TimeSpan.FromMinutes(10)) // a fresh client is still settling
            && clients.Count(o => o.AccountId == id) == 1);                                             // two windows: see CloseDuplicateWindowsAsync
        if (victim?.AccountId is not { } account) return;
        bool quiet = victim.Session is not { AuthFailures: >= AuthFailureThreshold };
        while (recoveryLog.Count > 0 && now - recoveryLog.Peek() > TimeSpan.FromHours(1)) recoveryLog.Dequeue();
        if (recoveryLog.Count >= MaxRecoveriesPerHour)
        {
            if (!capWarned) { capWarned = true; host.AddActivity("Auto-restart", $"Paused: {MaxRecoveriesPerHour} accounts were already restarted in the last hour. If many accounts lose their login at once, Roblox itself may be having trouble. Restart accounts by hand or wait.", true); }
            return;
        }
        capWarned = false;
        recoveryLog.Enqueue(now);
        recovering = true; recoveredAt[account] = now;
        string label = victim.Label;
        try
        {
            // The login is checked before the running client is touched, so a dead cookie never costs a client.
            await launch.ReplaceAsync(account, victim.Identity);
            host.AddActivity(label, quiet ? "Stopped doing anything for 10 minutes (probably stuck on an error box). Fix: closed the client and restarted it from its saved login."
                : "Roblox ended this account's login. Fix: closed the client and restarted it from its saved login.", false, info: true);
            host.SetStatus(quiet ? $"{label} was stuck. Restarted it." : $"{label}'s login was lost. Restarted it.", false);
        }
        catch (Exception ex)
        {
            log.Write($"Recovering account {account} failed.", ex);
            host.AddActivity(label, (quiet ? "Stopped doing anything for 10 minutes and it could not be restarted: " : "Roblox ended this account's login and it could not be restarted: ") + ex.Message, true);
            host.SetStatus($"{label}: {ex.Message}", true);
        }
        finally { recovering = false; host.LaunchStateChanged(); }
    }
}
