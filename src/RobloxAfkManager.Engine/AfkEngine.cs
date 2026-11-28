using RobloxAfkManager.Core;

namespace RobloxAfkManager.Engine;

// Everything a window needs to run the manager. The engine owns the list of clients and all behaviour; a window reads its state,
// listens to its events and calls its methods. See docs/ENGINE.md. Windows calls come in through the interfaces in EngineOptions.
public sealed class EngineOptions
{
    public required IWindowSystem Windows { get; init; }
    public required IAppLog Log { get; init; }
    public required IProcessResourceSystem Resources { get; init; }
    public required IWorkingSetSystem WorkingSet { get; init; }
    public IAccountNameLookup? AccountLookup { get; init; }
    public required IClientNameStore ClientNames { get; init; }
    public required IWorkspaceStore Workspace { get; init; }
    public required IAccountLauncher Launcher { get; init; }
    public required LoginVault Vault { get; init; }
    public required string SettingsFolder { get; init; }
    public required DiscordWebhook Webhook { get; init; }
    public required IUserIdle Idle { get; init; }
    public IUserPrompts? Prompts { get; init; }
}

public sealed class AfkEngine : IEngineHost, IDisposable
{
    private readonly IWindowSystem windows;
    private readonly WindowManager manager;
    private readonly ResourceProfileManager resources;
    private readonly WorkingSetCapManager memoryCaps;
    private readonly IAppLog log;
    private readonly IAccountNameLookup? accountNames;
    private readonly IClientNameStore clientNames;
    private readonly SessionHealthTracker health = new();
    private readonly CancellationTokenSource nameCancellation = new();
    private readonly List<EngineClient> clients = [];
    private readonly HashSet<ProcessIdentity> restoredProfiles = [], editedNames = [], editedIgnores = [];
    private IReadOnlyList<MonitorInfo> monitors = [];
    private int nextSlot;
    private bool busy, disposed, resolvingNames, restoring, manualOrderChanged, allowUnidentified;
    private DateTimeOffset nextNameRefresh;

    public AfkEngine(EngineOptions options)
    {
        windows = options.Windows; log = options.Log;
        manager = new(windows, log);
        resources = new(options.Resources, log);
        memoryCaps = new(options.WorkingSet, log);
        accountNames = options.AccountLookup;
        clientNames = options.ClientNames;
        Workspace = new WorkspaceService(options.Workspace, log);
        Launch = new LaunchService(this, options.Vault, options.Launcher, options.SettingsFolder, log) { Prompts = options.Prompts };
        Health = new HealthService(this, Launch, options.SettingsFolder, log);
        Alerts = new AlertService(this, options.Webhook, options.Idle, Launch, Health, options.SettingsFolder);
        Placements = new PlacementService(this, Workspace, manager, Launch);
    }

    // Start-up work that talks to the outside world (crash marker, "manager started" alert). Call once the window is listening.
    public void Start() { Alerts.WatchForCrashes(); Alerts.AnnounceStarted(); }

    public LaunchService Launch { get; }
    public HealthService Health { get; }
    public AlertService Alerts { get; }
    public WorkspaceService Workspace { get; }
    public PlacementService Placements { get; }

    // ---- state --------------------------------------------------------------------------------------------------------------------------
    // Every detected client, in dashboard order.
    public IReadOnlyList<EngineClient> Clients => clients;
    public IReadOnlyList<MonitorInfo> Monitors => monitors;
    public DateTimeOffset? DetectedAt { get; private set; }
    // Refreshing or running a window action; other actions wait.
    public bool Busy => busy;
    public bool Disposed => disposed;
    public bool ResolvingNames => resolvingNames;
    // True while saved names and ignore choices are being put back on identified clients.
    public bool Restoring => restoring;
    // Number of windows whose original position and size are saved (Restore originals).
    public int SavedWindowCount => manager.OriginalCount;
    public bool CanRestoreGrid => manager.CanRestoreGrid;
    public GridPlan? LastGrid => manager.LastGrid;
    public bool IsCurrent(WindowIdentity identity) => windows.IsCurrent(identity);

