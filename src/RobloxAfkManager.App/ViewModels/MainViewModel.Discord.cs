using RobloxAfkManager.Core;

namespace RobloxAfkManager.App.ViewModels;

// Sends the Activity alerts (disconnects, closed windows, frozen clients, ended logins, restarts) to a Discord channel,
// so they reach a phone when the PC is out of sight. Nothing is sent until the person saves a webhook.
public sealed partial class MainViewModel
{
    private DiscordWebhook webhook = null!;
    public AsyncCommand TestDiscordCommand { get; private set; } = null!;
    public bool DiscordConfigured => webhook.Configured;
    // One message that lists every account and is edited in place, instead of a stream of new messages.
    public bool BoardHasOwnChannel => webhook.BoardHasOwnWebhook;
    public string BoardSummary => !webhook.BoardEnabled ? "Off." : webhook.BoardHasOwnWebhook ? "The board has its own channel." : "The board is posted in the alerts channel. Add a board webhook below to give it a channel of its own.";
    public void SaveBoardChannel(string? url)
    {
        try { webhook.SaveBoardWebhook(url ?? ""); Warning = false; Message = "The status board will use this channel."; if (webhook.BoardEnabled) UpdateBoard(); }
        catch (ArgumentException ex) { Warning = true; Message = ex.Message; }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { Warning = true; Message = "Could not save the board channel: " + ex.Message; }
        RaiseDiscord();
    }
    public void ClearBoardChannel() { webhook.ClearBoardWebhook(); Warning = false; Message = "The status board now uses the alerts channel."; RaiseDiscord(); if (webhook.BoardEnabled) UpdateBoard(); }
    public bool BoardEnabled
    {
        get => webhook.BoardEnabled;
        set
        {
            if (value && !webhook.Configured && !webhook.BoardHasOwnWebhook) { Warning = true; Message = "Add a webhook first: either the alerts webhook above or a board channel webhook below."; RaiseDiscord(); return; }
            webhook.SetBoard(value); RaiseDiscord(); if (value) UpdateBoard();
        }
    }
    public string DiscordUserId => webhook.UserId ?? "";
    public string DiscordSummary => !webhook.Configured ? "Not set up. Alerts stay on this PC."
        : "Alerts go to your Discord channel." + (webhook.UserId is null ? " Add your user ID to get a phone ping." : " You will be pinged.")
        + (webhook.LastResult.Length > 0 ? " " + webhook.LastResult : "");

    // Alerts are only sent while the person is away. At the PC, the desktop notifications and the Activity list are enough,
    // so closing a client yourself never pings you.
    public enum AwayModes { Auto, Away, AtPc }
    private IUserIdle idle = null!;
    private AwayModes awayMode = AwayModes.Auto;
    private int awayMinutes = 2;
    private bool wasAway;
    private readonly Dictionary<string, ActivityEntry> openProblems = [];
    private string AwayFile => System.IO.Path.Combine(settingsFolder, "away.txt");

