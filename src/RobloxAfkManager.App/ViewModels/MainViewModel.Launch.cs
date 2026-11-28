using System.Collections.ObjectModel;
using RobloxAfkManager.App.Services;
using RobloxAfkManager.Core;

namespace RobloxAfkManager.App.ViewModels;

public sealed class SavedLoginView(long userId, string name, bool running, string status, bool selected, Action<long, bool> changed) : ObservableObject
{
    private bool selected = selected;
    public long UserId { get; } = userId;
    public string Name { get; } = name;
    public bool Running { get; } = running;
    public string Status { get; } = status;
    public bool Selected { get => selected; set { if (Set(ref selected, value)) changed(UserId, value); } }
}

// The dashboard side of starting accounts. What actually happens (servers, the launch gap, fallback, Reset servers) lives in the
// engine's LaunchService; this only turns it into properties and commands for the window.
public sealed partial class MainViewModel
{
    private LaunchService launch = null!;
    private LoginVault vault = null!;
    private readonly HashSet<long> selectedLogins = [];

    public int LaunchSpacingSeconds { get => launch.LaunchSpacingSeconds; set { launch.LaunchSpacingSeconds = value; Raise(); } }
    private TimeSpan LaunchSpacing => launch.LaunchSpacing;
    public long GamePlaceId => launch.GamePlaceId;
    public string GameLink { get => launch.GamePlaceId > 0 ? launch.GamePlaceId.ToString() : ""; set => launch.SetGame(value); }
    public static long? ParsePlaceId(string? text) => LaunchService.ParsePlaceId(text);
    public bool HasLaunchTarget => launch.HasLaunchTarget;
    public string PrivateServer1Link { get => launch.PrivateServer1Link; set => launch.SetServerLink(1, value); }
    public string PrivateServer2Link { get => launch.PrivateServer2Link; set => launch.SetServerLink(2, value); }
    public int AccountsOnServer1 { get => launch.AccountsOnServer1; set => launch.AccountsOnServer1 = value; }
    public string ServerSummary => launch.ServerSummary;
    public TimeSpan ResetDelay { get => launch.ResetDelay; set => launch.ResetDelay = value; }
    public TimeSpan PrivateJoinPatience { get => launch.PrivateJoinPatience; set => launch.PrivateJoinPatience = value; }

    public ObservableCollection<SavedLoginView> SavedLogins { get; } = [];
    public AsyncCommand OpenClosedCommand { get; private set; } = null!;
    public AsyncCommand OpenSelectedCommand { get; private set; } = null!;
    public AsyncCommand RemoveLoginCommand { get; private set; } = null!;
    public AsyncCommand OpenLoginCommand { get; private set; } = null!;
    public AsyncCommand ResetServersCommand { get; private set; } = null!;
    public AsyncCommand ResplitServersCommand { get; private set; } = null!;
    public int ClosedLoginCount => SavedLogins.Count(l => !l.Running);
    public bool HasClosedLogins => ClosedLoginCount > 0 && !launch.Launching && HasLaunchTarget;
    public string OpenClosedLabel => launch.Launching ? "Opening…" : !HasLaunchTarget ? "Set a game first" : ClosedLoginCount == 0 ? "Nothing to open" : $"Open {ClosedLoginCount} closed";
    public int SelectedCount => vault.Logins.Count(l => selectedLogins.Contains(l.UserId) && !launch.IsActive(l.UserId, DateTimeOffset.UtcNow));
    public string OpenSelectedLabel => SelectedCount == 0 ? "Open selected" : $"Open {SelectedCount} selected";

    private void OnSelected(long userId, bool on)
    {
        if (on) selectedLogins.Add(userId); else selectedLogins.Remove(userId);
        Raise(nameof(SelectedCount)); Raise(nameof(OpenSelectedLabel)); OpenSelectedCommand?.Notify();
    }