    // Saved ignore choices can only be restored once a client's account is known, so until then unknown clients stay hidden.
    public bool RequiresIdentification => accountNames is IAccountDetailsLookup && Workspace.Profiles.Values.Any(p => p.Ignored);
    public bool AllowUnidentifiedClients { get => allowUnidentified; set { if (allowUnidentified == value) return; allowUnidentified = value; StateChanged?.Invoke(); } }
    // Shown on the dashboard and included in bulk actions.
    public bool CanManage(EngineClient client) => !client.IsIgnored && (!RequiresIdentification || client.AccountId is not null || allowUnidentified);

    public ResourcePlan? ActivePlan => resources.ActivePlan;
    public int PlanAppliedCount => resources.AppliedCount;
    public int PlanSavedCount => resources.SavedCount;
    public int? MemoryCapMiB => memoryCaps.ActiveMiB;
    public int MemoryCapAppliedCount => memoryCaps.AppliedCount;
    public int MemoryCapSavedCount => memoryCaps.SavedCount;
    public bool HasResourceChanges => resources.ActivePlan is not null || resources.SavedCount > 0 || memoryCaps.ActiveMiB is not null || memoryCaps.SavedCount > 0;

    // ---- events -------------------------------------------------------------------------------------------------------------------------
    // The client list, its order, the monitors or resource state changed.
    public event Action? StateChanged;
    // A message for the status bar. message null keeps the current text and only changes the warning flag.
    public event Action<string?, bool>? StatusChanged;
    public event Action? BusyChanged;
    // Saved logins, servers or launch progress changed.
    public event Action? LaunchStateChanged;

    private void SetBusy(bool value) { if (busy == value) return; busy = value; BusyChanged?.Invoke(); }
    private void Status(string? message, bool warning) => StatusChanged?.Invoke(message, warning);
    private bool lastWarning;
    private void Report(OperationResult result) { lastWarning = !result.Success; Status(result.Message, !result.Success); }
    private void Fail(Exception ex) { log.Write("Dashboard operation failed.", ex); lastWarning = true; Status(ex.Message, true); }

    // ---- refresh ------------------------------------------------------------------------------------------------------------------------
    // Detects the clients, then runs the automatic fixes. manual: the person pressed Refresh (says what was found, reads names now).
    public async Task RefreshAsync(bool manual = false)
    {
        if (busy || disposed) return;
        SetBusy(true);
        if (manual) { lastWarning = false; Status(null, false); nextNameRefresh = DateTimeOffset.MinValue; }
        try
        {
            await ReadStateAsync();
            if (manual && !lastWarning) Status($"Detected {clients.Count} Roblox game windows. Positions have not been changed.", false);
        }
        catch (Exception ex) { Fail(ex); }
        finally { SetBusy(false); }
        // These start with "do nothing while busy", so they must run after the refresh has released Busy.
        _ = Placements.ApplySavedAsync();
        _ = Health.RecoverSignedOutAsync();
        _ = Health.ReopenAwayAsync(Alerts.IsAway);
        _ = Health.CloseDuplicateWindowsAsync(Alerts.IsAway);
        _ = Launch.FallbackStuckPrivateJoinsAsync();
    }

    private async Task ReadStateAsync()
    {
        var allowed = clients.Where(CanManage).Select(c => c.Process).ToHashSet();
        bool guardUnknown = RequiresIdentification;
        var state = await Task.Run(() =>
        {
            var found = windows.Detect();
            var screens = windows.GetMonitors();
            manager.PruneClosed();
            var included = found.Where(c => !manager.IsIgnored(c.Identity) && (!guardUnknown || allowed.Contains(ProcessIdentity.From(c.Identity)))).Select(c => ProcessIdentity.From(c.Identity)).ToArray();
            var resourceResult = resources.Synchronize(included);
            var memoryResult = memoryCaps.Synchronize(included);
            return (found, screens, resourceResult, memoryResult);
        });
        if (disposed) return;
        DetectedAt = DateTimeOffset.UtcNow;
        var keep = state.found.Select(c => ProcessIdentity.From(c.Identity)).ToHashSet();
        clients.RemoveAll(c => !keep.Contains(c.Process));
        foreach (var item in state.found)
        {
            var client = clients.FirstOrDefault(c => c.Process == ProcessIdentity.From(item.Identity));
            if (client is null)
            {
                client = new EngineClient(item, ++nextSlot) { IsIgnored = manager.IsIgnored(item.Identity) };
                client.SetCustomName(clientNames.Get(ProcessIdentity.From(item.Identity)));
                clients.Add(client);
            }
            else client.Update(item);
        }
        monitors = state.screens;
        if (state.resourceResult is { } result) { lastWarning = !result.Success; Status(result.Message, !result.Success); }
        if (state.memoryResult is { } memoryResult) { lastWarning |= !memoryResult.Success; Status((state.resourceResult?.Message is { } first ? first + " " : " ") + memoryResult.Message, lastWarning); }
        UpdateResourceStatus();
        if (Workspace.TrackRunning(clients, DateTimeOffset.UtcNow)) SaveWorkspace();
        StateChanged?.Invoke();
        ObserveHealth();
        _ = RefreshAccountNamesAsync();
    }