    public int AwayModeIndex { get => (int)awayMode; set { if (Enum.IsDefined((AwayModes)value) && (AwayModes)value != awayMode) { awayMode = (AwayModes)value; SaveAway(); } RaiseAway(); UpdateAway(); } }
    public int AwayMinutes { get => awayMinutes; set { awayMinutes = Math.Clamp(value, 1, 240); SaveAway(); RaiseAway(); UpdateAway(); } }
    public bool IsAway => awayMode switch { AwayModes.Away => true, AwayModes.AtPc => false, _ => idle.IdleFor() >= TimeSpan.FromMinutes(awayMinutes) };
    public string AwayLabel => awayMode == AwayModes.Away ? "Sleep" : IsAway ? "Away" : "At PC";
    public bool SleepMode => awayMode == AwayModes.Away;
    public string AwayHint => awayMode switch
    {
        AwayModes.Away => "Sleep mode is on: you count as away even at the PC, so Discord alerts and automatic reopening are active. Click to turn it off.",
        AwayModes.AtPc => "At PC (forced): no Discord alerts or reopening. Click to turn Sleep mode on.",
        _ => $"Automatic: you count as away after {awayMinutes} minutes without keyboard or mouse. Click to turn Sleep mode on (away even while you use the PC).",
    };
    public void CycleAway() => AwayModeIndex = ((int)awayMode + 1) % 3;
    // Sleep mode: count as away even while using the PC, for when you are busy with something else.
    public void ToggleSleep() => AwayModeIndex = awayMode == AwayModes.Away ? (int)AwayModes.Auto : (int)AwayModes.Away;
    // On = doing its job right now (you are away). Standby = switched on but waiting for you to be away. Off = not set up.
    public string AlertsState => !webhook.Configured ? "Off" : IsAway ? "On" : "Standby";
    public string AlertsLabel => "Alerts " + AlertsState.ToLowerInvariant();
    public string AlertsHint => AlertsState switch
    {
        "On" => "Discord alerts are on: you're away, so problems are sent to Discord and ping you. Click to open the alert settings.",
        "Standby" => "Discord alerts are set up but on standby while you're at the PC. They start when you're away, or in Sleep mode. Click to open the alert settings.",
        _ => "Discord alerts are off. Click to set them up.",
    };
    public string ReopenState => !autoReopen ? "Off" : IsAway ? "On" : "Standby";
    public string ReopenLabel => "Rejoin " + ReopenState.ToLowerInvariant();
    public string ReopenHint => ReopenState switch
    {
        "On" => "Automatic rejoin is on: you're away, so crashed or kicked accounts are reopened. Click to turn it off.",
        "Standby" => "Automatic rejoin is on but waiting: it only acts while you're away, or in Sleep mode. Click to turn it off.",
        _ => "Automatic rejoin is off. Click to turn it on: crashed or kicked accounts are reopened while you're away.",
    };
    private void RaiseIndicators() { Raise(nameof(AlertsState)); Raise(nameof(AlertsLabel)); Raise(nameof(AlertsHint)); Raise(nameof(ReopenState)); Raise(nameof(ReopenLabel)); Raise(nameof(ReopenHint)); }
    public void ToggleReopen()
    {
        if (!autoReopen && !HasLaunchTarget) { Warning = true; Message = "Set a game first (Settings → Accounts → Game), then turn automatic rejoin on."; return; }
        if (!autoReopen && vault.Logins.Count == 0) { Warning = true; Message = "Automatic rejoin needs a saved login. Add one in Settings → Accounts."; return; }
        AutoReopen = !autoReopen;
    }
    private void RaiseAway() { RaiseIndicators(); Raise(nameof(AwayModeIndex)); Raise(nameof(AwayMinutes)); Raise(nameof(IsAway)); Raise(nameof(AwayLabel)); Raise(nameof(SleepMode)); Raise(nameof(AwayHint)); }

