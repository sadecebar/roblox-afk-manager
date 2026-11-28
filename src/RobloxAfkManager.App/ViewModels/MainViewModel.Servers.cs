using RobloxAfkManager.App.Services;
using RobloxAfkManager.Core;

namespace RobloxAfkManager.App.ViewModels;

// Two private servers: the first N accounts (in dashboard order) join server 1, the rest join server 2. Every way of
// starting an account goes through PlanLaunchAsync, so opening, restarting and reopening all land on the right server.
// Reconnect all closes every running account that can be reopened, waits a minute, then starts them again.
public sealed partial class MainViewModel
{
    private sealed record StoredServers(string? Server1, string? Server2, int FirstCount);
    private sealed record LaunchPlan(long PlaceId, string? LinkCode, int Server);
    private static readonly TimeSpan ResolvedServerLife = TimeSpan.FromMinutes(30);

    private string? serverCode1, serverCode2;
    private int firstServerCount = ServerAssignment.DefaultFirstCount;
    private readonly Dictionary<string, (PrivateServer Server, DateTimeOffset At)> resolvedServers = [];
    private readonly HashSet<long> reconnectingIds = [];
    private readonly HashSet<string> reconnectingLabels = [];
    private string ServersFile => System.IO.Path.Combine(settingsFolder, "servers.json");

    // How long Reconnect all keeps the accounts closed before starting them again.
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromMinutes(1);
    public AsyncCommand ReconnectAllCommand { get; private set; } = null!;
    // The window asks "close N accounts and reconnect them?" before anything is closed. Not set in tests, which proceed.
    public Func<int, bool>? ConfirmReconnect { get; set; }

    // Something to open: a public game, or at least one private server.
    public bool HasLaunchTarget => gamePlaceId > 0 || serverCode1 is not null || serverCode2 is not null;

    public string PrivateServer1Link { get => LinkFor(serverCode1); set => SetServerLink(ref serverCode1, value, 1); }
    public string PrivateServer2Link { get => LinkFor(serverCode2); set => SetServerLink(ref serverCode2, value, 2); }
    private static string LinkFor(string? code) => code is null ? "" : $"https://www.roblox.com/share?code={code}&type=Server";
    public int AccountsOnServer1
    {
        get => firstServerCount;
        set { int clamped = Math.Clamp(value, 1, 128); if (clamped != firstServerCount) { firstServerCount = clamped; SaveServers(); } RaiseServers(); RefreshLogins(); }
    }
    public string ServerSummary => serverCode1 is null && serverCode2 is null ? "No private servers set: accounts open the game above on a public server."
        : serverCode1 is null ^ serverCode2 is null ? $"One private server set: every account joins private server {(serverCode1 is null ? 2 : 1)}."
        : $"The first {firstServerCount} accounts (in dashboard order) join private server 1, the rest join private server 2.";

    private void SetServerLink(ref string? field, string? value, int number)
    {
        if (string.IsNullOrWhiteSpace(value)) { field = null; Warning = false; Message = $"Private server {number} link removed."; }
        else if (PrivateServerLink.ParseCode(value) is { } code) { field = code; Warning = false; Message = $"Private server {number} saved."; }
        else { Warning = true; Message = "That isn't a private server share link (it looks like roblox.com/share?code=…&type=Server)."; RaiseServers(); return; }
        resolvedServers.Clear(); SaveServers(); RaiseServers(); RefreshLogins();
    }

