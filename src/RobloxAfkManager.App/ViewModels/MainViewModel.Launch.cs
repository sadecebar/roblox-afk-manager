using System.Collections.ObjectModel;
using RobloxAfkManager.App.Services;

namespace RobloxAfkManager.App.ViewModels;

public sealed record SavedLoginView(long UserId, string Name, bool Running, string Status);

// Opens the configured game for every saved account that is not currently running.
public sealed partial class MainViewModel
{
    private static readonly TimeSpan LaunchSpacing = TimeSpan.FromSeconds(10), LaunchGrace = TimeSpan.FromMinutes(3);
    private readonly LoginVault vault = new();
    private readonly AccountLauncher launcher = new();

    // The game every saved account opens. Paste a roblox.com/games/... link or a place id; it is remembered between runs.
    private static string GameFile => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RobloxAfkManager", "game.txt");
    private long gamePlaceId = ReadGame();
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
    private static long ReadGame()
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

    private void InitializeLaunch()
    {
        OpenClosedCommand = new(OpenClosedAsync, () => HasClosedLogins, HandleError);
        OpenLoginCommand = new(OpenOneAsync, p => !launching && gamePlaceId > 0 && p is SavedLoginView { Running: false }, HandleError);
        RemoveLoginCommand = new(parameter =>
        {
            if (parameter is SavedLoginView view) { vault.Remove(view.UserId); ForgetBrowserProfile(view.UserId); RefreshLogins(); Message = $"{view.Name}'s saved login was removed."; Warning = false; }
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
            SavedLogins.Add(new(login.UserId, login.Username, running || starting, running ? "Running" : starting ? "Starting…" : "Closed"));
        }
        Raise(nameof(ClosedLoginCount)); Raise(nameof(HasClosedLogins)); Raise(nameof(OpenClosedLabel)); OpenClosedCommand?.Notify(); OpenLoginCommand?.Notify();
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

    private Task OpenOneAsync(object? parameter) =>
        parameter is SavedLoginView view && vault.Logins.FirstOrDefault(l => l.UserId == view.UserId) is { } login ? LaunchAsync([login]) : Task.CompletedTask;

    private async Task LaunchAsync(SavedLogin[] targets)
    {
        if (launching) return;
        if (gamePlaceId == 0) { Warning = true; Message = "Paste a game link first (Offline panel, Game)."; return; }
        if (targets.Length == 0) { Warning = false; Message = "Every saved account is already running or starting."; return; }
        launching = true; Raise(nameof(HasClosedLogins)); Raise(nameof(OpenClosedLabel)); OpenClosedCommand.Notify();
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
