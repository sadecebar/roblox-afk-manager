using System.IO;
using RobloxAfkManager.Core;

namespace RobloxAfkManager.Engine;

// Alerts only go out while the person is away. At the PC, the desktop notifications and the Activity list are enough, so
// closing a client yourself never pings you. Sleep = away even at the PC; AtPc = never away.
public enum AwayMode { Auto, Away, AtPc }

// Everything that tells the person what happened:
// - the activity entries (and the incident log on disk, the morning-after record of problems and fixes),
// - Discord: problems ping while away, repairs the manager makes itself are posted without a ping, a problem that is still there
//   after 15 minutes pings, and the message turns green when the account is back,
// - away detection (keyboard/mouse idle time, Sleep mode),
// - the status board (one Discord message listing every account, edited in place),
// - a crash of the manager itself.
public sealed class AlertService
{
    private readonly IEngineHost host;
    private readonly DiscordWebhook webhook;
    private readonly IUserIdle idle;
    private readonly LaunchService launch;
    private readonly HealthService keeper;
    private readonly string folder;

    public AlertService(IEngineHost host, DiscordWebhook webhook, IUserIdle idle, LaunchService launch, HealthService keeper, string settingsFolder)
    {
        this.host = host; this.webhook = webhook; this.idle = idle; this.launch = launch; this.keeper = keeper; folder = settingsFolder;
        LoadAway();
        wasAway = IsAway;
    }

    public DiscordWebhook Webhook => webhook;
    // A new activity entry. alert: it is a warning the person should see (desktop notification, unread count).
    public event Action<ActivityEntry, bool>? ActivityAdded;
    // Away / at-PC changed (or its settings did).
    public event Action? AwayChanged;

    // ---- incident log and activity ------------------------------------------------------------------------------------------
    private IncidentJournal? incidents;
    // Where the morning-after record of problems and fixes is kept (next to the other settings).
    public string IncidentLogPath => Path.Combine(folder, "incidents.log");
    public void Journal(ActivityEntry entry) => (incidents ??= new(IncidentLogPath)).Write(entry);

    public void Add(string label, string message, bool warning, bool announce = true, bool info = false, bool quiet = false)
    {
        var entry = new ActivityEntry(DateTimeOffset.UtcNow, label, message, warning);
        Journal(entry);
        ActivityAdded?.Invoke(entry, warning);
        if (announce) Announce(entry, warning, info, quiet);
    }

    // What the session health tracker noticed on this refresh (disconnects, closed windows, frozen clients, rejoins).
    public void Report(IReadOnlyList<ActivityEntry> events)
    {
        foreach (var raw in events)
        {
            if (launch.IsReconnecting(raw.Label)) { Journal(raw); continue; } // Reset servers closes windows on purpose
            var entry = Explain(raw);
            Journal(entry);
            ActivityAdded?.Invoke(entry, entry.Warning);
            Announce(entry, entry.Warning, quiet: RepairedAutomatically(entry));
        }
    }

    // ---- away ---------------------------------------------------------------------------------------------------------------------
    private AwayMode awayMode = AwayMode.Auto;
    private int awayMinutes = 2;
    private bool wasAway;
    private string AwayFile => Path.Combine(folder, "away.txt");
    public AwayMode Mode
    {
        get => awayMode;
        set { if (Enum.IsDefined(value) && value != awayMode) { awayMode = value; SaveAway(); } AwayChanged?.Invoke(); UpdateAway(); }
    }
    public int AwayMinutes
    {
        get => awayMinutes;
        set { awayMinutes = Math.Clamp(value, 1, 240); SaveAway(); AwayChanged?.Invoke(); UpdateAway(); }
    }
    public bool IsAway => awayMode switch { AwayMode.Away => true, AwayMode.AtPc => false, _ => idle.IdleFor() >= TimeSpan.FromMinutes(awayMinutes) };