    private void InitializeServers()
    {
        try
        {
            if (System.IO.File.Exists(ServersFile) && new System.IO.FileInfo(ServersFile).Length < 4096 &&
                System.Text.Json.JsonSerializer.Deserialize<StoredServers>(System.IO.File.ReadAllText(ServersFile)) is { } stored)
            {
                serverCode1 = PrivateServerLink.ParseCode(stored.Server1); serverCode2 = PrivateServerLink.ParseCode(stored.Server2);
                firstServerCount = Math.Clamp(stored.FirstCount, 1, 128);
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        ReconnectAllCommand = new(ReconnectAllAsync, CanReconnectAll, HandleError);
    }
    private void SaveServers()
    {
        try
        {
            System.IO.Directory.CreateDirectory(settingsFolder);
            System.IO.File.WriteAllText(ServersFile, System.Text.Json.JsonSerializer.Serialize(new StoredServers(serverCode1, serverCode2, firstServerCount)));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { Warning = true; Message = "Could not save the private servers: " + ex.Message; }
    }
    private void RaiseServers()
    {
        Raise(nameof(PrivateServer1Link)); Raise(nameof(PrivateServer2Link)); Raise(nameof(AccountsOnServer1)); Raise(nameof(ServerSummary)); Raise(nameof(HasLaunchTarget));
        Raise(nameof(HasClosedLogins)); Raise(nameof(OpenClosedLabel)); OpenClosedCommand?.Notify(); OpenSelectedCommand?.Notify(); OpenLoginCommand?.Notify(); ReconnectAllCommand?.Notify();
    }

    // 1-based place of every saved account in the dashboard order. Does not depend on which accounts are running.
    private IReadOnlyDictionary<long, int> ServerPositions() =>
        ServerAssignment.Positions(vault.Logins.Select(l => l.UserId), profiles.ToDictionary(p => p.Key, p => p.Value.Order));

    // 0 when no private server is configured (the public game is used).
    private int ServerNumberFor(long userId, IReadOnlyDictionary<long, int>? positions = null)
    {
        if (serverCode1 is null && serverCode2 is null) return 0;
        if (serverCode1 is null) return 2;
        if (serverCode2 is null) return 1;
        return ServerAssignment.ServerFor((positions ?? ServerPositions()).GetValueOrDefault(userId, int.MaxValue), firstServerCount);
    }

    // Where this account should go. Asks Roblox to resolve a private server's share link the first time (and every 30 minutes).
    private async Task<LaunchPlan> PlanLaunchAsync(long userId, string cookie)
    {
        int server = ServerNumberFor(userId);
        if (server == 0)
        {
            if (gamePlaceId > 0) return new(gamePlaceId, null, 0);
            throw new InvalidOperationException("Set a game or a private server link first (Settings → Accounts).");
        }
        string code = (server == 1 ? serverCode1 : serverCode2)!;
        var now = DateTimeOffset.UtcNow;
        if (!resolvedServers.TryGetValue(code, out var known) || now - known.At > ResolvedServerLife)
        {
            var resolved = await launcher.ResolveShareLinkAsync(cookie, code, CancellationToken.None);
            resolvedServers[code] = known = (resolved, now);
        }
        return new(known.Server.PlaceId, known.Server.LinkCode, server);
    }

    // Two accounts that start at the same moment can get mixed up in the game (one is logged in as the other), so every
    // launch, from any button or automatic fix, waits until the previous one finished and the spacing has passed.
    private readonly SemaphoreSlim launchGate = new(1, 1);
    private DateTimeOffset lastLaunchEnd = DateTimeOffset.MinValue;
    private async Task<int> LaunchOneAsync(string cookie, LaunchPlan plan, string? ticket)
    {
        await launchGate.WaitAsync();
        try
        {
            var wait = lastLaunchEnd + LaunchSpacing - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
            return await launcher.LaunchSafelyAsync(cookie, plan.PlaceId, CancellationToken.None, ticket, plan.LinkCode);
        }
        finally { lastLaunchEnd = DateTimeOffset.UtcNow; launchGate.Release(); }
    }

    private bool CanReconnectAll() => HasLaunchTarget && !launching && !recovering && !reopening && !Busy && !disposed
        && Clients.Any(c => !c.IsIgnored && c.AccountId is { } id && vault.Logins.Any(l => l.UserId == id));

    private async Task ReconnectAllAsync()
    {
        if (!CanReconnectAll()) return;
        var saved = vault.Logins.Select(l => l.UserId).ToHashSet();
        var candidates = Clients.Where(c => !c.IsIgnored && c.AccountId is { } id && saved.Contains(id))
            .Select(c => (Id: c.AccountId!.Value, c.Label, c.Identity)).ToArray();
        if (ConfirmReconnect is { } ask && !ask(candidates.Length)) return;
        launching = true; RaiseServers();
        var targets = new List<(long Id, string Label, WindowIdentity Identity)>(); var skipped = new List<string>();
        int opened = 0; var failures = new List<string>();
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
                catch (Exception ex) { skipped.Add($"{c.Label}: {ex.Message}"); log.Write($"Reconnect all skipped account {c.Id}.", ex); }
            }
            if (targets.Count == 0)
            {
                Warning = true; Message = "Reconnect all did nothing: " + (skipped.Count > 0 ? skipped[0] : "no running account has a saved login.");
                if (skipped.Count > 0) AddActivity("Reconnect all", "Cancelled, nothing was closed. " + skipped[0], true, announce: false);
                return;
            }

            reconnectingIds.UnionWith(targets.Select(t => t.Id)); reconnectingLabels.UnionWith(targets.Select(t => t.Label));
            var closedOnes = new List<(long Id, string Label, WindowIdentity Identity)>();
            foreach (var t in targets)
            {
                Message = $"Reconnect all: closing {t.Label}…"; Warning = false;
                var closed = await Task.Run(() => manager.CloseClient(t.Identity));
                if (closed.Success) closedOnes.Add(t); else failures.Add($"{t.Label} could not be closed: {closed.Message}");
            }
            // A private server only shuts down and starts fresh when everyone has left it, so anyone still inside defeats the wait.
            int stillOpen = Clients.Count - closedOnes.Count;
            string staying = stillOpen > 0 ? $" {stillOpen} other client(s) stay open (ignored, no saved login or could not be closed): a private server with someone still in it will not reset." : "";
            AddActivity("Reconnect all", $"Closed {closedOnes.Count} accounts. Once everyone has left, the private servers reset; they reconnect in {(int)Math.Ceiling(ReconnectDelay.TotalSeconds)} seconds." + (skipped.Count > 0 ? $" Left alone ({skipped.Count}): {string.Join("; ", skipped.Take(3))}." : "") + staying, stillOpen > 0, announce: false);

            var until = DateTimeOffset.UtcNow + ReconnectDelay;
            while (DateTimeOffset.UtcNow < until && !disposed)
            {
                var left = until - DateTimeOffset.UtcNow;
                Message = $"Reconnect all: {closedOnes.Count} accounts closed, reconnecting in {Math.Ceiling(left.TotalSeconds):0}s…";
                await Task.Delay(left < TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1));
            }
            if (disposed) return;

            foreach (var t in closedOnes)
            {
                Message = $"Reconnect all: opening {t.Label} ({opened + 1} of {closedOnes.Count})…";
                try
                {
                    string cookie = vault.Cookie(t.Id) ?? throw new InvalidOperationException("The saved login could not be read.");
                    var plan = await PlanLaunchAsync(t.Id, cookie);
                    string ticket = await launcher.PrepareAsync(cookie, plan.PlaceId, CancellationToken.None);
                    int pid = await LaunchOneAsync(cookie, plan, ticket);
                    launchedPids[pid] = t.Id; launchedAt[t.Id] = DateTimeOffset.UtcNow; opened++; RefreshLogins();
                }
                catch (Exception ex) { failures.Add($"{t.Label}: {ex.Message}"); log.Write($"Reconnecting account {t.Id} failed.", ex); }
            }
            string summary = failures.Count == 0 ? $"Reconnected {opened} accounts." : $"Reconnected {opened}; {failures.Count} failed: {failures[0]}";
            AddActivity("Reconnect all", summary, failures.Count > 0);
            Warning = failures.Count > 0; Message = summary;
        }
        finally
        {
            reconnectingIds.Clear(); reconnectingLabels.Clear(); launching = false; RefreshLogins(); RaiseServers();
        }
    }
}