    // Preferences only (names, order, ignored accounts, saved window positions, activity). Never logins or cookies.
    public void BackupSettings(string path)
    {
        try
        {
            work.Backup(path);
            Warning = false; Message = "Settings saved to the backup file. It holds no logins.";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { Warning = true; Message = "Could not write the backup: " + ex.Message; }
    }
    public void RestoreSettings(string path)
    {
        try
        {
            int count = work.Restore(path);
            Activity.Clear(); foreach (var entry in work.History) Activity.Add(entry);
            Raise(nameof(NotificationsEnabled)); Raise(nameof(HasActivity));
            SaveWorkspace(); RefreshLogins();
            Warning = false; Message = $"Restored {count} account profiles. Card order applies the next time the manager starts.";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        { Warning = true; Message = "That isn't a backup this manager can read: " + ex.Message; }
    }

    private void InitializeLaunch()
    {
        launch = engine.Launch; vault = launch.Vault; keeper = engine.Health;
        OpenClosedCommand = new(launch.OpenClosedAsync, () => HasClosedLogins, HandleError);
        OpenSelectedCommand = new(() => launch.LaunchAsync(vault.Logins.Where(l => selectedLogins.Contains(l.UserId) && !launch.IsActive(l.UserId, DateTimeOffset.UtcNow)).ToArray()),
            () => !launch.Launching && HasLaunchTarget && SelectedCount > 0, HandleError);
        RestartClientCommand = new(RestartClientAsync, CanRestart, HandleError);
        OpenLoginCommand = new(parameter => parameter is SavedLoginView view && vault.Logins.FirstOrDefault(l => l.UserId == view.UserId) is { } login ? launch.LaunchAsync([login]) : Task.CompletedTask,
            p => !launch.Launching && HasLaunchTarget && p is SavedLoginView { Running: false }, HandleError);
        RemoveLoginCommand = new(parameter =>
        {
            if (parameter is SavedLoginView view) { vault.Remove(view.UserId); selectedLogins.Remove(view.UserId); LoginProfiles.Forget(view.UserId); RefreshLogins(); Message = $"{view.Name}'s saved login was removed."; Warning = false; }
            return Task.CompletedTask;
        }, _ => !launch.Launching, HandleError);
        ResetServersCommand = new(launch.ResetServersAsync, () => launch.CanResetServers(engine.Disposed), HandleError);
        ResplitServersCommand = new(() => { launch.Resplit(); return Task.CompletedTask; }, () => !launch.Launching, HandleError);
        RefreshLogins();
    }

    private void RaiseServers()
    {
        Raise(nameof(PrivateServer1Link)); Raise(nameof(PrivateServer2Link)); Raise(nameof(AccountsOnServer1)); Raise(nameof(ServerSummary)); Raise(nameof(HasLaunchTarget));
        Raise(nameof(GameLink)); Raise(nameof(GamePlaceId));
        Raise(nameof(HasClosedLogins)); Raise(nameof(OpenClosedLabel)); OpenClosedCommand?.Notify(); OpenSelectedCommand?.Notify(); OpenLoginCommand?.Notify(); ResetServersCommand?.Notify(); ResplitServersCommand?.Notify();
    }

    private void RefreshLogins()
    {
        var now = DateTimeOffset.UtcNow;
        var preview = launch.HasServer1 && launch.HasServer2 ? launch.Assignments(commit: false) : null;
        SavedLogins.Clear();
        foreach (var login in vault.Logins.OrderBy(l => l.Username, StringComparer.OrdinalIgnoreCase))
        {
            bool running = runningAccounts.Contains(login.UserId);
            bool starting = !running && launch.IsStarting(login.UserId, now);
            string server = launch.ServerShownFor(login.UserId, preview) is { } number ? $" · server {number}" : "";
            SavedLogins.Add(new(login.UserId, login.Username, running || starting, (running ? "Running" : starting ? "Starting…" : "Closed") + server, selectedLogins.Contains(login.UserId), OnSelected));
        }
        Raise(nameof(ClosedLoginCount)); Raise(nameof(HasClosedLogins)); Raise(nameof(OpenClosedLabel)); Raise(nameof(SelectedCount)); Raise(nameof(OpenSelectedLabel)); OpenClosedCommand?.Notify(); OpenSelectedCommand?.Notify(); OpenLoginCommand?.Notify(); ResetServersCommand?.Notify();
    }

    // Called from the UI with the pasted cookie; the cookie is never kept in memory beyond this call.
    public async Task<bool> AddLoginAsync(string? rawCookie)
    {
        if (AccountLauncher.CleanCookie(rawCookie) is not { } cookie)
        { Warning = true; Message = "That doesn't look like a .ROBLOSECURITY cookie. Paste only its value."; return false; }
        try
        {
            var login = await launch.Launcher.IdentifyAsync(cookie, CancellationToken.None);
            vault.Save(login.UserId, login.Username, cookie);
            RefreshLogins();
            Warning = false; Message = $"{login.Username} saved. Their login is encrypted for this Windows user.";
            return true;
        }
        catch (Exception ex) { HandleError(ex); return false; }
    }
}
