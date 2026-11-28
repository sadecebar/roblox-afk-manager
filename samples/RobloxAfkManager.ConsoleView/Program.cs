using RobloxAfkManager.Core;
using RobloxAfkManager.Engine;
using RobloxAfkManager.Platform.Windows;

// A second, throwaway front-end for the same engine: a live text dashboard in a console window. It exists to prove that the
// engine works without the WPF app (docs/ARCHITECTURE-PLAN.md, M6), using only the contract in docs/ENGINE.md.
//
// Observe only, so it is safe to run next to the real manager: it reads the real accounts, profiles and private-server split
// from a temporary copy, never writes the real settings, never posts to Discord, never moves windows, changes process
// priority or launches anything (Auto-reopen and the other automatic fixes are off).
//
//   RobloxAfkManager.ConsoleView           refresh every 5 seconds until Q or Ctrl+C
//   RobloxAfkManager.ConsoleView --once    print one snapshot and exit

bool once = args.Contains("--once");
WindowsPlatform.Install();
var log = new ConsoleLog();

// A private copy of the settings the view needs to show the same split and game; nothing that would make it act.
string real = AppFolders.Data;
string copy = Path.Combine(Path.GetTempPath(), "rafk-console-" + Environment.ProcessId);
Directory.CreateDirectory(copy);
foreach (var name in new[] { "servers.json", "game.txt", "away.txt", "spacing.txt", "process-accounts.txt" })
    if (File.Exists(Path.Combine(real, name))) File.Copy(Path.Combine(real, name), Path.Combine(copy, name), true);
var saved = new WorkspaceStore().Read();
var workspace = new MemoryWorkspaceStore();
// Saved window positions and resource plans are dropped: this view must never move a window or change a process.
workspace.Write(saved with { Profiles = saved.Profiles.Select(p => p with { Window = null, PreferredPlan = null }).ToArray() });

var windows = new Win32WindowSystem(log);
var observeOnly = new ObserveOnly();
using var engine = new AfkEngine(new EngineOptions
{
    Windows = windows, Log = log,
    Resources = observeOnly, WorkingSet = observeOnly,
    AccountLookup = new LogAccountNameLookup(owners: LogFileOwners.Read, stateFile: Path.Combine(copy, "process-accounts.txt")),
    ClientNames = new ClientNameStore(), Workspace = workspace,
    Launcher = observeOnly, Vault = new LoginVault(), SettingsFolder = copy,
    Webhook = new DiscordWebhook(copy, log: log), Idle = new Win32UserIdle(),
});

var events = new List<string>();
engine.StatusChanged += (message, warning) => { if (message is not null) events.Add((warning ? "! " : "  ") + message); };
engine.Alerts.ActivityAdded += (entry, _) => events.Add((entry.Warning ? "! " : "  ") + $"{entry.Label}: {entry.Message}");

try
{
    do
    {
        await engine.RefreshAsync();
        // Reading which account is in which window takes a few seconds; the first view waits for it.
        if (once || engine.Clients.Any(c => c.AccountId is null)) await engine.SettleAccountsAsync();
        for (int i = 0; i < 600 && engine.ResolvingNames; i++) await Task.Delay(100);
        Draw(engine, events, once);
        if (once) break;
        for (int i = 0; i < 50; i++)
        {
            if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Q) return;
            await Task.Delay(100);
        }
    }
    while (true);
}
finally { try { Directory.Delete(copy, true); } catch (IOException) { } }

static void Draw(AfkEngine engine, List<string> events, bool once)
{
    var now = DateTimeOffset.UtcNow;
    if (!once) Console.Clear();
    var shown = engine.Clients.Where(engine.CanManage).ToArray();
    int joined = shown.Count(c => c.StatusAt(now).Tone == ClientTone.Good);
    int attention = shown.Count(c => c.StatusAt(now).NeedsAttention);
    var offline = engine.Workspace.Offline(now);
    Console.WriteLine($"Roblox AFK Manager · console view (observe only)   {DateTime.Now:HH:mm:ss}");
    Console.WriteLine($"{shown.Length} on watch · {joined} joined · {attention} need attention · {offline.Count} offline · {(engine.Alerts.IsAway ? "away" : "at PC")}");
    Console.WriteLine();
    Console.WriteLine($"{"#",-3} {"Account",-22} {"Status",-24} {"Joined for",-11} {"Server",-7} Game");
    var preview = engine.Launch.HasServer1 && engine.Launch.HasServer2 ? engine.Launch.Assignments(commit: false) : null;
    foreach (var client in shown)
    {
        var status = client.StatusAt(now);
        string state = status.Window switch { ClientWindowState.Minimized => "Minimized", ClientWindowState.OtherDesktop => "Other desktop", _ => status.Session.ToString() };
        string since = client.Session is { Phase: SessionPhase.Joined, JoinedAt: { } at } ? Duration(now - at) : "";
        string server = client.AccountId is { } id && engine.Launch.ServerShownFor(id, preview) is { } n ? n.ToString() : "-";
        Console.ForegroundColor = status.Tone switch { ClientTone.Good => ConsoleColor.Green, ClientTone.Pending => ConsoleColor.Cyan, ClientTone.Warn => ConsoleColor.Yellow, ClientTone.Alert => ConsoleColor.Red, _ => ConsoleColor.Gray };
        Console.WriteLine($"{client.Slot,-3:00} {Cut(client.Label, 22),-22} {state,-24} {since,-11} {server,-7} {Cut(client.KnownGameName ?? "", 30)}");
        Console.ResetColor();
    }
    if (offline.Count > 0)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        foreach (var account in offline) Console.WriteLine($"    offline: {account.Name}, last seen {AccountRoster.Ago(account.LastSeen, now)}");
        Console.ResetColor();
    }
    Console.WriteLine();
    foreach (var line in events.TakeLast(6)) Console.WriteLine(line);
    if (!once) Console.WriteLine("\nRefreshing every 5 seconds. Q quits.");
}
static string Duration(TimeSpan span) => span.TotalMinutes < 1 ? "<1m" : span.TotalHours < 1 ? $"{(int)span.TotalMinutes}m" : $"{(int)span.TotalHours}h {span.Minutes:00}m";
static string Cut(string text, int width) => text.Length <= width ? text : text[..(width - 1)] + "…";

// Everything that would change something is refused: resource plans, memory limits and launching.
sealed class ObserveOnly : IProcessResourceSystem, IWorkingSetSystem, IAccountLauncher
{
    private static InvalidOperationException Refused() => new("The console view only observes.");
    public IProcessResourceHandle Open(ProcessIdentity process) => throw Refused();
    public IWorkingSetHandle OpenWorkingSet(ProcessIdentity process) => throw Refused();
    public bool IsAlive(ProcessIdentity process) => true;
    public Task<SavedLogin> IdentifyAsync(string cookie, CancellationToken token) => throw Refused();
    public Task<string> PrepareAsync(string cookie, long placeId, CancellationToken token) => throw Refused();
    public Task<int> LaunchSafelyAsync(string cookie, long placeId, CancellationToken token, string? readyTicket = null, string? linkCode = null) => throw Refused();
    public Task<PrivateServer> ResolveShareLinkAsync(string cookie, string shareCode, CancellationToken token) => throw Refused();
}

// Messages are dropped: nothing is written to the manager's own log file.
sealed class ConsoleLog : IAppLog
{
    public void Write(string message, Exception? exception = null) { }
}
