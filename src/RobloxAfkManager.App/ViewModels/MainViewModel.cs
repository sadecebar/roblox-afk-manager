using System.Collections.ObjectModel;
using System.ComponentModel;
using RobloxAfkManager.App.Services;
using RobloxAfkManager.Core;

namespace RobloxAfkManager.App.ViewModels;

public sealed record LayoutOption(string Label, int Columns, int Rows);

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IWindowSystem windows;
    private readonly WindowManager manager;
    private readonly ResourceProfileManager resources;
    private readonly WorkingSetCapManager memoryCaps;
    private readonly IAppLog log;
    private readonly WindowMonitor? monitor;
    private readonly IAccountNameLookup? accountNames;
    private readonly IClientNameStore clientNames;
    private readonly CancellationTokenSource nameCancellation = new();
    private bool resolvingNames;
    public bool IsResolvingAccounts => resolvingNames;
    private DateTimeOffset nextNameRefresh;
    private bool busy, disposed, warning;
    private bool fillPreviews = true;
    private string message = "Click a preview to switch to that client.";
    private string lastRefresh = "Waiting for detection";
    private MonitorInfo? selectedMonitor;
    private ClientTileViewModel? selectedClient;
    private LayoutOption selectedLayout;
    private string customColumns = "4", customRows = "5";
    private string currentGrid = "No grid arranged";
    private int savedCount;
    private int nextClientSlot;
    private bool changingIgnore;
    private ResourcePlan selectedResourcePlan = ResourcePlan.Balanced;
    private string memoryCapInput = "500";
    private DateTimeOffset? detectedAt;
    public RemoteMonitoringViewModel Remote { get; }

    public ObservableCollection<ClientTileViewModel> Clients { get; } = [];
    public ObservableCollection<ClientTileViewModel> VisibleClients { get; } = [];
    public ObservableCollection<ClientTileViewModel> IgnoredClients { get; } = [];
    public ObservableCollection<MonitorInfo> Monitors { get; } = [];
    public IReadOnlyList<LayoutOption> Layouts { get; } = [new("2 clients · 2 × 1 (test)", 2, 1), new("8 clients · 4 × 2", 4, 2), new("12 clients · 4 × 3", 4, 3), new("16 clients · 4 × 4", 4, 4), new("Custom layout", 0, 0)];
    public AsyncCommand DetectCommand { get; }
    public AsyncCommand ArrangeCommand { get; }
    public AsyncCommand FocusCommand { get; }
    public AsyncCommand FocusClientCommand { get; }
    public AsyncCommand PrepareDashboardCommand { get; }
    public AsyncCommand UnignoreClientCommand { get; }
    public AsyncCommand RestoreGridCommand { get; }
    public AsyncCommand RestoreOriginalsCommand { get; }
    public AsyncCommand ApplyResourcePlanCommand { get; }
    public AsyncCommand ResetResourcePlanCommand { get; }
    public AsyncCommand ApplyMemoryCapCommand { get; }
    public AsyncCommand ResetMemoryCapCommand { get; }
    public IReadOnlyList<ResourcePlan> ResourcePlans { get; } = Enum.GetValues<ResourcePlan>();
    private IEnumerable<AsyncCommand> Commands => [DetectCommand, ArrangeCommand, FocusCommand, FocusClientCommand, PrepareDashboardCommand, UnignoreClientCommand, RestoreGridCommand, RestoreOriginalsCommand, ApplyResourcePlanCommand, ResetResourcePlanCommand, ApplyMemoryCapCommand, ResetMemoryCapCommand];

    public MainViewModel(IWindowSystem? windowSystem = null, IAppLog? appLog = null, IProcessResourceSystem? resourceSystem = null, IWorkingSetSystem? workingSetSystem = null,
        IAccountNameLookup? accountNameLookup = null, IClientNameStore? clientNameStore = null, IWorkspaceStore? workspaceStore = null)
    {
        log = appLog ?? new FileLog(); windows = windowSystem ?? new Win32WindowSystem(log); manager = new(windows, log);
        accountNames = accountNameLookup ?? (windows is Win32WindowSystem ? new LogAccountNameLookup() : null);
        clientNames = clientNameStore ?? (windows is Win32WindowSystem ? new ClientNameStore() : new MemoryClientNameStore());
        Remote = new(windows.IsCurrent, log);
        resources = new(resourceSystem ?? new Win32ProcessResources(), log);
        memoryCaps = new(workingSetSystem ?? new Win32ProcessResources(), log);
        InitializeWorkspace(workspaceStore ?? (windows is Win32WindowSystem ? new WorkspaceStore() : new MemoryWorkspaceStore())); InitializeRoster();
        selectedLayout = Layouts[0];
        DetectCommand = new(() => RefreshAsync(true), () => !Busy, HandleError);
        ArrangeCommand = new(ArrangeAsync, () => !Busy && IncludedCount > 0 && SelectedMonitor is not null, HandleError);
        FocusCommand = new(() => { var id = SelectedClient!.Identity; return RunOperation(() => manager.Focus(id)); }, () => !Busy && SelectedClient is not null, HandleError);
        FocusClientCommand = new(parameter =>
        {
            if (parameter is not ClientTileViewModel client) return Task.CompletedTask;
            SelectedClient = client;
            var id = client.Identity;
            return RunOperation(() => manager.Focus(id));
        }, parameter => !Busy && parameter is ClientTileViewModel client && VisibleClients.Contains(client), HandleError);
        UnignoreClientCommand = new(parameter =>
        {
            if (parameter is ClientTileViewModel client) client.IsIgnored = false;
            return Task.CompletedTask;
        }, parameter => !Busy && parameter is ClientTileViewModel client && IgnoredClients.Contains(client), HandleError);
        PrepareDashboardCommand = new(() =>
        {
            var clients = Clients.Where(CanManageProfile).Select(c => c.Snapshot).ToArray();
            return RunOperation(() => manager.PrepareDashboard(clients));
        }, () => !Busy && IncludedCount > 0, HandleError);
        RestoreGridCommand = new(() => RunOperation(manager.RestoreGrid), () => !Busy && manager.CanRestoreGrid, HandleError);
        RestoreOriginalsCommand = new(() => RunOperation(manager.RestoreOriginals), () => !Busy && SavedCount > 0, HandleError);
        ApplyResourcePlanCommand = new(async () =>
        {
            var targets = VisibleClients.Where(CanManageProfile).Select(c => ProcessIdentity.From(c.Identity)).ToArray();
            var plan = SelectedResourcePlan;
            await RunOperation(() => resources.Apply(plan, targets));
            foreach (var client in VisibleClients.Where(c => targets.Contains(ProcessIdentity.From(c.Identity))))
                SaveProfile(client, preferredPlan: resources.AppliedPlan(ProcessIdentity.From(client.Identity)), updatePlan: true);
        }, () => !Busy && IncludedCount > 0, HandleError);
        ResetResourcePlanCommand = new(async () => { await StopResourcePlansAsync(); }, () => !Busy && (HasResourceChanges || HasSavedPlans), HandleError);
        ApplyMemoryCapCommand = new(() =>
        {
            var targets = VisibleClients.Where(CanManageProfile).Select(c => ProcessIdentity.From(c.Identity)).ToArray();
            int limit = int.Parse(MemoryCapInput);
            return RunOperation(() => memoryCaps.Apply(limit, targets));
        }, () => !Busy && IncludedCount > 0 && int.TryParse(MemoryCapInput, out int cap) && cap is >= 256 and <= 8192, HandleError);
        ResetMemoryCapCommand = new(() => RunOperation(memoryCaps.Reset), () => !Busy && (memoryCaps.ActiveMiB is not null || memoryCaps.SavedCount > 0), HandleError);
        if (windows is Win32WindowSystem native) monitor = new(native, log, () => _ = RefreshAsync());
    }

    public bool Busy { get => busy; private set { if (Set(ref busy, value)) { foreach (var c in Commands) c.Notify(); Raise(nameof(IsIdle)); } } }
    public bool IsIdle => !Busy;
    public bool FillPreviews { get => fillPreviews; set { if (Set(ref fillPreviews, value)) Raise(nameof(FitPreviews)); } }
    public bool FitPreviews { get => !fillPreviews; set { if (value) FillPreviews = false; } }
    public ResourcePlan SelectedResourcePlan { get => selectedResourcePlan; set { if (Set(ref selectedResourcePlan, value)) Raise(nameof(ResourcePlanDescription)); } }
    public string ResourcePlanDescription => SelectedResourcePlan switch
    {
        ResourcePlan.Low => "Low: lower CPU priority, energy-efficient scheduling and low memory priority. Background games may respond more slowly.",
        ResourcePlan.Balanced => "Balanced: lower CPU priority, system-managed power and slightly lower memory priority.",
        _ => "High: normal CPU and memory priority, with energy throttling disabled. Gives clients more scheduling resources."
    };
    public bool HasResourceChanges => resources.ActivePlan is not null || resources.SavedCount > 0 || memoryCaps.ActiveMiB is not null || memoryCaps.SavedCount > 0;
    public string MemoryCapInput { get => memoryCapInput; set { if (Set(ref memoryCapInput, value)) ApplyMemoryCapCommand.Notify(); } }
    public string MemoryCapSummary => memoryCaps.ActiveMiB is { } cap ? $"{cap} MiB per client · {memoryCaps.AppliedCount} applied" : memoryCaps.SavedCount > 0 ? "Resident limit reset needs retry" : "Resident RAM limit off";
    public string ResourcePlanSummary => resources.ActivePlan is { } plan ? $"{plan} · {resources.AppliedCount} applied" : resources.SavedCount > 0 ? "Reset needs retry" : "Plans off";
    public bool Warning { get => warning; private set => Set(ref warning, value); }
    public string Message { get => message; private set => Set(ref message, value); }
    public string LastRefresh { get => lastRefresh; private set => Set(ref lastRefresh, value); }
    public int ClientCount => Clients.Count;
    public double DashboardPreviewAspectRatio
    {
        get
        {
            var ratios = VisibleClients.Where(c => c.CanPreview && c.WindowBounds.Height > 0 && c.WindowBounds.Width > 0)
                .Select(c => (double)c.WindowBounds.Width / c.WindowBounds.Height).Order().ToArray();
            return ratios.Length == 0 ? 1.5 : Math.Clamp(ratios[ratios.Length / 2], 0.5, 3);
        }
    }
    public int IncludedCount => Clients.Count(CanManageProfile);
    public int IgnoredCount => Clients.Count(c => c.IsIgnored);
    public string SelectionSummary => $"{IncludedCount} visible · {IgnoredCount} ignored";
    public string IgnoredMenuLabel => $"Ignored ({IgnoredCount})";
    public bool HasIgnored => IgnoredCount > 0;
    public int AttentionCount => VisibleClients.Count(c => c.NeedsAttention);
    public bool HasAttention => AttentionCount > 0;
    public int JoinedCount => VisibleClients.Count(c => c.StatusTone == "Good");
    public string HealthSummary => AttentionCount > 0 ? (AttentionCount == 1 ? "1 needs attention" : $"{AttentionCount} need attention")
        : OfflineCount > 0 ? $"{OfflineCount} offline" : VisibleClients.Count == 0 ? "No clients" : "All clear";
    // Alert: a running client needs a look. Warn: an expected account is not running. Good / Idle otherwise.
    public string HealthTone => AttentionCount > 0 ? "Alert" : OfflineCount > 0 ? "Warn" : VisibleClients.Count == 0 ? "Idle" : "Good";
    public Func<WindowIdentity, bool> PreviewValidator => windows.IsCurrent;
    public bool IsEmpty => VisibleClients.Count == 0;
    public string EmptyHeading => ClientCount == 0 ? "Open Roblox to start" : HasPendingProfiles ? "Identifying accounts" : "All clients are ignored";
    public string EmptyMessage => ClientCount == 0 ? "Your running clients appear here automatically. Click a preview to bring the game forward." : HasPendingProfiles ? "Restoring saved ignore choices. If identification is unavailable, open Ignored to show unidentified clients for this session." : "Open the Ignored menu and choose a client to bring it back to the dashboard.";
    public string TotalMemory => $"{Clients.Sum(c => c.Snapshot.MemoryBytes ?? 0) / 1073741824d:N2} GB";
    public string CurrentGrid { get => currentGrid; private set => Set(ref currentGrid, value); }
    public int SavedCount { get => savedCount; private set => Set(ref savedCount, value); }
    public string LogPath => (log as FileLog)?.DirectoryPath ?? "";
    public MonitorInfo? SelectedMonitor { get => selectedMonitor; set { if (Set(ref selectedMonitor, value)) { Raise(nameof(MonitorDetails)); ArrangeCommand.Notify(); } } }
    public string MonitorDetails => SelectedMonitor?.Details ?? "No monitor available";
    public ClientTileViewModel? SelectedClient { get => selectedClient; set { if (Set(ref selectedClient, value)) FocusCommand.Notify(); } }
    public LayoutOption SelectedLayout { get => selectedLayout; set { if (Set(ref selectedLayout, value)) { Raise(nameof(IsCustom)); Raise(nameof(LayoutSummary)); } } }
    public bool IsCustom => SelectedLayout.Columns == 0;
    public string LayoutSummary => IsCustom ? $"Custom · {CustomColumns} × {CustomRows}" : $"{SelectedLayout.Columns} columns × {SelectedLayout.Rows} rows";
    public string CustomColumns { get => customColumns; set { if (Set(ref customColumns, value)) Raise(nameof(LayoutSummary)); } }
    public string CustomRows { get => customRows; set { if (Set(ref customRows, value)) Raise(nameof(LayoutSummary)); } }

    public async Task RefreshAsync(bool manual = false)
    {
        if (Busy || disposed) return;
        Busy = true;
        if (manual) { Warning = false; nextNameRefresh = DateTimeOffset.MinValue; }
        try
        {
            await ReadStateAsync();
            if (manual && !Warning) Message = $"Detected {ClientCount} Roblox game windows. Positions have not been changed.";
        }
        catch (Exception ex) { HandleError(ex); }
        finally { Busy = false; }
    }

    private async Task ReadStateAsync()
    {
        var allowed = Clients.Where(CanManageProfile).Select(c => ProcessIdentity.From(c.Identity)).ToHashSet();
        bool guardUnknown = RequiresProfileIdentification;
        var state = await Task.Run(() =>
        {
            var clients = windows.Detect();
            var screens = windows.GetMonitors();
            manager.PruneClosed();
            var included = clients.Where(c => !manager.IsIgnored(c.Identity) && (!guardUnknown || allowed.Contains(ProcessIdentity.From(c.Identity)))).Select(c => ProcessIdentity.From(c.Identity)).ToArray();
            var resourceResult = resources.Synchronize(included);
            var memoryResult = memoryCaps.Synchronize(included);
            return (clients, screens, resourceResult, memoryResult);
        });
        if (disposed) return;
        detectedAt = DateTimeOffset.UtcNow;
        var selectedId = SelectedClient?.Identity;
        var keep = state.clients.Select(c => ProcessIdentity.From(c.Identity)).ToHashSet();
        foreach (var tile in Clients.Where(c => !keep.Contains(ProcessIdentity.From(c.Identity))).ToArray())
        { tile.PropertyChanged -= ClientChanged; Clients.Remove(tile); }
        for (int i = 0; i < state.clients.Count; i++)
        {
            var item = state.clients[i];
            var tile = Clients.FirstOrDefault(c => ProcessIdentity.From(c.Identity) == ProcessIdentity.From(item.Identity));
            if (tile is null)
            {
                tile = new(item, ++nextClientSlot) { IsIgnored = manager.IsIgnored(item.Identity) };
                tile.SetCustomName(clientNames.Get(ProcessIdentity.From(item.Identity)));
                tile.PropertyChanged += ClientChanged;
                Clients.Add(tile);
            }
            else tile.Update(item);
        }
        SelectedClient = Clients.FirstOrDefault(c => c.Identity == selectedId);
        if (!Monitors.SequenceEqual(state.screens))
        {
            var device = SelectedMonitor?.DeviceName;
            Monitors.Clear();
            foreach (var screen in state.screens) Monitors.Add(screen);
            SelectedMonitor = Monitors.FirstOrDefault(m => m.DeviceName == device) ?? Monitors.FirstOrDefault();
        }
        SavedCount = manager.OriginalCount;
        CurrentGrid = manager.LastGrid is { } grid ? $"{grid.Layout} on {grid.MonitorDevice.Replace(@"\\.\", "")}" : "No grid arranged";
        LastRefresh = $"Updated {DateTime.Now:HH:mm:ss}";
        if (state.resourceResult is { } result) { Warning = !result.Success; Message = result.Message; }
        if (state.memoryResult is { } memoryResult) { Warning |= !memoryResult.Success; Message += " " + memoryResult.Message; }
        RaiseResources();
        Raise(nameof(ClientCount)); Raise(nameof(TotalMemory)); RaiseSelection(); TrackRoster();
        ObserveHealth();
        _ = RefreshAccountNamesAsync();
    }

    public async Task RefreshAccountNamesAsync()
    {
        if (disposed || resolvingNames || accountNames is null || Clients.Count == 0 || DateTimeOffset.UtcNow < nextNameRefresh) return;
        resolvingNames = true;
        nextNameRefresh = DateTimeOffset.UtcNow.AddSeconds(30);
        var requested = Clients.Select(c => ProcessIdentity.From(c.Identity)).ToArray();
        try
        {
            if (accountNames is IAccountDetailsLookup detailed)
            {
                var details = await detailed.ResolveDetailsAsync(requested, nameCancellation.Token);
                while (Busy && !disposed) await Task.Delay(50, nameCancellation.Token);
                if (disposed) return;
                Busy = true;
                restoringWorkspace = true;
                try { await ApplyAccountDetailsAsync(details, requested); }
                finally { restoringWorkspace = false; Busy = false; }
                return;
            }
            var result = await accountNames.ResolveAsync(requested, nameCancellation.Token);
            if (disposed) return;
            foreach (var tile in Clients)
            {
                var process = ProcessIdentity.From(tile.Identity);
                if (requested.Contains(process)) tile.SetDetectedName(result.GetValueOrDefault(process));
            }
        }
        catch (OperationCanceledException) { /* Closing the manager cancels outstanding name lookups. */ }
        catch (Exception) { log.Write("Automatic account names unavailable; manual labels remain available."); }
        finally
        {
            resolvingNames = false;
            if (disposed) nameCancellation.Dispose();
        }
    }

    public OperationResult SetClientName(ClientTileViewModel tile, string? name)
    {
        if (disposed || !Clients.Contains(tile)) return new(false, "This client has closed. Select a current client.");
        try
        {
            string? value = AccountNames.Normalize(name);
            clientNames.Set(ProcessIdentity.From(tile.Identity), value);
            tile.SetCustomName(value);
            editedNames.Add(ProcessIdentity.From(tile.Identity));
            SaveProfile(tile);
            return new(true, value is null ? "Automatic naming restored." : "Account label saved.");
        }
        catch (ArgumentException ex) { return new(false, ex.Message); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        { return new(false, "Could not save the name locally. Check access to the app's settings folder."); }
    }

    public bool SwapClients(ClientTileViewModel first, ClientTileViewModel second)
    {
        if (Busy || disposed || ReferenceEquals(first, second) || first.IsIgnored || second.IsIgnored) return false;
        int a = Clients.IndexOf(first), b = Clients.IndexOf(second);
        if (a < 0 || b < 0) return false;
        int low = Math.Min(a, b), high = Math.Max(a, b);
        Clients.Move(high, low);
        Clients.Move(low + 1, high);
        manualOrderChanged |= Clients.Any(c => c.AccountId is null);
        RaiseSelection();
        SaveOrder();
        Message = $"Swapped Client {first.Slot} and Client {second.Slot}.";
        return true;
    }

    private async void ClientChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClientTileViewModel.Label)) { if (!restoringWorkspace) RaiseSelection(); return; }
        if (changingIgnore || e.PropertyName != nameof(ClientTileViewModel.IsIgnored) || sender is not ClientTileViewModel tile) return;
        if (Busy) { tile.IsIgnored = manager.IsIgnored(tile.Identity); return; }
        if (tile.IsIgnored && (resources.SavedCount > 0 || memoryCaps.SavedCount > 0))
        {
            OperationResult restored;
            Busy = true;
            try
            {
                restored = await Task.Run(() =>
                {
                    var policy = resources.Restore(ProcessIdentity.From(tile.Identity));
                    var memory = memoryCaps.Restore(ProcessIdentity.From(tile.Identity));
                    return new OperationResult(policy.Success && memory.Success, policy.Message + " " + memory.Message);
                });
            }
            catch (Exception ex) { restored = new(false, ex.Message); log.Write("Restore before ignoring failed.", ex); }
            if (!restored.Success)
            {
                changingIgnore = true; try { tile.IsIgnored = false; } finally { changingIgnore = false; }
                Warning = true; Message = restored.Message + " Client remains visible; retry Reset before ignoring it.";
                RaiseResources(); Busy = false; return;
            }
        }
        manager.SetIgnored(tile.Identity, tile.IsIgnored);
        editedIgnores.Add(ProcessIdentity.From(tile.Identity));
        if (!tile.IsIgnored) { restoredProfiles.Remove(ProcessIdentity.From(tile.Identity)); nextNameRefresh = DateTimeOffset.MinValue; }
        SaveProfile(tile);
        SavedCount = manager.OriginalCount;
        RaiseSelection();
        RaiseResources();
        Warning = false;
        Message = tile.IsIgnored
            ? $"PID {tile.ProcessId} hidden and ignored. Use the Ignored menu to show it again."
            : $"PID {tile.ProcessId} included again. {SelectionSummary}.";
        foreach (var command in Commands) command.Notify();
        Busy = false;
    }

    private void RaiseResources()
    {
        foreach (var client in Clients) client.ResourceStatus = resources.Status(ProcessIdentity.From(client.Identity)) + " · " + memoryCaps.Status(ProcessIdentity.From(client.Identity));
        Raise(nameof(ResourcePlanSummary)); Raise(nameof(HasResourceChanges));
        Raise(nameof(MemoryCapSummary)); ApplyMemoryCapCommand.Notify(); ResetMemoryCapCommand.Notify();
        ApplyResourcePlanCommand.Notify(); ResetResourcePlanCommand.Notify();
    }
    public async Task<bool> StopResourcePlansAsync(bool forgetPreferences = true)
    {
        if (Busy) return false;
        OperationResult? result = null;
        await RunOperation(() =>
        {
            var policy = resources.Reset(); var memory = memoryCaps.Reset();
            return result = new(policy.Success && memory.Success, policy.Message + " " + memory.Message);
        });
        if (forgetPreferences && result?.Success == true)
        {
            foreach (long id in profiles.Keys.ToArray()) profiles[id] = profiles[id] with { PreferredPlan = null };
            SaveWorkspace();
        }
        return result?.Success == true;
    }

    private void RaiseSelection()
    {
        Reconcile(VisibleClients, Clients.Where(CanManageProfile).ToArray());
        Reconcile(IgnoredClients, Clients.Where(c => c.IsIgnored).ToArray());
        Remote.Feed.Publish(VisibleClients.Select(c => new RobloxAfkManager.App.Remote.MonitoredClient(c.Snapshot, c.Label, c.GameName, c.SessionStatus,
            c.Session?.Phase == SessionPhase.Joined ? c.Session.JoinedAt : null)), Clients.Where(c => !CanManageProfile(c)).Select(c => c.Identity), detectedAt);
        if (SelectedClient?.IsIgnored == true) SelectedClient = null;
        Raise(nameof(IncludedCount)); Raise(nameof(IgnoredCount)); Raise(nameof(SelectionSummary)); Raise(nameof(IgnoredMenuLabel)); Raise(nameof(HasIgnored));
        Raise(nameof(IsEmpty)); Raise(nameof(EmptyHeading)); Raise(nameof(EmptyMessage)); Raise(nameof(DashboardPreviewAspectRatio));
        Raise(nameof(PendingProfileCount)); Raise(nameof(HasPendingProfiles)); Raise(nameof(PendingProfileHint));
        Raise(nameof(AttentionCount)); Raise(nameof(HasAttention)); Raise(nameof(JoinedCount)); Raise(nameof(HealthSummary)); Raise(nameof(HealthTone));
    }
    private static void Reconcile(ObservableCollection<ClientTileViewModel> collection, IReadOnlyList<ClientTileViewModel> desired)
    {
        foreach (var item in collection.Where(c => !desired.Contains(c)).ToArray()) collection.Remove(item);
        for (int i = 0; i < desired.Count; i++)
        {
            int old = collection.IndexOf(desired[i]);
            if (old < 0) collection.Insert(i, desired[i]);
            else if (old != i) collection.Move(old, i);
        }
    }

    private Task ArrangeAsync()
    {
        int cols = SelectedLayout.Columns, rows = SelectedLayout.Rows;
        if (IsCustom && (!int.TryParse(CustomColumns, out cols) || !int.TryParse(CustomRows, out rows)))
        { Warning = true; Message = "Enter whole numbers between 1 and 32 for columns and rows."; return Task.CompletedTask; }
        var clients = Clients.Select(c => c.Snapshot).ToArray();
        var screen = SelectedMonitor!;
        return RunOperation(() => manager.Arrange(clients, screen, new(cols, rows)));
    }

    private async Task RunOperation(Func<OperationResult> operation)
    {
        if (Busy || disposed) return;
        Busy = true; Warning = false; Message = "Checking windows and applying the requested action…";
        try
        {
            var result = await Task.Run(operation);
            Message = result.Message; Warning = !result.Success;
            await ReadStateAsync();
        }
        catch (Exception ex) { HandleError(ex); }
        finally { Busy = false; }
    }

    private void HandleError(Exception ex)
    {
        log.Write("Dashboard operation failed.", ex);
        Warning = true; Message = ex.Message;
    }
    public void NotifyDisplayChanged() => monitor?.RequestRefresh();
    public void Dispose()
    {
        if (disposed) return;
        try { TrackRoster(forceSave: true); } catch (Exception ex) { log.Write("Account roster could not be saved on close.", ex); }
        disposed = true; monitor?.Dispose();
        nameCancellation.Cancel();
        if (!resolvingNames) nameCancellation.Dispose();
        Remote.Dispose();
        // Normal close uses the async reset path first; disposal is also safe for test hosts.
        var restored = resources.Reset();
        if (!restored.Success) log.Write(restored.Message);
        var memoryRestored = memoryCaps.Reset();
        if (!memoryRestored.Success) log.Write(memoryRestored.Message);
        foreach (var tile in Clients) tile.PropertyChanged -= ClientChanged;
    }
}