    private void UpdateResourceStatus()
    {
        foreach (var client in clients)
        {
            string status = resources.Status(client.Process) + " · " + memoryCaps.Status(client.Process);
            if (status != client.ResourceStatus) { client.ResourceStatus = status; client.RaiseChanged(); }
        }
    }

    private void SaveWorkspace() { if (!Workspace.Save()) Status("Could not save workspace preferences. Check the app's local settings folder.", true); }

    private void ObserveHealth()
    {
        Alerts.UpdateAway();
        var events = health.Observe(clients.Select(c => new ClientHealth(c.Process, c.Label,
            c.IsIgnored || (RequiresIdentification && c.AccountId is null), c.Status, c.Session)), DateTimeOffset.UtcNow);
        Alerts.Report(events);
        Alerts.AnnounceReturns();
        Health.Track(Alerts.IsAway); Alerts.EscalateStuck();
        Alerts.UpdateBoard();
    }

    // ---- which account is in which window ----------------------------------------------------------------------------------------------
    // Reads the Roblox logs at most every 30 seconds (or now, after a manual refresh) and puts each account's saved name,
    // ignore choice and resource plan back on its client.
    public async Task RefreshAccountNamesAsync()
    {
        if (disposed || resolvingNames || accountNames is null || clients.Count == 0 || DateTimeOffset.UtcNow < nextNameRefresh) return;
        resolvingNames = true;
        nextNameRefresh = DateTimeOffset.UtcNow.AddSeconds(30);
        var requested = clients.Select(c => c.Process).ToArray();
        try
        {
            if (accountNames is IAccountDetailsLookup detailed)
            {
                var details = await detailed.ResolveDetailsAsync(requested, nameCancellation.Token);
                while (busy && !disposed) await Task.Delay(50, nameCancellation.Token);
                if (disposed) return;
                SetBusy(true);
                restoring = true;
                try { await ApplyAccountDetailsAsync(details, requested); }
                finally { restoring = false; SetBusy(false); }
                return;
            }
            var result = await accountNames.ResolveAsync(requested, nameCancellation.Token);
            if (disposed) return;
            foreach (var client in clients)
                if (requested.Contains(client.Process)) client.SetDetectedName(result.GetValueOrDefault(client.Process));
            StateChanged?.Invoke();
        }
        catch (OperationCanceledException) { /* Closing the manager cancels outstanding name lookups. */ }
        catch (Exception) { log.Write("Automatic account names unavailable; manual labels remain available."); }
        finally
        {
            resolvingNames = false;
            if (disposed) nameCancellation.Dispose();
        }
    }

    private async Task<OperationResult> RestoreResourcesAsync(ProcessIdentity process) => await Task.Run(() =>
    {
        var a = resources.Restore(process); var b = memoryCaps.Restore(process);
        return new OperationResult(a.Success && b.Success, a.Message + " " + b.Message);
    });

