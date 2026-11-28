using RobloxAfkManager.Core;

namespace RobloxAfkManager.App.Services;

// Builds the engine for this app: the Windows implementations for real use, or the fakes a test passes in. A test host (any
// window system other than Win32) never touches the person's real settings files, logins log or Discord webhook.
public static class EngineFactory
{
    public sealed record Parts(AfkEngine Engine, IWindowSystem Windows, IAppLog Log);

    public static Parts Create(IUserPrompts prompts, IWindowSystem? windowSystem = null, IAppLog? appLog = null, IProcessResourceSystem? resourceSystem = null,
        IWorkingSetSystem? workingSetSystem = null, IAccountNameLookup? accountNameLookup = null, IClientNameStore? clientNameStore = null,
        IWorkspaceStore? workspaceStore = null, IAccountLauncher? accountLauncher = null, LoginVault? loginVault = null, string? settingsFolder = null,
        DiscordWebhook? discordWebhook = null, IUserIdle? userIdle = null)
    {
        var log = appLog ?? new FileLog();
        var windows = windowSystem ?? new Win32WindowSystem(log);
        bool real = windows is Win32WindowSystem;
        var system = new Win32ProcessResources();
        var engine = new AfkEngine(new EngineOptions
        {
            Windows = windows, Log = log,
            Resources = resourceSystem ?? system, WorkingSet = workingSetSystem ?? system,
            AccountLookup = accountNameLookup ?? (real ? new LogAccountNameLookup(owners: LogFileOwners.Read, stateFile: System.IO.Path.Combine(AppFolders.Data, "process-accounts.txt")) : null),
            ClientNames = clientNameStore ?? (real ? new ClientNameStore() : new MemoryClientNameStore()),
            Workspace = workspaceStore ?? (real ? new WorkspaceStore() : new MemoryWorkspaceStore()),
            Launcher = accountLauncher ?? new AccountLauncher(new WindowsRobloxClients()),
            Vault = loginVault ?? new LoginVault(),
            SettingsFolder = settingsFolder ?? AppFolders.Data,
            Webhook = discordWebhook ?? (real ? new DiscordWebhook(log: log) : new DiscordWebhook(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rafk-no-webhook"), log: log)),
            Idle = userIdle ?? (real ? new Win32UserIdle() : new NeverIdle()),
            Prompts = prompts,
        });
        return new(engine, windows, log);
    }

    // Refreshes as soon as Roblox windows open, close or move (real Windows only).
    public static WindowMonitor? WatchWindows(IWindowSystem windows, IAppLog log, Action changed) =>
        windows is Win32WindowSystem native ? new WindowMonitor(native, log, changed) : null;
}