    private void LoadAway()
    {
        try
        {
            var parts = System.IO.File.Exists(AwayFile) ? System.IO.File.ReadAllText(AwayFile).Split(',') : [];
            if (parts.Length == 2 && int.TryParse(parts[0], out var mode) && Enum.IsDefined((AwayModes)mode) && int.TryParse(parts[1], out var minutes))
            { awayMode = (AwayModes)mode; awayMinutes = Math.Clamp(minutes, 1, 240); }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
    }
    private void SaveAway()
    {
        try { System.IO.Directory.CreateDirectory(settingsFolder); System.IO.File.WriteAllText(AwayFile, $"{(int)awayMode},{awayMinutes}"); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { Warning = true; Message = "Could not save the away setting: " + ex.Message; }
    }

    // Runs on every refresh. When the person steps away, one message lists what is already down, because anything that
    // went wrong while they were at the PC was (on purpose) not sent.
    private void UpdateAway()
    {
        bool away = IsAway;
        if (away == wasAway) return;
        wasAway = away; RaiseAway();
        if (!away || !webhook.Configured) return;
        var cutoff = DateTimeOffset.UtcNow.AddHours(-6);
        foreach (var stale in openProblems.Where(p => p.Value.At < cutoff).Select(p => p.Key).ToArray()) openProblems.Remove(stale);
        if (openProblems.Count == 0) return;
        webhook.Enqueue(new(DateTimeOffset.UtcNow, "You're away", "Still needing attention: " + string.Join(", ", openProblems.Keys.Take(15)) + ".", false), DiscordWebhook.AlertKind.Info);
    }

    private void InitializeDiscord(DiscordWebhook? hook, IUserIdle? userIdle)
    {
        idle = userIdle ?? (windows is Win32WindowSystem ? new Win32UserIdle() : new NeverIdle());
        LoadAway();
        // Test hosts never touch the real webhook file, so a test can't post to someone's channel.
        webhook = hook ?? (windows is Win32WindowSystem ? new DiscordWebhook(log: log) : new DiscordWebhook(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rafk-no-webhook"), log: log));
        TestDiscordCommand = new(async () =>
        {
            string result = await webhook.TestAsync();
            Warning = !result.StartsWith("Last alert sent", StringComparison.Ordinal); Message = Warning ? result : "Test alert sent. Check your Discord channel.";
            RaiseDiscord();
        }, () => webhook.Configured, HandleError);
        wasAway = IsAway;
        WatchForCrashes();
        if (webhook.Configured) Announce(new(DateTimeOffset.UtcNow, "Manager", "Started. Watching your accounts; alerts will appear here.", false), false, info: true);
    }

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
        var saved = vault.Logins.Select(l => l.UserId).ToHashSet();
        long? id = Clients.FirstOrDefault(c => c.Label == label)?.AccountId ?? accountLabels.FirstOrDefault(p => p.Value == label).Key;
        return id is { } known && known != 0 && saved.Contains(known);
    }
    private bool RepairedAutomatically(ActivityEntry entry) => autoReopen && IsRepairable(entry) && HasSavedLogin(entry.Label);
    // Says so when nothing will repair the account, so the alert is not a silent promise.
    private ActivityEntry Explain(ActivityEntry entry) => autoReopen && IsRepairable(entry) && !HasSavedLogin(entry.Label)
        ? entry with { Message = entry.Message + " No saved login for this account, so it cannot be restarted automatically." } : entry;

    // Anything still reported as down after this long was not fixed by the automatic repair, so it now pings.
    private static readonly TimeSpan EscalateAfter = TimeSpan.FromMinutes(15);
    private readonly HashSet<string> escalated = [];
    private void EscalateStuck()
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

    // A crash of the manager itself is the one thing it cannot report from a normal alert, so it is sent on the way down,
    // and a marker file lets the next start report a crash it could not (power loss, forced close).
    private string RunningMarker => System.IO.Path.Combine(settingsFolder, "running.marker");
    private void WatchForCrashes()
    {
        try
        {
            if (System.IO.File.Exists(RunningMarker))
            {
                var text = System.IO.File.ReadAllText(RunningMarker).Trim();
                if (text != "reported")
                {
                    string since = System.IO.File.GetLastWriteTime(RunningMarker).ToString("g");
                    var entry = new ActivityEntry(DateTimeOffset.UtcNow, "Manager", $"The manager was not closed properly last time (crash, power loss or forced close, last running around {since}). Accounts may have been unattended until now.", true);
                    Journal(entry); webhook.Enqueue(entry, DiscordWebhook.AlertKind.Problem);
                }
            }
            System.IO.Directory.CreateDirectory(settingsFolder); System.IO.File.WriteAllText(RunningMarker, "running");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
        AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
    }
    private void OnUnhandled(object? sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            var entry = new ActivityEntry(DateTimeOffset.UtcNow, "Manager", "The manager crashed and stopped: " + (e.ExceptionObject as Exception)?.GetType().Name + ". Nothing is watching or restarting your accounts until it is started again.", true);
            Journal(entry);
            if (webhook.Configured) { webhook.Enqueue(entry, DiscordWebhook.AlertKind.Problem); Task.Run(() => webhook.FlushAsync()).Wait(TimeSpan.FromSeconds(5)); }
            System.IO.File.WriteAllText(RunningMarker, "reported");
        }
        catch (Exception) { }
    }
    private void StopWatchingForCrashes()
    {
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
        try { System.IO.File.Delete(RunningMarker); } catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
    }

    // A closed window has no "back" event of its own: the account is back when a client with the same name shows up again.
    private void AnnounceReturns()
    {
        if (!webhook.Configured || openProblems.Count == 0) return;
        foreach (var label in openProblems.Where(p => p.Value.Message.StartsWith("Window closed", StringComparison.Ordinal)).Select(p => p.Key).ToArray())
            if (Clients.Any(c => !c.IsIgnored && c.Label == label && (c.Session is null || c.Session.Phase == SessionPhase.Joined)))
                Announce(new(DateTimeOffset.UtcNow, label, "Back online.", false), false);
    }
    private void RaiseDiscord() { Raise(nameof(DiscordConfigured)); RaiseIndicators(); Raise(nameof(BoardEnabled)); Raise(nameof(BoardHasOwnChannel)); Raise(nameof(BoardSummary)); Raise(nameof(ShowAwayChip)); Raise(nameof(DiscordUserId)); Raise(nameof(DiscordSummary)); TestDiscordCommand?.Notify(); }

    public void SaveDiscord(string? url, string? userId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url) && webhook.Configured) webhook.SetUserId(userId); else webhook.Save(url ?? "", userId);
            Warning = false; Message = "Discord alerts are on. Press Test to check them.";
        }
        catch (ArgumentException ex) { Warning = true; Message = ex.Message; }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { Warning = true; Message = "Could not save the Discord settings: " + ex.Message; }
        RaiseDiscord();
    }