    private async Task ApplyAccountDetailsAsync(IReadOnlyDictionary<ProcessIdentity, AccountDetails> details, ProcessIdentity[] requested)
    {
        bool changed = false;
        var profiles = Workspace.Profiles;
        var live = clients.Select(c => c.Process).ToHashSet();
        restoredProfiles.RemoveWhere(p => !live.Contains(p));
        editedNames.RemoveWhere(p => !live.Contains(p)); editedIgnores.RemoveWhere(p => !live.Contains(p));
        foreach (var client in clients.ToArray())
        {
            var process = client.Process;
            if (!requested.Contains(process) || !details.TryGetValue(process, out var info)) continue;
            long? previousId = client.AccountId;
            if (previousId is not null && info.Session.UserId is not null && previousId != info.Session.UserId)
            {
                // The process now belongs to another account: forget what was set for the old one.
                var reset = await RestoreResourcesAsync(process);
                if (!reset.Success) { Status(reset.Message, true); continue; }
                editedNames.Remove(process); editedIgnores.Remove(process);
                client.SetCustomName(null);
                manager.SetIgnored(client.Identity, false); client.IsIgnored = false; client.RaiseChanged();
            }
            client.SetAccountDetails(info);
            if (client.AccountId is not { } id) continue;
            if (previousId != id) restoredProfiles.Remove(process);
            if (restoredProfiles.Contains(process)) continue;
            if (profiles.TryGetValue(id, out var profile))
            {
                if (editedNames.Contains(process)) profile = profile with { CustomName = client.CustomName };
                if (editedIgnores.Contains(process)) profile = profile with { Ignored = client.IsIgnored };
                profiles[id] = profile;
                // Restore ignored status before any resource policy, even if a global plan is active.
                if (profile.Ignored)
                {
                    var reset = await RestoreResourcesAsync(process);
                    if (!reset.Success) { Status(reset.Message, true); continue; }
                }
                manager.SetIgnored(client.Identity, profile.Ignored); client.IsIgnored = profile.Ignored;
                client.SetCustomName(profile.CustomName);
                if (!client.IsIgnored && (resources.ActivePlan ?? profile.PreferredPlan) is { } plan)
                {
                    var applied = await Task.Run(() => resources.ApplySaved(plan, process));
                    if (!applied.Success) Status(applied.Message, true);
                    else if (resources.ActivePlan is not null) profiles[id] = profile with { PreferredPlan = plan };
                }
            }
            else if (profiles.Count < WorkspaceService.MaxProfiles)
                profiles[id] = new(id, client.CustomName, Workspace.NextOrder, client.IsIgnored, resources.AppliedPlan(process));
            restoredProfiles.Add(process); changed = true;
        }
        if (changed)
        {
            if (manualOrderChanged) SaveOrder();
            else
            {
                // Cards follow the saved order of their accounts; unknown clients go last.
                var ordered = clients.OrderBy(c => c.AccountId is { } id && profiles.TryGetValue(id, out var p) ? p.Order : int.MaxValue).ToArray();
                clients.Clear(); clients.AddRange(ordered); SaveWorkspace();
            }
            if (clients.All(c => c.AccountId is not null)) manualOrderChanged = false;
        }
        UpdateResourceStatus();
        StateChanged?.Invoke();
        ObserveHealth();
        if (Workspace.TrackRunning(clients, DateTimeOffset.UtcNow)) SaveWorkspace();
        StateChanged?.Invoke();
    }

    // A window whose account is not known yet (the manager only just started, or Roblox just opened it) would look like a closed account.
    // Before deciding what is closed, windows that are still unidentified get their account read once more.
    public async Task SettleAccountsAsync()
    {
        if (accountNames is null) return;
        bool Unknown() => clients.Any(c => !c.IsIgnored && c.AccountId is null && !Launch.WasLaunchedByManager(c.ProcessId));
        for (int round = 0; round < 3 && Unknown(); round++)
        {
            for (int wait = 0; wait < 100 && (busy || resolvingNames); wait++) await Task.Delay(100);
            await RefreshAsync();
            nextNameRefresh = DateTimeOffset.MinValue;
            await RefreshAccountNamesAsync();
        }
    }

