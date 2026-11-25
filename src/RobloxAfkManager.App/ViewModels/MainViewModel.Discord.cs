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

    private void InitializeDiscord(DiscordWebhook? hook)
    {
        // Test hosts never touch the real webhook file, so a test can't post to someone's channel.
        webhook = hook ?? (windows is Win32WindowSystem ? new DiscordWebhook(log: log) : new DiscordWebhook(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rafk-no-webhook"), log: log));
        TestDiscordCommand = new(async () =>
        {
            string result = await webhook.TestAsync();
            Warning = !result.StartsWith("Last alert sent", StringComparison.Ordinal); Message = Warning ? result : "Test alert sent. Check your Discord channel.";
            RaiseDiscord();
        }, () => webhook.Configured, HandleError);
        if (webhook.Configured) Announce(new(DateTimeOffset.UtcNow, "Manager", "Started. Watching your accounts; alerts will appear here.", false), false);
    }

    private void Announce(ActivityEntry entry, bool bad) { webhook.Enqueue(entry, bad); }
    private void RaiseDiscord() { Raise(nameof(DiscordConfigured)); Raise(nameof(DiscordUserId)); Raise(nameof(DiscordSummary)); TestDiscordCommand?.Notify(); }

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
        webhook.Enqueue(new(DateTimeOffset.UtcNow, "Manager", "Closed. No alerts will arrive until it is started again.", false), false);
        try { Task.Run(webhook.FlushAsync).Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
    }
}
