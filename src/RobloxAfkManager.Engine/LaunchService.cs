using System.IO;
using System.Text.Json;
using RobloxAfkManager.Core;

namespace RobloxAfkManager.Engine;

// Where an account is sent. Stage: 0 = the account's own private server, 1 = the other private server, 2 = the public game.
public sealed record LaunchPlan(long PlaceId, string? LinkCode, int Server, int Stage = 0);

// Everything about starting accounts: the game and private servers they join, the fixed 6/6 split, the gap between launches,
// never starting an account twice, moving an account on when its private server is full, and Reset servers.
// Every way of starting an account (buttons and automatic fixes) goes through here, so they all follow the same rules.
public sealed class LaunchService
{
    public static readonly TimeSpan LaunchGrace = TimeSpan.FromMinutes(3);
    // Running, just started, or started a while ago and still loading (a private server join can queue for minutes).
    private static readonly TimeSpan LoadingLife = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ResolvedServerLife = TimeSpan.FromMinutes(30);
    private sealed record StoredServers(string? Server1, string? Server2, int FirstCount, Dictionary<long, int>? Assignments = null, long Place = 0);

    private readonly IEngineHost host;
    private readonly LoginVault vault;
    private readonly IAccountLauncher launcher;
    private readonly IAppLog log;
    private readonly string folder;

    public LaunchService(IEngineHost host, LoginVault vault, IAccountLauncher launcher, string settingsFolder, IAppLog log)
    {
        this.host = host; this.vault = vault; this.launcher = launcher; this.log = log; folder = settingsFolder;
        spacingSeconds = ReadSpacing(); gamePlaceId = ReadGame(); LoadServers();
    }

    public LoginVault Vault => vault;
    public IAccountLauncher Launcher => launcher;

