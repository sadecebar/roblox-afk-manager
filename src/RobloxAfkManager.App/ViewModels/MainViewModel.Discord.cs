using RobloxAfkManager.App.Services;
using RobloxAfkManager.Core;

namespace RobloxAfkManager.App.ViewModels;

// Sends the Activity alerts (disconnects, closed windows, frozen clients, ended logins, restarts) to a Discord channel,
// so they reach a phone when the PC is out of sight. Nothing is sent until the person saves a webhook.
public sealed partial class MainViewModel
{
    private DiscordWebhook webhook = null!;
    public AsyncCommand TestDiscordCommand { get; private set; } = null!;
    public bool DiscordConfigured => webhook.Configured;
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
    public string AwayLabel => IsAway ? "Away" : "At PC";
    public string AwayHint => awayMode switch
    {
        AwayModes.Away => "Away (forced): Discord alerts and pings are on. Click to change.",
        AwayModes.AtPc => "At PC (forced): no Discord alerts. Click to change.",
        _ => $"Automatic: you count as away after {awayMinutes} minutes without keyboard or mouse. Click to change.",
    };
    public void CycleAway() => AwayModeIndex = ((int)awayMode + 1) % 3;
    private void RaiseAway() { Raise(nameof(AwayModeIndex)); Raise(nameof(AwayMinutes)); Raise(nameof(IsAway)); Raise(nameof(AwayLabel)); Raise(nameof(AwayHint)); }

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
        if (webhook.Configured) Announce(new(DateTimeOffset.UtcNow, "Manager", "Started. Watching your accounts; alerts will appear here.", false), false, info: true);
    }

    // bad: a problem (posted, pings while away). Not bad: the account is fine again, which edits the message that reported
    // the problem instead of posting another. info: a plain message such as "manager started", sent only while away.
    public void Announce(ActivityEntry entry, bool bad, bool info = false)
    {
        if (!webhook.Configured) return;
        if (bad) openProblems[entry.Label] = entry; else if (!info) openProblems.Remove(entry.Label);
        UpdateAway();
        if (!bad && !info) { webhook.Enqueue(entry, DiscordWebhook.AlertKind.Recovery); return; }
        if (IsAway) webhook.Enqueue(entry, bad ? DiscordWebhook.AlertKind.Problem : DiscordWebhook.AlertKind.Info);
    }

    // A closed window has no "back" event of its own: the account is back when a client with the same name shows up again.
    private void AnnounceReturns()
    {
        if (!webhook.Configured || openProblems.Count == 0) return;
        foreach (var label in openProblems.Where(p => p.Value.Message.StartsWith("Window closed", StringComparison.Ordinal)).Select(p => p.Key).ToArray())
            if (Clients.Any(c => !c.IsIgnored && c.Label == label && (c.Session is null || c.Session.Phase == SessionPhase.Joined)))
                Announce(new(DateTimeOffset.UtcNow, label, "Back online.", false), false);
    }
    private void RaiseDiscord() { Raise(nameof(DiscordConfigured)); Raise(nameof(ShowAwayChip)); Raise(nameof(DiscordUserId)); Raise(nameof(DiscordSummary)); TestDiscordCommand?.Notify(); }

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
        if (!webhook.Configured || !IsAway) return;
        webhook.Enqueue(new(DateTimeOffset.UtcNow, "Manager", "Closed. No alerts will arrive until it is started again.", false), DiscordWebhook.AlertKind.Info);
        try { Task.Run(webhook.FlushAsync).Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
    }
}
