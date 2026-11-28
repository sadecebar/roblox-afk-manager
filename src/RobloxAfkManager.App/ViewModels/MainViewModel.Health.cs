namespace RobloxAfkManager.App.ViewModels;

// The dashboard side of keeping accounts running. Reopening, login and quiet restarts, second-window cleanup and the Restart
// button live in the engine's HealthService; this turns them into properties and the Restart command.
public sealed partial class MainViewModel
{
    private HealthService keeper = null!;
    private bool recovering => keeper.Recovering;
    private bool reopening => keeper.Reopening;

    public bool AutoReopen
    {
        get => keeper.AutoReopen;
        set { if (keeper.AutoReopen == value) return; keeper.AutoReopen = value; Raise(); Raise(nameof(ShowAwayChip)); RaiseIndicators(); }
    }
    // The Away indicator matters whenever something depends on it.
    public bool ShowAwayChip => webhook.Configured || keeper.AutoReopen;
    public TimeSpan ReopenAfter { get => keeper.ReopenAfter; set => keeper.ReopenAfter = value; }

    public bool AutoRecover { get => keeper.AutoRecover; set { if (keeper.AutoRecover == value) return; keeper.AutoRecover = value; Raise(); Raise(nameof(LoginFixState)); Raise(nameof(LoginFixHint)); } }
    public string LoginFixState => keeper.AutoRecover ? "On" : "Off";
    public string LoginFixHint => keeper.AutoRecover ? "Login restart is on: an account whose Roblox login was ended is closed and reopened. Click to turn it off." : "Login restart is off. Click to turn it on: an account whose Roblox login was ended is closed and reopened.";
    public bool AutoRestartQuiet { get => keeper.AutoRestartQuiet; set { if (keeper.AutoRestartQuiet == value) return; keeper.AutoRestartQuiet = value; Raise(); Raise(nameof(QuietFixState)); Raise(nameof(QuietFixHint)); } }
    public string QuietFixState => keeper.AutoRestartQuiet ? "On" : "Off";
    public string QuietFixHint => keeper.AutoRestartQuiet ? "Quiet-log restart is on (experimental): a client that logs under 100 lines in 10 minutes is restarted. Click to turn it off." : "Quiet-log restart is off (experimental). Click to turn it on.";

    // The per-client Restart button: always allowed, no cooldown or hourly cap, because the person asked for it.
    public AsyncCommand RestartClientCommand { get; private set; } = null!;
    private bool CanRestart(object? parameter) => parameter is ClientTileViewModel tile && keeper.CanRestart(tile.Client);
    private Task RestartClientAsync(object? parameter) => parameter is ClientTileViewModel tile ? keeper.RestartAsync(tile.Client) : Task.CompletedTask;

    public void ToggleReopen()
    {
        if (!keeper.AutoReopen && !HasLaunchTarget) { Warning = true; Message = "Set a game first (Settings → Accounts → Game), then turn automatic rejoin on."; return; }
        if (!keeper.AutoReopen && vault.Logins.Count == 0) { Warning = true; Message = "Automatic rejoin needs a saved login. Add one in Settings → Accounts."; return; }
        AutoReopen = !keeper.AutoReopen;
    }
}
