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

// Opens the configured game for every saved account that is not currently running.
public sealed partial class MainViewModel
{
    private static readonly TimeSpan LaunchGrace = TimeSpan.FromMinutes(3);
    private TimeSpan LaunchSpacing => TimeSpan.FromSeconds(spacingSeconds);
    private readonly HashSet<long> selectedLogins = [];

    // Seconds between starting one account and the next. Roblox rate-limits launches, so some people want longer.
    private string settingsFolder = null!;
    private string SpacingFile => System.IO.Path.Combine(settingsFolder, "spacing.txt");
    private int spacingSeconds = 10;
    public int LaunchSpacingSeconds
    {
        get => spacingSeconds;
        set
        {
            int clamped = Math.Clamp(value, 5, 120);
            if (clamped != spacingSeconds)
            {
                spacingSeconds = clamped;
                try { System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SpacingFile)!); System.IO.File.WriteAllText(SpacingFile, clamped.ToString()); }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { Warning = true; Message = "Could not save the spacing: " + ex.Message; }
            }
            Raise();
        }
    }
    private int ReadSpacing()
    {
        try { return System.IO.File.Exists(SpacingFile) && int.TryParse(System.IO.File.ReadAllText(SpacingFile).Trim(), out var seconds) ? Math.Clamp(seconds, 5, 120) : 10; }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { return 10; }
    }

    public int SelectedCount => vault.Logins.Count(l => selectedLogins.Contains(l.UserId) && !IsActive(l.UserId, DateTimeOffset.UtcNow));
    public string OpenSelectedLabel => SelectedCount == 0 ? "Open selected" : $"Open {SelectedCount} selected";
    public AsyncCommand OpenSelectedCommand { get; private set; } = null!;
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
            SaveWorkspace();
            System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(workspace.Read()));
            Warning = false; Message = "Settings saved to the backup file. It holds no logins.";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { Warning = true; Message = "Could not write the backup: " + ex.Message; }
    }
    public void RestoreSettings(string path)
    {
        try
        {
            if (new System.IO.FileInfo(path).Length > 1_000_000) throw new System.IO.InvalidDataException("That file is too large to be a backup.");
            var restored = System.Text.Json.JsonSerializer.Deserialize<WorkspaceSettings>(System.IO.File.ReadAllText(path)) ?? throw new System.IO.InvalidDataException("That file is empty.");
            workspace.Write(restored);          // validates and trims the data
            var clean = workspace.Read();
            profiles.Clear(); foreach (var profile in clean.Profiles) profiles.TryAdd(profile.UserId, profile);
            Activity.Clear(); foreach (var entry in clean.History.Take(100)) Activity.Add(entry);
            notificationsEnabled = clean.Notifications; Raise(nameof(NotificationsEnabled)); Raise(nameof(HasActivity));
            SaveWorkspace(); RefreshLogins();
            Warning = false; Message = $"Restored {profiles.Count} account profiles. Card order applies the next time the manager starts.";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        { Warning = true; Message = "That isn't a backup this manager can read: " + ex.Message; }
    }
    private LoginVault vault = null!;
    private IAccountLauncher launcher = null!;

    // The game every saved account opens. Paste a roblox.com/games/... link or a place id; it is remembered between runs.
    private string GameFile => System.IO.Path.Combine(settingsFolder, "game.txt");
    private long gamePlaceId;
    public long GamePlaceId => gamePlaceId;
    public string GameLink
    {
        get => gamePlaceId > 0 ? gamePlaceId.ToString() : "";
        set
        {
            if (string.IsNullOrWhiteSpace(value)) { gamePlaceId = 0; SaveGame(); }
            else if (ParsePlaceId(value) is { } id) { gamePlaceId = id; SaveGame(); Warning = false; Message = $"Accounts will open game {id}."; }
            else { Warning = true; Message = "That isn't a Roblox game link or place id."; }
            Raise(nameof(GameLink)); Raise(nameof(GamePlaceId)); Raise(nameof(HasClosedLogins)); Raise(nameof(OpenClosedLabel)); OpenClosedCommand?.Notify(); OpenLoginCommand?.Notify();
        }
    }
    public static long? ParsePlaceId(string? text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text ?? "", @"^\s*(?:https?://(?:www\.)?roblox\.com/(?:[a-z-]+/)?games/)?(\d{3,19})(?:[/?#].*)?\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
        return match.Success && long.TryParse(match.Groups[1].Value, out var id) && id > 0 ? id : null;
    }
    private long ReadGame()
    {
        try { return System.IO.File.Exists(GameFile) && new System.IO.FileInfo(GameFile).Length < 1024 ? ParsePlaceId(System.IO.File.ReadAllText(GameFile)) ?? 0 : 0; }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { return 0; }
    }
    private void SaveGame()
    {
        try { System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(GameFile)!); System.IO.File.WriteAllText(GameFile, gamePlaceId > 0 ? gamePlaceId.ToString() : ""); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { Warning = true; Message = "Could not save the game: " + ex.Message; }
    }
    private readonly Dictionary<long, DateTimeOffset> launchedAt = [];
    private bool launching;
    public ObservableCollection<SavedLoginView> SavedLogins { get; } = [];
    public AsyncCommand OpenClosedCommand { get; private set; } = null!;
    public AsyncCommand RemoveLoginCommand { get; private set; } = null!;
    public AsyncCommand OpenLoginCommand { get; private set; } = null!;
    public int ClosedLoginCount => SavedLogins.Count(l => !l.Running);
    public bool HasClosedLogins => ClosedLoginCount > 0 && !launching && gamePlaceId > 0;
    public string OpenClosedLabel => launching ? "Opening…" : gamePlaceId == 0 ? "Set a game first" : ClosedLoginCount == 0 ? "Nothing to open" : $"Open {ClosedLoginCount} closed";

    private void InitializeLaunch(IAccountLauncher? accountLauncher, LoginVault? loginVault, string? folder)
    {
        launcher = accountLauncher ?? new AccountLauncher();
        vault = loginVault ?? new LoginVault();
        settingsFolder = folder ?? AppFolders.Data;
        spacingSeconds = ReadSpacing(); gamePlaceId = ReadGame();
        OpenClosedCommand = new(OpenClosedAsync, () => HasClosedLogins, HandleError);
        OpenSelectedCommand = new(OpenSelectedAsync, () => !launching && gamePlaceId > 0 && SelectedCount > 0, HandleError);
        RestartClientCommand = new(RestartClientAsync, CanRestart, HandleError);
        OpenLoginCommand = new(OpenOneAsync, p => !launching && gamePlaceId > 0 && p is SavedLoginView { Running: false }, HandleError);
        RemoveLoginCommand = new(parameter =>
        {
            if (parameter is SavedLoginView view) { vault.Remove(view.UserId); selectedLogins.Remove(view.UserId); ForgetBrowserProfile(view.UserId); RefreshLogins(); Message = $"{view.Name}'s saved login was removed."; Warning = false; }
            return Task.CompletedTask;
        }, _ => !launching, HandleError);
        RefreshLogins();
    }

    private static void ForgetBrowserProfile(long userId)
    {
        try { var folder = LoginWindow.ProfileFolder(userId); if (System.IO.Directory.Exists(folder)) System.IO.Directory.Delete(folder, true); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { /* Left behind; harmless. */ }
    }

    private void RefreshLogins()
    {
        var now = DateTimeOffset.UtcNow;
        SavedLogins.Clear();
        foreach (var login in vault.Logins.OrderBy(l => l.Username, StringComparer.OrdinalIgnoreCase))
        {
            bool running = runningAccounts.Contains(login.UserId);
            bool starting = !running && launchedAt.TryGetValue(login.UserId, out var at) && now - at < LaunchGrace;
            SavedLogins.Add(new(login.UserId, login.Username, running || starting, running ? "Running" : starting ? "Starting…" : "Closed", selectedLogins.Contains(login.UserId), OnSelected));
        }
        Raise(nameof(ClosedLoginCount)); Raise(nameof(HasClosedLogins)); Raise(nameof(OpenClosedLabel)); Raise(nameof(SelectedCount)); Raise(nameof(OpenSelectedLabel)); OpenClosedCommand?.Notify(); OpenSelectedCommand?.Notify(); OpenLoginCommand?.Notify();
    }

    // Called from the UI with the pasted cookie; the cookie is never kept in memory beyond this call.
    public async Task<bool> AddLoginAsync(string? rawCookie)
    {
        if (AccountLauncher.CleanCookie(rawCookie) is not { } cookie)
        { Warning = true; Message = "That doesn't look like a .ROBLOSECURITY cookie. Paste only its value."; return false; }
        try
        {
            var login = await launcher.IdentifyAsync(cookie, CancellationToken.None);
            vault.Save(login.UserId, login.Username, cookie);
            RefreshLogins();
            Warning = false; Message = $"{login.Username} saved. Their login is encrypted for this Windows user.";
            return true;
        }
        catch (Exception ex) { HandleError(ex); return false; }
    }

    private bool IsActive(long userId, DateTimeOffset now) => runningAccounts.Contains(userId) || (launchedAt.TryGetValue(userId, out var at) && now - at < LaunchGrace);

    private Task OpenClosedAsync() => LaunchAsync(vault.Logins.Where(l => !IsActive(l.UserId, DateTimeOffset.UtcNow)).ToArray());

    private Task OpenSelectedAsync() => LaunchAsync(vault.Logins.Where(l => selectedLogins.Contains(l.UserId) && !IsActive(l.UserId, DateTimeOffset.UtcNow)).ToArray());

    private Task OpenOneAsync(object? parameter) =>
        parameter is SavedLoginView view && vault.Logins.FirstOrDefault(l => l.UserId == view.UserId) is { } login ? LaunchAsync([login]) : Task.CompletedTask;

    private async Task LaunchAsync(SavedLogin[] targets)
    {
        if (launching) return;
        if (gamePlaceId == 0) { Warning = true; Message = "Paste a game link first (Settings → Accounts → Game)."; return; }
        if (targets.Length == 0) { Warning = false; Message = "Every saved account is already running or starting."; return; }
        launching = true; Raise(nameof(HasClosedLogins)); Raise(nameof(OpenClosedLabel)); OpenClosedCommand.Notify(); OpenSelectedCommand.Notify();
        int opened = 0; var failures = new List<string>();
        try
        {
            foreach (var login in targets)
            {
                Warning = false; Message = $"Opening {login.Username} ({opened + 1} of {targets.Length})…";
                try
                {
                    string cookie = vault.Cookie(login.UserId) ?? throw new InvalidOperationException("The saved login could not be read.");
                    int pid = await launcher.LaunchSafelyAsync(cookie, GamePlaceId, CancellationToken.None);
                    launchedPids[pid] = login.UserId;
                    launchedAt[login.UserId] = DateTimeOffset.UtcNow; opened++; RefreshLogins();
                }
                catch (Exception ex) { failures.Add($"{login.Username}: {ex.Message}"); log.Write($"Launching account {login.UserId} failed.", ex); }
                if (login != targets[^1]) await Task.Delay(LaunchSpacing);
            }
        }
        finally { launching = false; RefreshLogins(); }
        Warning = failures.Count > 0;
        Message = failures.Count == 0 ? $"Started {opened} account(s). Use Arrange once their windows appear." : $"Started {opened}; {failures.Count} failed. {failures[0]}";
    }
}