    public void ClearDiscord() { webhook.Clear(); Warning = false; Message = "Discord alerts are off and the webhook was deleted from this PC."; RaiseDiscord(); }

    private void AnnounceClosing()
    {
        if (!webhook.Configured) return;
        if (IsAway) webhook.Enqueue(new(DateTimeOffset.UtcNow, "Manager", "Closed. No alerts will arrive until it is started again.", false), DiscordWebhook.AlertKind.Info);
        try { Task.Run(async () => { await webhook.FlushAsync(); await webhook.CloseBoardAsync(); }).Wait(TimeSpan.FromSeconds(4)); } catch (AggregateException) { }
    }

    // Builds one line per account from what the manager knows right now: running clients, then saved logins and recently seen
    // accounts that are not running.
    private void UpdateBoard()
    {
        if (!webhook.BoardEnabled) return;
        var now = DateTimeOffset.UtcNow;
        var rows = new List<BoardRow>(); var seen = new HashSet<long>();
        foreach (var tile in Clients.Where(c => !c.IsIgnored))
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
        var others = vault.Logins.Select(l => (Id: l.UserId, Name: l.Username))
            .Concat(profiles.Values.Where(p => !p.Ignored && p.LastSeen is { } seenAt && now - seenAt < TimeSpan.FromHours(24)).Select(p => (Id: p.UserId, Name: p.CustomName ?? p.Username ?? "Account " + p.UserId)))
            .Where(a => !seen.Contains(a.Id)).GroupBy(a => a.Id).Select(g => g.First());
        foreach (var (id, saved) in others)
        {
            profiles.TryGetValue(id, out var profile);
            string name = profile?.CustomName ?? profile?.Username ?? saved;
            if (pendingReopen.ContainsKey(id)) rows.Add(new(name, BoardState.Warn, "Closed · reopening soon"));
            else if (launch.IsStarting(id, now)) rows.Add(new(name, BoardState.Warn, "Starting…"));
            else if (profile?.LastSeen is { } last) rows.Add(new(name, BoardState.Down, $"Closed · last seen <t:{last.ToUnixTimeSeconds()}:R>"));
            else rows.Add(new(name, BoardState.Idle, "Not running"));
        }
        webhook.QueueBoard(rows);
    }
}