    private void LoadAway()
    {
        try
        {
            var parts = File.Exists(AwayFile) ? File.ReadAllText(AwayFile).Split(',') : [];
            if (parts.Length == 2 && int.TryParse(parts[0], out var mode) && Enum.IsDefined((AwayMode)mode) && int.TryParse(parts[1], out var minutes))
            { awayMode = (AwayMode)mode; awayMinutes = Math.Clamp(minutes, 1, 240); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private void SaveAway()
    {
        try { Directory.CreateDirectory(folder); File.WriteAllText(AwayFile, $"{(int)awayMode},{awayMinutes}"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { host.SetStatus("Could not save the away setting: " + ex.Message, true); }
    }

    // Runs on every refresh. When the person steps away, one message lists what is already down, because anything that
    // went wrong while they were at the PC was (on purpose) not sent.
    public void UpdateAway()
    {
        bool away = IsAway;
        if (away == wasAway) return;
        wasAway = away; AwayChanged?.Invoke();
        if (!away || !webhook.Configured) return;
        var cutoff = DateTimeOffset.UtcNow.AddHours(-6);
        foreach (var stale in openProblems.Where(p => p.Value.At < cutoff).Select(p => p.Key).ToArray()) openProblems.Remove(stale);
        if (openProblems.Count == 0) return;
        webhook.Enqueue(new(DateTimeOffset.UtcNow, "You're away", "Still needing attention: " + string.Join(", ", openProblems.Keys.Take(15)) + ".", false), DiscordWebhook.AlertKind.Info);
    }

    // ---- Discord ---------------------------------------------------------------------------------------------------------------------
    private readonly Dictionary<string, ActivityEntry> openProblems = [];

    // bad: a problem (posted, pings while away). Not bad: the account is fine again, which edits the message that reported
    // the problem instead of posting another. info: a plain message such as "manager started", sent only while away.
    public void Announce(ActivityEntry entry, bool bad, bool info = false, bool quiet = false)
    {
        if (!webhook.Configured) return;
        if (bad) openProblems[entry.Label] = entry; else if (!info) openProblems.Remove(entry.Label);
        UpdateAway();
        if (!bad && !info) { webhook.Enqueue(entry, DiscordWebhook.AlertKind.Recovery); return; }
        if (IsAway) webhook.Enqueue(entry, bad ? (quiet ? DiscordWebhook.AlertKind.Notice : DiscordWebhook.AlertKind.Problem) : DiscordWebhook.AlertKind.Info);
    }

    // A disconnect or closed window the manager repairs by itself (Auto-reopen on and a saved login) is posted without a
    // ping: the person only needs to know if the repair fails, which is its own pinging alert.
    private static bool IsRepairable(ActivityEntry entry) => entry.Warning
        && (entry.Message.StartsWith("Roblox's log reports a disconnect", StringComparison.Ordinal) || entry.Message.StartsWith("Window closed", StringComparison.Ordinal));
    private bool HasSavedLogin(string label)
    {
        var saved = launch.Vault.Logins.Select(l => l.UserId).ToHashSet();
        long? id = host.Clients.FirstOrDefault(c => c.Label == label)?.AccountId ?? keeper.AccountLabels.FirstOrDefault(p => p.Value == label).Key;
        return id is { } known && known != 0 && saved.Contains(known);
    }
    private bool RepairedAutomatically(ActivityEntry entry) => keeper.AutoReopen && IsRepairable(entry) && HasSavedLogin(entry.Label);
    // Says so when nothing will repair the account, so the alert is not a silent promise.
    private ActivityEntry Explain(ActivityEntry entry) => keeper.AutoReopen && IsRepairable(entry) && !HasSavedLogin(entry.Label)
        ? entry with { Message = entry.Message + " No saved login for this account, so it cannot be restarted automatically." } : entry;

    // Anything still reported as down after this long was not fixed by the automatic repair, so it now pings.
    private static readonly TimeSpan EscalateAfter = TimeSpan.FromMinutes(15);
    private readonly HashSet<string> escalated = [];
    public void EscalateStuck()
    {
        if (!webhook.Configured || !IsAway) return;
        escalated.RemoveWhere(l => !openProblems.ContainsKey(l));
        var now = DateTimeOffset.UtcNow;
        foreach (var (label, entry) in openProblems.Where(p => now - p.Value.At >= EscalateAfter && !escalated.Contains(p.Key)).ToArray())
        {
            escalated.Add(label);
            var again = new ActivityEntry(now, label, $"Still not fixed {(int)EscalateAfter.TotalMinutes} minutes after: {entry.Message}", true);
            Journal(again);
            Announce(again, true);
        }
    }

    // A closed window has no "back" event of its own: the account is back when a client with the same name shows up again.
    public void AnnounceReturns()
    {
        if (!webhook.Configured || openProblems.Count == 0) return;
        foreach (var label in openProblems.Where(p => p.Value.Message.StartsWith("Window closed", StringComparison.Ordinal)).Select(p => p.Key).ToArray())
            if (host.Clients.Any(c => !c.IsIgnored && c.Label == label && (c.Session is null || c.Session.Phase == SessionPhase.Joined)))
                Announce(new(DateTimeOffset.UtcNow, label, "Back online.", false), false);
    }

    public void AnnounceStarted()
    {
        if (webhook.Configured) Announce(new(DateTimeOffset.UtcNow, "Manager", "Started. Watching your accounts; alerts will appear here.", false), false, info: true);
    }

    public void AnnounceClosing()
    {
        if (!webhook.Configured) return;
        if (IsAway) webhook.Enqueue(new(DateTimeOffset.UtcNow, "Manager", "Closed. No alerts will arrive until it is started again.", false), DiscordWebhook.AlertKind.Info);
        try { Task.Run(async () => { await webhook.FlushAsync(); await webhook.CloseBoardAsync(); }).Wait(TimeSpan.FromSeconds(4)); } catch (AggregateException) { }
    }

    // ---- the manager's own crash -------------------------------------------------------------------------------------------------
    // A crash of the manager itself is the one thing it cannot report from a normal alert, so it is sent on the way down,
    // and a marker file lets the next start report a crash it could not (power loss, forced close).
    private string RunningMarker => Path.Combine(folder, "running.marker");
    public void WatchForCrashes()
    {
        try
        {
            if (File.Exists(RunningMarker))
            {
                var text = File.ReadAllText(RunningMarker).Trim();
                if (text != "reported")
                {
                    string since = File.GetLastWriteTime(RunningMarker).ToString("g");
                    var entry = new ActivityEntry(DateTimeOffset.UtcNow, "Manager", $"The manager was not closed properly last time (crash, power loss or forced close, last running around {since}). Accounts may have been unattended until now.", true);
                    Journal(entry); webhook.Enqueue(entry, DiscordWebhook.AlertKind.Problem);
                }
            }
            Directory.CreateDirectory(folder); File.WriteAllText(RunningMarker, "running");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
    }
    private void OnUnhandled(object? sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            var entry = new ActivityEntry(DateTimeOffset.UtcNow, "Manager", "The manager crashed and stopped: " + (e.ExceptionObject as Exception)?.GetType().Name + ". Nothing is watching or restarting your accounts until it is started again.", true);
            Journal(entry);
            if (webhook.Configured) { webhook.Enqueue(entry, DiscordWebhook.AlertKind.Problem); Task.Run(() => webhook.FlushAsync()).Wait(TimeSpan.FromSeconds(5)); }
            File.WriteAllText(RunningMarker, "reported");
        }
        catch (Exception) { }
    }
    public void StopWatchingForCrashes()
    {
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
        try { File.Delete(RunningMarker); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---- status board -------------------------------------------------------------------------------------------------------------
    // Builds one line per account from what the manager knows right now: running clients, then saved logins and recently seen
    // accounts that are not running.
    public void UpdateBoard()
    {
        if (!webhook.BoardEnabled) return;
        var now = DateTimeOffset.UtcNow;
        var rows = new List<BoardRow>(); var seen = new HashSet<long>();
        foreach (var tile in host.Clients.Where(c => !c.IsIgnored))
        {
            if (tile.AccountId is { } running) seen.Add(running);
            var session = tile.Session;
            if (tile.Status == "Not responding") rows.Add(new(tile.Label, BoardState.Down, "Not responding"));
            else if (session is { Phase: SessionPhase.Disconnected }) rows.Add(new(tile.Label, BoardState.Down, session.EventAt is { } at ? $"Disconnected · <t:{at.ToUnixTimeSeconds()}:R>" : "Disconnected"));
            else if (session is { Phase: SessionPhase.Joining }) rows.Add(session.EventAt is { } started && now - started > TimeSpan.FromMinutes(2)
                ? new(tile.Label, BoardState.Warn, "Possibly stuck joining") : new(tile.Label, BoardState.Warn, "Joining…"));
            else if (session is { Phase: SessionPhase.Joined, JoinedAt: { } joined }) rows.Add(new(tile.Label, BoardState.Ok, $"Joined · since <t:{joined.ToUnixTimeSeconds()}:R>"));
            else rows.Add(new(tile.Label, BoardState.Ok, "Running"));
        }
        var profiles = host.Profiles.ToDictionary(p => p.UserId);
        var others = launch.Vault.Logins.Select(l => (Id: l.UserId, Name: l.Username))
            .Concat(profiles.Values.Where(p => !p.Ignored && p.LastSeen is { } seenAt && now - seenAt < TimeSpan.FromHours(24)).Select(p => (Id: p.UserId, Name: p.CustomName ?? p.Username ?? "Account " + p.UserId)))
            .Where(a => !seen.Contains(a.Id)).GroupBy(a => a.Id).Select(g => g.First());
        foreach (var (id, saved) in others)
        {
            profiles.TryGetValue(id, out var profile);
            string name = profile?.CustomName ?? profile?.Username ?? saved;
            if (keeper.IsPendingReopen(id)) rows.Add(new(name, BoardState.Warn, "Closed · reopening soon"));
            else if (launch.IsStarting(id, now)) rows.Add(new(name, BoardState.Warn, "Starting…"));
            else if (profile?.LastSeen is { } last) rows.Add(new(name, BoardState.Down, $"Closed · last seen <t:{last.ToUnixTimeSeconds()}:R>"));
            else rows.Add(new(name, BoardState.Idle, "Not running"));
        }
        webhook.QueueBoard(rows);
    }

    // ---- Discord settings ----------------------------------------------------------------------------------------------------------
    public void SaveDiscord(string? url, string? userId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url) && webhook.Configured) webhook.SetUserId(userId); else webhook.Save(url ?? "", userId);
            host.SetStatus("Discord alerts are on. Press Test to check them.", false);
        }
        catch (ArgumentException ex) { host.SetStatus(ex.Message, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { host.SetStatus("Could not save the Discord settings: " + ex.Message, true); }
    }
    public void ClearDiscord() { webhook.Clear(); host.SetStatus("Discord alerts are off and the webhook was deleted from this PC.", false); }
    public void SaveBoardChannel(string? url)
    {
        try { webhook.SaveBoardWebhook(url ?? ""); host.SetStatus("The status board will use this channel.", false); if (webhook.BoardEnabled) UpdateBoard(); }
        catch (ArgumentException ex) { host.SetStatus(ex.Message, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { host.SetStatus("Could not save the board channel: " + ex.Message, true); }
    }
    public void ClearBoardChannel() { webhook.ClearBoardWebhook(); host.SetStatus("The status board now uses the alerts channel.", false); if (webhook.BoardEnabled) UpdateBoard(); }
    // False when there is no webhook to post the board to.
    public bool SetBoard(bool on)
    {
        if (on && !webhook.Configured && !webhook.BoardHasOwnWebhook) { host.SetStatus("Add a webhook first: either the alerts webhook above or a board channel webhook below.", true); return false; }
        webhook.SetBoard(on); if (on) UpdateBoard();
        return true;
    }
    public async Task TestAsync()
    {
        string result = await webhook.TestAsync();
        bool failed = !result.StartsWith("Last alert sent", StringComparison.Ordinal);
        host.SetStatus(failed ? result : "Test alert sent. Check your Discord channel.", failed);
    }
}