    // ---- changes a person asks for -----------------------------------------------------------------------------------------------------
    public OperationResult SetClientName(EngineClient client, string? name)
    {
        if (disposed || !clients.Contains(client)) return new(false, "This client has closed. Select a current client.");
        try
        {
            string? value = AccountNames.Normalize(name);
            clientNames.Set(client.Process, value);
            client.SetCustomName(value);
            editedNames.Add(client.Process);
            SaveProfile(client);
            StateChanged?.Invoke();
            return new(true, value is null ? "Automatic naming restored." : "Account label saved.");
        }
        catch (ArgumentException ex) { return new(false, ex.Message); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        { return new(false, "Could not save the name locally. Check access to the app's settings folder."); }
    }

    public bool SwapClients(EngineClient first, EngineClient second)
    {
        if (busy || disposed || ReferenceEquals(first, second) || first.IsIgnored || second.IsIgnored) return false;
        int a = clients.IndexOf(first), b = clients.IndexOf(second);
        if (a < 0 || b < 0) return false;
        (clients[a], clients[b]) = (clients[b], clients[a]);
        manualOrderChanged |= clients.Any(c => c.AccountId is null);
        SaveOrder();
        StateChanged?.Invoke();
        Status($"Swapped Client {first.Slot:00} and Client {second.Slot:00}.", false);
        return true;
    }

    // Hiding a client puts its original resource settings back first; if that fails it stays visible.
    public async Task SetIgnoredAsync(EngineClient client, bool ignored)
    {
        if (client.IsIgnored == ignored || !clients.Contains(client)) return;
        if (busy) { client.RaiseChanged(); return; } // the window shows the unchanged state again
        client.IsIgnored = ignored;
        if (ignored && (resources.SavedCount > 0 || memoryCaps.SavedCount > 0))
        {
            OperationResult restored;
            SetBusy(true);
            try { restored = await RestoreResourcesAsync(client.Process); }
            catch (Exception ex) { restored = new(false, ex.Message); log.Write("Restore before ignoring failed.", ex); }
            if (!restored.Success)
            {
                client.IsIgnored = false; client.RaiseChanged();
                Status(restored.Message + " Client remains visible; retry Reset before ignoring it.", true);
                UpdateResourceStatus(); StateChanged?.Invoke(); SetBusy(false); return;
            }
        }
        manager.SetIgnored(client.Identity, ignored);
        editedIgnores.Add(client.Process);
        if (!ignored) { restoredProfiles.Remove(client.Process); nextNameRefresh = DateTimeOffset.MinValue; }
        SaveProfile(client);
        UpdateResourceStatus();
        client.RaiseChanged();
        StateChanged?.Invoke();
        int included = clients.Count(CanManage), hidden = clients.Count(c => c.IsIgnored);
        Status(ignored ? $"PID {client.ProcessId} hidden and ignored. Use the Ignored menu to show it again."
            : $"PID {client.ProcessId} included again. {included} visible · {hidden} ignored.", false);
        SetBusy(false);
    }

    private void SaveProfile(EngineClient client, ResourcePlan? preferredPlan = null, bool updatePlan = false)
    {
        if (client.AccountId is not { } id) return;
        Workspace.SaveProfile(id, client.CustomName, client.IsIgnored, preferredPlan, updatePlan);
        SaveWorkspace();
    }
    private void SaveOrder()
    {
        // Existing slots are reused so accounts that are currently offline keep their relative position.
        Workspace.SaveOrder(clients.Where(c => c.AccountId is not null).Select(c => c.AccountId!.Value).Distinct().ToArray());
        SaveWorkspace();
    }

    // ---- window and resource actions ----------------------------------------------------------------------------------------------------
    // Runs one action off the UI thread, reports its result and reads the clients again.
    private async Task RunAsync(Func<OperationResult> operation)
    {
        if (busy || disposed) return;
        SetBusy(true); lastWarning = false; Status("Checking windows and applying the requested action…", false);
        try
        {
            var result = await Task.Run(operation);
            Report(result);
            await ReadStateAsync();
        }
        catch (Exception ex) { Fail(ex); }
        finally { SetBusy(false); }
    }

    public Task FocusAsync(EngineClient client) { var id = client.Identity; return RunAsync(() => manager.Focus(id)); }
    public Task PrepareDashboardAsync() { var targets = clients.Where(CanManage).Select(c => c.Window).ToArray(); return RunAsync(() => manager.PrepareDashboard(targets)); }
    public Task RestoreGridAsync() => RunAsync(manager.RestoreGrid);
    public Task RestoreOriginalsAsync() => RunAsync(manager.RestoreOriginals);
    // main: the account you play on, maximized on mainScreen outside the grid.
    public Task ArrangeAsync(LayoutSpec layout, MonitorInfo screen, EngineClient? main, MonitorInfo mainScreen)
    {
        var windowsNow = clients.Select(c => c.Window).ToArray();
        var mainWindow = main is { IsIgnored: false } ? main.Window : null;
        return RunAsync(() => manager.ArrangeWithMain(windowsNow, mainWindow, screen, mainScreen, layout));
    }

    public async Task ApplyResourcePlanAsync(ResourcePlan plan)
    {
        var targets = clients.Where(CanManage).Select(c => c.Process).ToArray();
        await RunAsync(() => resources.Apply(plan, targets));
        foreach (var client in clients.Where(c => CanManage(c) && targets.Contains(c.Process)))
            SaveProfile(client, preferredPlan: resources.AppliedPlan(client.Process), updatePlan: true);
        UpdateResourceStatus(); StateChanged?.Invoke();
    }
    public async Task ApplyMemoryCapAsync(int limitMiB)
    {
        var targets = clients.Where(CanManage).Select(c => c.Process).ToArray();
        await RunAsync(() => memoryCaps.Apply(limitMiB, targets));
        UpdateResourceStatus(); StateChanged?.Invoke();
    }
    public async Task ResetMemoryCapAsync() { await RunAsync(memoryCaps.Reset); UpdateResourceStatus(); StateChanged?.Invoke(); }

    // Puts every client's original resource settings back. forgetPreferences: also forget the plans saved per account.
    public async Task<bool> StopResourcePlansAsync(bool forgetPreferences = true)
    {
        if (busy) return false;
        OperationResult? result = null;
        await RunAsync(() =>
        {
            var policy = resources.Reset(); var memory = memoryCaps.Reset();
            return result = new(policy.Success && memory.Success, policy.Message + " " + memory.Message);
        });
        if (forgetPreferences && result?.Success == true) { Workspace.ForgetPreferredPlans(); SaveWorkspace(); }
        UpdateResourceStatus(); StateChanged?.Invoke();
        return result?.Success == true;
    }

    // ---- IEngineHost: what the services use from the engine ----------------------------------------------------------------------------
    IReadOnlyList<IClientView> IEngineHost.Clients => clients;
    IReadOnlySet<long> IEngineHost.RunningAccounts => Workspace.RunningAccounts;
    int? IEngineHost.ProfileOrder(long userId) => Workspace.Profiles.TryGetValue(userId, out var p) ? p.Order : null;
    IReadOnlyCollection<AccountProfile> IEngineHost.Profiles => Workspace.Profiles.Values;
    public string NameOf(long userId) => Health.AccountLabels.GetValueOrDefault(userId) ?? Launch.Vault.Logins.FirstOrDefault(l => l.UserId == userId)?.Username ?? $"Account {userId}";
    bool IEngineHost.OtherFixRunning => Health.Working;
    void IEngineHost.SetStatus(string message, bool warning) => Status(message, warning);
    void IEngineHost.AddActivity(string label, string message, bool warning, bool announce, bool info, bool quiet) => Alerts.Add(label, message, warning, announce, info, quiet);
    void IEngineHost.LaunchStateChanged() => LaunchStateChanged?.Invoke();
    OperationResult IEngineHost.CloseClient(WindowIdentity identity) => manager.CloseClient(identity);

    public void Dispose()
    {
        if (disposed) return;
        Alerts.AnnounceClosing(); Alerts.StopWatchingForCrashes();
        try { if (Workspace.TrackRunning(clients, DateTimeOffset.UtcNow, forceSave: true)) Workspace.Save(); } catch (Exception ex) { log.Write("Account roster could not be saved on close.", ex); }
        disposed = true; (Launch.Launcher as IDisposable)?.Dispose(); Alerts.Webhook.Dispose();
        nameCancellation.Cancel();
        if (!resolvingNames) nameCancellation.Dispose();
        // Normal close uses the async reset path first; disposal is also safe for test hosts.
        var restored = resources.Reset();
        if (!restored.Success) log.Write(restored.Message);
        var memoryRestored = memoryCaps.Reset();
        if (!memoryRestored.Success) log.Write(memoryRestored.Message);
    }
}
