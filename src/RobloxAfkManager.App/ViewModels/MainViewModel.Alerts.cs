using RobloxAfkManager.Core;

namespace RobloxAfkManager.App.ViewModels;

// The dashboard side of alerts. Discord, away detection, escalation, the status board and crash reporting live in the engine's
// AlertService; this turns them into the chips, labels and settings the window shows.
public sealed partial class MainViewModel
{
    private AlertService alerts = null!;
    private DiscordWebhook webhook = null!;
    public AsyncCommand TestDiscordCommand { get; private set; } = null!;

    public bool DiscordConfigured => webhook.Configured;
    // One message that lists every account and is edited in place, instead of a stream of new messages.
    public bool BoardHasOwnChannel => webhook.BoardHasOwnWebhook;
    public string BoardSummary => !webhook.BoardEnabled ? "Off." : webhook.BoardHasOwnWebhook ? "The board has its own channel." : "The board is posted in the alerts channel. Add a board webhook below to give it a channel of its own.";
    public void SaveBoardChannel(string? url) { alerts.SaveBoardChannel(url); RaiseDiscord(); }
    public void ClearBoardChannel() { alerts.ClearBoardChannel(); RaiseDiscord(); }
    public bool BoardEnabled { get => webhook.BoardEnabled; set { alerts.SetBoard(value); RaiseDiscord(); } }
    public string DiscordUserId => webhook.UserId ?? "";
    public string DiscordSummary => !webhook.Configured ? "Not set up. Alerts stay on this PC."
        : "Alerts go to your Discord channel." + (webhook.UserId is null ? " Add your user ID to get a phone ping." : " You will be pinged.")
        + (webhook.LastResult.Length > 0 ? " " + webhook.LastResult : "");
    public void SaveDiscord(string? url, string? userId) { alerts.SaveDiscord(url, userId); RaiseDiscord(); }
    public void ClearDiscord() { alerts.ClearDiscord(); RaiseDiscord(); }
    public void Announce(ActivityEntry entry, bool bad, bool info = false, bool quiet = false) => alerts.Announce(entry, bad, info, quiet);

    // ---- away ----
    public int AwayModeIndex { get => (int)alerts.Mode; set { if (Enum.IsDefined((AwayMode)value)) alerts.Mode = (AwayMode)value; else RaiseAway(); } }
    public int AwayMinutes { get => alerts.AwayMinutes; set => alerts.AwayMinutes = value; }
    public bool IsAway => alerts.IsAway;
    public string AwayLabel => alerts.Mode == AwayMode.Away ? "Sleep" : IsAway ? "Away" : "At PC";
    public bool SleepMode => alerts.Mode == AwayMode.Away;
    public string AwayHint => alerts.Mode switch
    {
        AwayMode.Away => "Sleep mode is on: you count as away even at the PC, so Discord alerts and automatic reopening are active. Click to turn it off.",
        AwayMode.AtPc => "At PC (forced): no Discord alerts or reopening. Click to turn Sleep mode on.",
        _ => $"Automatic: you count as away after {alerts.AwayMinutes} minutes without keyboard or mouse. Click to turn Sleep mode on (away even while you use the PC).",
    };
    public void CycleAway() => AwayModeIndex = ((int)alerts.Mode + 1) % 3;
    // Sleep mode: count as away even while using the PC, for when you are busy with something else.
    public void ToggleSleep() => AwayModeIndex = alerts.Mode == AwayMode.Away ? (int)AwayMode.Auto : (int)AwayMode.Away;
    // On = doing its job right now (you are away). Standby = switched on but waiting for you to be away. Off = not set up.
    public string AlertsState => !webhook.Configured ? "Off" : IsAway ? "On" : "Standby";
    public string AlertsLabel => "Alerts " + AlertsState.ToLowerInvariant();
    public string AlertsHint => AlertsState switch
    {
        "On" => "Discord alerts are on: you're away, so problems are sent to Discord and ping you. Click to open the alert settings.",
        "Standby" => "Discord alerts are set up but on standby while you're at the PC. They start when you're away, or in Sleep mode. Click to open the alert settings.",
        _ => "Discord alerts are off. Click to set them up.",
    };
    public string ReopenState => !keeper.AutoReopen ? "Off" : IsAway ? "On" : "Standby";
    public string ReopenLabel => "Rejoin " + ReopenState.ToLowerInvariant();
    public string ReopenHint => ReopenState switch
    {
        "On" => "Automatic rejoin is on: you're away, so crashed or kicked accounts are reopened. Click to turn it off.",
        "Standby" => "Automatic rejoin is on but waiting: it only acts while you're away, or in Sleep mode. Click to turn it off.",
        _ => "Automatic rejoin is off. Click to turn it on: crashed or kicked accounts are reopened while you're away.",
    };
    private void RaiseIndicators() { Raise(nameof(AlertsState)); Raise(nameof(AlertsLabel)); Raise(nameof(AlertsHint)); Raise(nameof(ReopenState)); Raise(nameof(ReopenLabel)); Raise(nameof(ReopenHint)); }
    private void RaiseAway() { RaiseIndicators(); Raise(nameof(AwayModeIndex)); Raise(nameof(AwayMinutes)); Raise(nameof(IsAway)); Raise(nameof(AwayLabel)); Raise(nameof(SleepMode)); Raise(nameof(AwayHint)); }
    private void RaiseDiscord() { Raise(nameof(DiscordConfigured)); RaiseIndicators(); Raise(nameof(BoardEnabled)); Raise(nameof(BoardHasOwnChannel)); Raise(nameof(BoardSummary)); Raise(nameof(ShowAwayChip)); Raise(nameof(DiscordUserId)); Raise(nameof(DiscordSummary)); TestDiscordCommand?.Notify(); }

    private void InitializeDiscord(DiscordWebhook? hook, IUserIdle? userIdle)
    {
        var idle = userIdle ?? (windows is Win32WindowSystem ? new Win32UserIdle() : new NeverIdle());
        // Test hosts never touch the real webhook file, so a test can't post to someone's channel.
        webhook = hook ?? (windows is Win32WindowSystem ? new DiscordWebhook(log: log) : new DiscordWebhook(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rafk-no-webhook"), log: log));
        alerts = new AlertService(this, webhook, idle, launch, keeper, settingsFolder);
        alerts.ActivityAdded += OnActivityAdded;
        alerts.AwayChanged += RaiseAway;
        TestDiscordCommand = new(async () => { await alerts.TestAsync(); RaiseDiscord(); }, () => webhook.Configured, HandleError);
        alerts.WatchForCrashes();
        alerts.AnnounceStarted();
    }
}