    // ---- launch gap ------------------------------------------------------------------------------------------------
    // Seconds between starting one account and the next. Roblox rate-limits launches, so some people want longer.
    private string SpacingFile => Path.Combine(folder, "spacing.txt");
    private int spacingSeconds = 10;
    public TimeSpan LaunchSpacing => TimeSpan.FromSeconds(spacingSeconds);
    public int LaunchSpacingSeconds
    {
        get => spacingSeconds;
        set
        {
            int clamped = Math.Clamp(value, 5, 120);
            if (clamped == spacingSeconds) return;
            spacingSeconds = clamped;
            try { Directory.CreateDirectory(folder); File.WriteAllText(SpacingFile, clamped.ToString()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { host.SetStatus("Could not save the spacing: " + ex.Message, true); }
        }
    }
    private int ReadSpacing()
    {
        try { return File.Exists(SpacingFile) && int.TryParse(File.ReadAllText(SpacingFile).Trim(), out var seconds) ? Math.Clamp(seconds, 5, 120) : 10; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 10; }
    }

    // ---- the game ----------------------------------------------------------------------------------------------------
    // The game every saved account opens. A roblox.com/games/... link or a place id; remembered between runs.
    private string GameFile => Path.Combine(folder, "game.txt");
    private long gamePlaceId;
    public long GamePlaceId => gamePlaceId;
    public void SetGame(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) { gamePlaceId = 0; SaveGame(); }
        else if (ParsePlaceId(value) is { } id) { gamePlaceId = id; SaveGame(); host.SetStatus($"Accounts will open game {id}.", false); }
        else host.SetStatus("That isn't a Roblox game link or place id.", true);
        host.LaunchStateChanged();
    }
    public static long? ParsePlaceId(string? text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text ?? "", @"^\s*(?:https?://(?:www\.)?roblox\.com/(?:[a-z-]+/)?games/)?(\d{3,19})(?:[/?#].*)?\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
        return match.Success && long.TryParse(match.Groups[1].Value, out var id) && id > 0 ? id : null;
    }
    private long ReadGame()
    {
        try { return File.Exists(GameFile) && new FileInfo(GameFile).Length < 1024 ? ParsePlaceId(File.ReadAllText(GameFile)) ?? 0 : 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }
    private void SaveGame()
    {
        try { Directory.CreateDirectory(folder); File.WriteAllText(GameFile, gamePlaceId > 0 ? gamePlaceId.ToString() : ""); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { host.SetStatus("Could not save the game: " + ex.Message, true); }
    }

    // ---- private servers and the split ---------------------------------------------------------------------------------
    private string? serverCode1, serverCode2;
    private int firstServerCount = ServerAssignment.DefaultFirstCount;
    private Dictionary<long, int> serverAssignments = [];
    private readonly Dictionary<string, (PrivateServer Server, DateTimeOffset At)> resolvedServers = [];
    private long lastKnownPlace;
    private string ServersFile => Path.Combine(folder, "servers.json");

    // Something to open: a public game, or at least one private server.
    public bool HasLaunchTarget => gamePlaceId > 0 || serverCode1 is not null || serverCode2 is not null;
    public bool HasServer1 => serverCode1 is not null;
    public bool HasServer2 => serverCode2 is not null;
    public string PrivateServer1Link => LinkFor(serverCode1);
    public string PrivateServer2Link => LinkFor(serverCode2);
    private static string LinkFor(string? code) => code is null ? "" : $"https://www.roblox.com/share?code={code}&type=Server";
    public int AccountsOnServer1
    {
        get => firstServerCount;
        set { int clamped = Math.Clamp(value, 1, 128); if (clamped != firstServerCount) { firstServerCount = clamped; serverAssignments.Clear(); SaveServers(); } host.LaunchStateChanged(); }
    }
    public string ServerSummary => serverCode1 is null && serverCode2 is null ? "No private servers set: accounts open the game above on a public server."
        : serverCode1 is null ^ serverCode2 is null ? $"One private server set: every account joins private server {(serverCode1 is null ? 2 : 1)}."
        : $"The first {firstServerCount} accounts (in dashboard order) join private server 1, the rest join private server 2.";
    // The public game: the game link if one is set, otherwise the place the private servers belong to.
    public long FallbackPlace => gamePlaceId > 0 ? gamePlaceId : lastKnownPlace;

    public void SetServerLink(int number, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) { SetCode(number, null); host.SetStatus($"Private server {number} link removed.", false); }
        else if (PrivateServerLink.ParseCode(value) is { } code) { SetCode(number, code); host.SetStatus($"Private server {number} saved.", false); }
        else { host.SetStatus("That isn't a private server share link (it looks like roblox.com/share?code=…&type=Server).", true); host.LaunchStateChanged(); return; }
        resolvedServers.Clear(); SaveServers(); host.LaunchStateChanged();
    }
    private void SetCode(int number, string? code) { if (number == 1) serverCode1 = code; else serverCode2 = code; }

    private void LoadServers()
    {
        try
        {
            if (File.Exists(ServersFile) && new FileInfo(ServersFile).Length < 4096 &&
                JsonSerializer.Deserialize<StoredServers>(File.ReadAllText(ServersFile)) is { } stored)
            {
                serverCode1 = PrivateServerLink.ParseCode(stored.Server1); serverCode2 = PrivateServerLink.ParseCode(stored.Server2);
                firstServerCount = Math.Clamp(stored.FirstCount, 1, 128);
                serverAssignments = (stored.Assignments ?? []).Where(p => p.Value is 1 or 2).ToDictionary(p => p.Key, p => p.Value);
                lastKnownPlace = stored.Place > 0 ? stored.Place : 0;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }
    private void SaveServers()
    {
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(ServersFile, JsonSerializer.Serialize(new StoredServers(serverCode1, serverCode2, firstServerCount, serverAssignments, lastKnownPlace)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { host.SetStatus("Could not save the private servers: " + ex.Message, true); }
    }

    // Every saved account, in the order the dashboard shows them (running ones as they appear on screen, the rest by saved order).
    private List<long> DashboardOrder()
    {
        var saved = vault.Logins.Select(l => l.UserId).ToHashSet();
        var shown = host.Clients.Where(c => c.AccountId is { } id && saved.Contains(id)).Select(c => c.AccountId!.Value).Distinct().ToList();
        shown.AddRange(saved.Except(shown).OrderBy(id => host.ProfileOrder(id) ?? int.MaxValue).ThenBy(id => id));
        return shown;
    }

    // Gives every saved account its server (kept for good once an account is launched) and returns it. Without commit nothing is
    // remembered, so the list can show a preview before the first launch decides.
    public IReadOnlyDictionary<long, int> Assignments(bool commit)
    {
        var assigned = ServerAssignment.Assign(DashboardOrder(), firstServerCount, serverAssignments);
        if (commit && !assigned.OrderBy(p => p.Key).SequenceEqual(serverAssignments.OrderBy(p => p.Key)))
        { serverAssignments = new(assigned); SaveServers(); }
        return assigned;
    }

    // The server shown next to a saved login, or null when no private server is set.
    public int? ServerShownFor(long userId, IReadOnlyDictionary<long, int>? preview) =>
        serverCode1 is not null && serverCode2 is not null ? (preview ?? Assignments(commit: false)).GetValueOrDefault(userId, 2)
        : serverCode1 is not null ? 1 : serverCode2 is not null ? 2 : null;

    // Starts the split over from the dashboard order as it is now.
    public void Resplit()
    {
        serverAssignments.Clear(); Assignments(commit: true); host.LaunchStateChanged();
        var counts = serverAssignments.Values.GroupBy(v => v).ToDictionary(g => g.Key, g => g.Count());
        host.SetStatus($"Accounts split again: {counts.GetValueOrDefault(1)} on private server 1, {counts.GetValueOrDefault(2)} on private server 2.", false);
    }

    // 0 when no private server is configured (the public game is used).
    public int ServerNumberFor(long userId, bool commit = false)
    {
        if (serverCode1 is null && serverCode2 is null) return 0;
        if (serverCode1 is null) return 2;
        if (serverCode2 is null) return 1;
        return Assignments(commit).GetValueOrDefault(userId, 2);
    }

    // Where this account should go. Every launch starts at stage 0, the account's own private server, so a restart or reopen always tries
    // it first. Only the check for a client stuck in a full server asks for a later stage: the other private server, then the public game.
    // Asks Roblox to resolve a private server's share link the first time (and every 30 minutes).
    public async Task<LaunchPlan> PlanLaunchAsync(long userId, string cookie, int stage = 0)
    {
        int assigned = ServerNumberFor(userId, commit: true);
        if (assigned == 0)
        {
            if (gamePlaceId > 0) return new(gamePlaceId, null, 0, 2);
            throw new InvalidOperationException("Set a game or a private server link first (Settings → Accounts).");
        }
        Exception? failure = null;
        for (; stage < 2; stage++)
        {
            int server = stage == 0 ? assigned : 3 - assigned;
            string? code = server == 1 ? serverCode1 : serverCode2;
            if (code is null) continue;
            var now = DateTimeOffset.UtcNow;
            if (!resolvedServers.TryGetValue(code, out var known) || now - known.At > ResolvedServerLife)
            {
                try
                {
                    var resolved = await launcher.ResolveShareLinkAsync(cookie, code, CancellationToken.None);
                    resolvedServers[code] = known = (resolved, now);
                    if (lastKnownPlace != resolved.PlaceId) { lastKnownPlace = resolved.PlaceId; SaveServers(); }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failure = ex;
                    host.AddActivity(host.NameOf(userId), $"Could not use private server {server}: {ex.Message} Fix: trying the next option.", true, announce: true, quiet: true);
                    continue;
                }
            }
            return new(known.Server.PlaceId, known.Server.LinkCode, server, stage);
        }
        if (FallbackPlace > 0) return new(FallbackPlace, null, 0, 2);
        throw failure ?? new InvalidOperationException("No private server could be used. Set a game link in Settings → Accounts as a fallback.");
    }

    // ---- launching ----------------------------------------------------------------------------------------------------------
    private readonly Dictionary<long, DateTimeOffset> launchedAt = [];
    private readonly Dictionary<int, long> launchedPids = [];
    private readonly HashSet<long> launchedPrivate = [];
    private readonly Dictionary<long, int> launchedStage = []; // 0 own server, 1 other server, 2 public
    private bool launching;
    public bool Launching => launching;
    public DateTimeOffset? LaunchedAt(long userId) => launchedAt.TryGetValue(userId, out var at) ? at : null;
    // The account a client was started for, by process id (before its log has identified it).
    public long? LaunchedAccount(int processId) => launchedPids.TryGetValue(processId, out var id) ? id : null;
    public bool WasLaunchedByManager(int processId) => launchedPids.ContainsKey(processId);
    public bool IsStarting(long userId, DateTimeOffset now) => launchedAt.TryGetValue(userId, out var at) && now - at < LaunchGrace;

    // A second client for the same account would knock the first one off ("joined from another device"), so none is started.
    public bool IsActive(long userId, DateTimeOffset now) => host.RunningAccounts.Contains(userId)
        || (launchedAt.TryGetValue(userId, out var at) && (now - at < LaunchGrace || (now - at < LoadingLife && HasLoadingClient(userId))));
    private bool HasLoadingClient(long userId) =>
        launchedPids.Any(p => p.Value == userId && host.Clients.Any(c => c.ProcessId == p.Key && c.AccountId is null));

    private void Record(int pid, long account) { launchedPids[pid] = account; launchedAt[account] = DateTimeOffset.UtcNow; }

    // Two accounts that start at the same moment can get mixed up in the game (one is logged in as the other), so every
    // launch, from any button or automatic fix, waits until the previous one finished and the spacing has passed.
    // The account is checked again after the wait: it may have been started by something else in the meantime.
    private readonly SemaphoreSlim launchGate = new(1, 1);
    private DateTimeOffset lastLaunchEnd = DateTimeOffset.MinValue;
    private async Task<int> LaunchOneAsync(string cookie, LaunchPlan plan, string? ticket, long account, bool replacing = false)
    {
        await launchGate.WaitAsync();
        try
        {
            var wait = lastLaunchEnd + LaunchSpacing - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
            if (!replacing && IsActive(account, DateTimeOffset.UtcNow)) throw new InvalidOperationException("It is already running or still starting, so it was not started a second time.");
            int started = await launcher.LaunchSafelyAsync(cookie, plan.PlaceId, CancellationToken.None, ticket, plan.LinkCode);
            if (plan.LinkCode is not null) launchedPrivate.Add(account); else launchedPrivate.Remove(account);
            launchedStage[account] = plan.Stage;
            return started;
        }
        finally { lastLaunchEnd = DateTimeOffset.UtcNow; launchGate.Release(); }
    }

    // Closes an account's client (when given) and starts it again from its saved login. The login is checked and the server
    // resolved before anything is closed, so a dead login never costs a running client. Used by Restart and every automatic fix.
    public async Task<LaunchPlan> ReplaceAsync(long account, WindowIdentity? close, bool replacing = true, int stage = 0)
    {
        string cookie = vault.Cookie(account) ?? throw new InvalidOperationException("The saved login could not be read.");
        var plan = await PlanLaunchAsync(account, cookie, stage);
        string ticket = await launcher.PrepareAsync(cookie, plan.PlaceId, CancellationToken.None);
        if (close is { } identity)
        {
            var closed = await Task.Run(() => host.CloseClient(identity));
            if (!closed.Success) throw new InvalidOperationException(closed.Message);
        }
        int pid = await LaunchOneAsync(cookie, plan, ticket, account, replacing);
        Record(pid, account);
        return plan;
    }

    // Opens the accounts one at a time and reports what happened. The caller owns the "launching" flag.
    private async Task<(int Opened, List<string> Failures)> LaunchCoreAsync(SavedLogin[] targets, ISet<long>? replacing = null)
    {
        int opened = 0; var failures = new List<string>();
        foreach (var login in targets)
        {
            // Became active since the list was made (opened by something else): skipped at once, without waiting for the launch gap.
            if (replacing?.Contains(login.UserId) != true && IsActive(login.UserId, DateTimeOffset.UtcNow)) continue;
            host.SetStatus($"Opening {login.Username} ({opened + 1} of {targets.Length})…", false);
            try
            {
                string cookie = vault.Cookie(login.UserId) ?? throw new InvalidOperationException("The saved login could not be read.");
                var plan = await PlanLaunchAsync(login.UserId, cookie);
                int pid = await LaunchOneAsync(cookie, plan, null, login.UserId, replacing?.Contains(login.UserId) == true);
                Record(pid, login.UserId); opened++; host.LaunchStateChanged();
            }
            catch (Exception ex) { failures.Add($"{login.Username}: {ex.Message}"); log.Write($"Launching account {login.UserId} failed.", ex); }
        }
        return (opened, failures);
    }

    public async Task LaunchAsync(SavedLogin[] targets)
    {
        if (launching) return;
        if (!HasLaunchTarget) { host.SetStatus("Paste a game link or a private server link first (Settings → Accounts).", true); return; }
        if (targets.Length == 0) { host.SetStatus("Every saved account is already running or starting.", false); return; }
        launching = true; host.LaunchStateChanged();
        int opened; List<string> failures;
        try { (opened, failures) = await LaunchCoreAsync(targets); }
        finally { launching = false; host.LaunchStateChanged(); }
        host.SetStatus(failures.Count == 0 ? $"Started {opened} account(s). Use Arrange once their windows appear." : $"Started {opened}; {failures.Count} failed. {failures[0]}", failures.Count > 0);
    }

    // Rejoin all: every saved account that is really closed (windows that are not identified yet are read first).
    public async Task OpenClosedAsync()
    {
        if (launching) return;
        await host.SettleAccountsAsync();
        await LaunchAsync(vault.Logins.Where(l => !IsActive(l.UserId, DateTimeOffset.UtcNow)).ToArray());
    }

    // ---- full private server: other server, then public ------------------------------------------------------------------
    // An account sent to a private server that never gets in (full, waiting in line, link problem) is moved on instead of being
    // retried forever: first to the other private server, then to the game's public server. A restart or reopen later starts again
    // at the account's own private server. What happened is written to the activity log and the incident log.
    public TimeSpan PrivateJoinPatience { get; set; } = TimeSpan.FromMinutes(2);
    private bool fallingBack;

    public async Task FallbackStuckPrivateJoinsAsync()
    {
        if (fallingBack || launching || host.OtherFixRunning || host.Busy || host.Disposed || launchedPrivate.Count == 0 || FallbackPlace == 0) return;
        var now = DateTimeOffset.UtcNow;
        (long Id, IClientView Tile)? stuck = null;
        foreach (var id in launchedPrivate.ToArray())
        {
            if (host.RunningAccounts.Contains(id) || !launchedAt.TryGetValue(id, out var at)) { launchedPrivate.Remove(id); continue; } // joined
            var tile = host.Clients.FirstOrDefault(c => c.AccountId is null && launchedPids.Any(p => p.Value == id && p.Key == c.ProcessId));
            if (tile is null) { if (now - at > TimeSpan.FromMinutes(2)) launchedPrivate.Remove(id); continue; } // the window is gone: reopen handles that
            if (now - at >= PrivateJoinPatience) { stuck = (id, tile); break; }
        }
        if (stuck is not { } s) return;

        fallingBack = true;
        string label = host.NameOf(s.Id);
        int own = ServerNumberFor(s.Id);
        int stage = launchedStage.GetValueOrDefault(s.Id) + 1;
        int launchedServer = stage == 1 ? own : 3 - own;
        launchedPrivate.Remove(s.Id);
        try
        {
            var plan = await ReplaceAsync(s.Id, s.Tile.Identity, replacing: true, stage);
            string waited = $"Could not get into private server {launchedServer} within {(int)Math.Ceiling(PrivateJoinPatience.TotalMinutes)} minutes (still loading or waiting in line, so it is probably full or its link has a problem).";
            string moved = plan.Server > 0 ? $"Fix: closed it and joined private server {plan.Server} instead. If that one is full too it joins the public server." : "Fix: closed it and joined the public server of the game instead.";
            host.AddActivity(label, $"{waited} {moved} A restart tries its own private server first again.", true, quiet: true);
            host.SetStatus($"{label} could not get into private server {launchedServer}; " + (plan.Server > 0 ? $"it joined private server {plan.Server}." : "it joined a public server."), true);
        }
        catch (Exception ex)
        {
            log.Write($"Server fallback for account {s.Id} failed.", ex);
            host.AddActivity(label, $"Could not get into private server {launchedServer} and moving the account on failed too: {ex.Message}", true);
            host.SetStatus($"{label}: {ex.Message}", true);
        }
        finally { fallingBack = false; host.LaunchStateChanged(); }
    }

    // ---- Reset servers ------------------------------------------------------------------------------------------------------
    // Closes every running account that can be reopened, waits so the private servers empty and reset, then opens every saved
    // account that is not running.
    public TimeSpan ResetDelay { get; set; } = TimeSpan.FromMinutes(1);
    // Asks "close N accounts and reset the servers?" before anything is closed. Not set in tests, which proceed.
    public Func<int, bool>? ConfirmReset { get; set; }
    private readonly HashSet<long> reconnectingIds = [];
    private readonly HashSet<string> reconnectingLabels = [];
    // Accounts that Reset servers closed on purpose: no alerts and no automatic reopen for them meanwhile.
    public bool IsReconnecting(long userId) => reconnectingIds.Contains(userId);
    public bool IsReconnecting(string label) => reconnectingLabels.Contains(label);

    public bool CanResetServers(bool disposed) => !disposed && vault.Logins.Count > 0;

    public async Task ResetServersAsync()
    {
        if (host.Disposed) return;
        if (launching || host.OtherFixRunning) { host.SetStatus("Accounts are being opened or restarted right now. Wait until that has finished, then press Reset servers again.", true); return; }
        if (!HasLaunchTarget) { host.SetStatus("Paste a game link or a private server link first (Settings → Accounts).", true); return; }
        await host.SettleAccountsAsync();
        var saved = vault.Logins.Select(l => l.UserId).ToHashSet();
        var candidates = host.Clients.Where(c => !c.IsIgnored && c.AccountId is { } id && saved.Contains(id))
            .Select(c => (Id: c.AccountId!.Value, c.Label, c.Identity)).ToArray();
        if (candidates.Length == 0) { host.SetStatus("Reset servers did nothing: no running account with a saved login was found.", true); return; }
        if (ConfirmReset is { } ask && !ask(candidates.Length)) return;
        launching = true; host.LaunchStateChanged();
        var targets = new List<(long Id, string Label, WindowIdentity Identity)>(); var skipped = new List<string>();
        try
        {
            // An account is only closed if it can certainly be reopened: its login works and its server link resolves.
            foreach (var c in candidates)
            {
                try
                {
                    string cookie = vault.Cookie(c.Id) ?? throw new InvalidOperationException("The saved login could not be read.");
                    await PlanLaunchAsync(c.Id, cookie);
                    targets.Add(c);
                }
                catch (Exception ex) { skipped.Add($"{c.Label}: {ex.Message}"); log.Write($"Reset servers skipped account {c.Id}.", ex); }
            }
            if (targets.Count == 0)
            {
                host.SetStatus("Reset servers did nothing: " + (skipped.Count > 0 ? skipped[0] : "no running account has a saved login."), true);
                if (skipped.Count > 0) host.AddActivity("Reset servers", "Cancelled, nothing was closed. " + skipped[0], true, announce: false);
                return;
            }

            reconnectingIds.UnionWith(targets.Select(t => t.Id)); reconnectingLabels.UnionWith(targets.Select(t => t.Label));
            var closedIds = new HashSet<long>(); var failures = new List<string>();
            foreach (var t in targets)
            {
                host.SetStatus($"Reset servers: closing {t.Label}…", false);
                var closed = await Task.Run(() => host.CloseClient(t.Identity));
                if (closed.Success) closedIds.Add(t.Id); else failures.Add($"{t.Label} could not be closed: {closed.Message}");
            }
            // A private server only shuts down and starts fresh when everyone has left it, so anyone still inside defeats the wait.
            int stillOpen = host.Clients.Count - closedIds.Count;
            string staying = stillOpen > 0 ? $" {stillOpen} other client(s) stay open (ignored, no saved login or could not be closed): a private server with someone still in it will not reset." : "";
            host.AddActivity("Reset servers", $"Closed {closedIds.Count} accounts. Once everyone has left, the private servers reset; the accounts rejoin in {(int)Math.Ceiling(ResetDelay.TotalSeconds)} seconds." + (skipped.Count > 0 ? $" Left alone ({skipped.Count}): {string.Join("; ", skipped.Take(3))}." : "") + staying, stillOpen > 0, announce: false);

            var until = DateTimeOffset.UtcNow + ResetDelay;
            while (DateTimeOffset.UtcNow < until && !host.Disposed)
            {
                var left = until - DateTimeOffset.UtcNow;
                host.SetStatus($"Reset servers: {closedIds.Count} accounts closed, rejoining in {Math.Ceiling(left.TotalSeconds):0}s…", false);
                await Task.Delay(left < TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1));
            }
            if (host.Disposed) return;

            // The accounts just closed, plus any other saved account that is not running, rejoin one at a time.
            var now = DateTimeOffset.UtcNow;
            var toOpen = vault.Logins.Where(l => closedIds.Contains(l.UserId) || !IsActive(l.UserId, now)).ToArray();
            var (opened, openFailures) = await LaunchCoreAsync(toOpen, closedIds);
            failures.AddRange(openFailures);
            string summary = failures.Count == 0 ? $"Servers reset. {opened} accounts rejoined." : $"Servers reset. {opened} rejoined; {failures.Count} problem(s): {failures[0]}";
            host.AddActivity("Reset servers", summary, failures.Count > 0);
            host.SetStatus(summary, failures.Count > 0);
        }
        finally
        {
            reconnectingIds.Clear(); reconnectingLabels.Clear(); launching = false; host.LaunchStateChanged();
        }
    }
}
