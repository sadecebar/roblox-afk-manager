using System.Collections.ObjectModel;
using RobloxAfkManager.App.Services;
using RobloxAfkManager.Core;

namespace RobloxAfkManager.App.ViewModels;

public sealed record LayoutOption(string Label, int Columns, int Rows);

// The dashboard. It builds the engine (with the Windows implementations, or the fakes a test passes in), shows the engine's state
// and turns clicks into engine calls. What the manager does lives in RobloxAfkManager.Engine; see docs/ENGINE.md.
public sealed partial class MainViewModel : ObservableObject, IDisposable, IUserPrompts
{
    private readonly IWindowSystem windows;
    private readonly IAppLog log;
    private readonly WindowMonitor? monitor;
    private readonly IAccountNameLookup? accountNames;
    private readonly AfkEngine engine;
    private readonly Dictionary<EngineClient, ClientTileViewModel> tiles = [];
    private bool busy, warning, disposed;
    private bool fillPreviews = true;
    private string message = "Click a preview to switch to that client.";
    private string lastRefresh = "Waiting for detection";
    private MonitorInfo? selectedMonitor;
    private ClientTileViewModel? selectedClient;
    private LayoutOption selectedLayout;
    private string customColumns = "4", customRows = "5";
    private string currentGrid = "No grid arranged";
    private int savedCount;
    private ResourcePlan selectedResourcePlan = ResourcePlan.Balanced;
    private string memoryCapInput = "500";
    public RemoteMonitoringViewModel Remote { get; }
    public bool IsResolvingAccounts => engine.ResolvingNames;

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
    private IEnumerable<AsyncCommand?> Commands => [DetectCommand, ArrangeCommand, FocusCommand, FocusClientCommand, PrepareDashboardCommand, UnignoreClientCommand, RestoreGridCommand, RestoreOriginalsCommand, ApplyResourcePlanCommand, ResetResourcePlanCommand, ApplyMemoryCapCommand, ResetMemoryCapCommand, SaveLayoutCommand, ClearLayoutCommand, OpenClosedCommand, OpenSelectedCommand, OpenLoginCommand, RestartClientCommand, ResetServersCommand, ResplitServersCommand];

    public MainViewModel(IWindowSystem? windowSystem = null, IAppLog? appLog = null, IProcessResourceSystem? resourceSystem = null, IWorkingSetSystem? workingSetSystem = null,
        IAccountNameLookup? accountNameLookup = null, IClientNameStore? clientNameStore = null, IWorkspaceStore? workspaceStore = null,
        IAccountLauncher? accountLauncher = null, LoginVault? loginVault = null, string? settingsFolder = null, DiscordWebhook? discordWebhook = null, IUserIdle? userIdle = null)
    {
        // The real app runs on Windows; a test passes fakes, and then nothing touches the person's real files or webhook.
        log = appLog ?? new FileLog(); windows = windowSystem ?? new Win32WindowSystem(log);
        bool real = windows is Win32WindowSystem;
        accountNames = accountNameLookup ?? (real ? new LogAccountNameLookup(owners: LogFileOwners.Read, stateFile: System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RobloxAfkManager", "process-accounts.txt")) : null);
        var system = new Win32ProcessResources();
        engine = new AfkEngine(new EngineOptions
        {
            Windows = windows, Log = log,
            Resources = resourceSystem ?? system, WorkingSet = workingSetSystem ?? system,
            AccountLookup = accountNames,
            ClientNames = clientNameStore ?? (real ? new ClientNameStore() : new MemoryClientNameStore()),
            Workspace = workspaceStore ?? (real ? new WorkspaceStore() : new MemoryWorkspaceStore()),
            Launcher = accountLauncher ?? new AccountLauncher(new WindowsRobloxClients()),
            Vault = loginVault ?? new LoginVault(),
            SettingsFolder = settingsFolder ?? AppFolders.Data,
            // Test hosts never touch the real webhook file, so a test can't post to someone's channel.
            Webhook = discordWebhook ?? (real ? new DiscordWebhook(log: log) : new DiscordWebhook(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rafk-no-webhook"), log: log)),
            Idle = userIdle ?? (real ? new Win32UserIdle() : new NeverIdle()),
            Prompts = this,
        });
        engine.StateChanged += OnEngineState;
        engine.StatusChanged += (text, warn) => { Warning = warn; if (text is not null) Message = text; };
        engine.BusyChanged += () => Busy = engine.Busy;
        engine.LaunchStateChanged += () => { RaiseServers(); RefreshLogins(); };
        Remote = new(windows.IsCurrent, log);
        InitializeWorkspace(); InitializeRoster(); InitializeLaunch(); InitializeDiscord(); InitializePlacements();
        selectedLayout = Layouts[1];
        DetectCommand = new(() => RefreshAsync(true), () => !Busy, HandleError);
        ArrangeCommand = new(ArrangeAsync, () => !Busy && IncludedCount > 0 && SelectedMonitor is not null, HandleError);
        FocusCommand = new(() => engine.FocusAsync(SelectedClient!.Client), () => !Busy && SelectedClient is not null, HandleError);
        FocusClientCommand = new(parameter =>
        {
            if (parameter is not ClientTileViewModel client) return Task.CompletedTask;
            SelectedClient = client;
            return engine.FocusAsync(client.Client);
        }, parameter => !Busy && parameter is ClientTileViewModel client && VisibleClients.Contains(client), HandleError);
        UnignoreClientCommand = new(parameter => parameter is ClientTileViewModel client ? engine.SetIgnoredAsync(client.Client, false) : Task.CompletedTask,
            parameter => !Busy && parameter is ClientTileViewModel client && IgnoredClients.Contains(client), HandleError);
        PrepareDashboardCommand = new(engine.PrepareDashboardAsync, () => !Busy && IncludedCount > 0, HandleError);
        RestoreGridCommand = new(engine.RestoreGridAsync, () => !Busy && engine.CanRestoreGrid, HandleError);
        RestoreOriginalsCommand = new(engine.RestoreOriginalsAsync, () => !Busy && SavedCount > 0, HandleError);
        ApplyResourcePlanCommand = new(() => engine.ApplyResourcePlanAsync(SelectedResourcePlan), () => !Busy && IncludedCount > 0, HandleError);
        ResetResourcePlanCommand = new(async () => { await StopResourcePlansAsync(); }, () => !Busy && (HasResourceChanges || HasSavedPlans), HandleError);
        ApplyMemoryCapCommand = new(() => engine.ApplyMemoryCapAsync(int.Parse(MemoryCapInput)),
            () => !Busy && IncludedCount > 0 && int.TryParse(MemoryCapInput, out int cap) && cap is >= 256 and <= 8192, HandleError);
        ResetMemoryCapCommand = new(engine.ResetMemoryCapAsync, () => !Busy && (engine.MemoryCapMiB is not null || engine.MemoryCapSavedCount > 0), HandleError);
        if (windows is Win32WindowSystem native) monitor = new(native, log, () => _ = RefreshAsync());
        engine.Start();
    }

    // The Reset servers confirmation. The window sets ConfirmReset to show a dialog; without it (tests) the engine proceeds.
    public Func<int, bool>? ConfirmReset { get; set; }
    bool IUserPrompts.ConfirmResetServers(int count) => ConfirmReset?.Invoke(count) ?? true;

    public bool Busy { get => busy; private set { if (Set(ref busy, value)) { NotifyCommands(); Raise(nameof(IsIdle)); } } }
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
    public bool HasResourceChanges => engine.HasResourceChanges;
    public string MemoryCapInput { get => memoryCapInput; set { if (Set(ref memoryCapInput, value)) ApplyMemoryCapCommand.Notify(); } }
    public string MemoryCapSummary => engine.MemoryCapMiB is { } cap ? $"{cap} MiB per client · {engine.MemoryCapAppliedCount} applied" : engine.MemoryCapSavedCount > 0 ? "Resident limit reset needs retry" : "Resident RAM limit off";
    public string ResourcePlanSummary => engine.ActivePlan is { } plan ? $"{plan} · {engine.PlanAppliedCount} applied" : engine.PlanSavedCount > 0 ? "Reset needs retry" : "Plans off";
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
    private bool CanManageProfile(ClientTileViewModel tile) => engine.CanManage(tile.Client);
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
    // The account you play on: maximized with its border on the primary monitor, outside the grid.
    public ClientTileViewModel? MainClient { get => mainClient; set { if (Set(ref mainClient, value)) Raise(nameof(MainSummary)); } }
    private ClientTileViewModel? mainClient;
    public string MainSummary => MainClient is null ? "No main client: every visible client goes in the grid." : $"{MainClient.Label} will be maximized on the primary monitor; the rest fill the grid.";
    public ClientTileViewModel? SelectedClient { get => selectedClient; set { if (Set(ref selectedClient, value)) FocusCommand.Notify(); } }
    public LayoutOption SelectedLayout { get => selectedLayout; set { if (Set(ref selectedLayout, value)) { Raise(nameof(IsCustom)); Raise(nameof(LayoutSummary)); } } }
    public bool IsCustom => SelectedLayout.Columns == 0;
    public string LayoutSummary => IsCustom ? $"Custom · {CustomColumns} × {CustomRows}" : $"{SelectedLayout.Columns} columns × {SelectedLayout.Rows} rows";
    public string CustomColumns { get => customColumns; set { if (Set(ref customColumns, value)) Raise(nameof(LayoutSummary)); } }
    public string CustomRows { get => customRows; set { if (Set(ref customRows, value)) Raise(nameof(LayoutSummary)); } }

    public Task RefreshAsync(bool manual = false) => engine.RefreshAsync(manual);
    public Task RefreshAccountNamesAsync() => engine.RefreshAccountNamesAsync();
    public OperationResult SetClientName(ClientTileViewModel tile, string? name) => engine.SetClientName(tile.Client, name);
    public bool SwapClients(ClientTileViewModel first, ClientTileViewModel second) => engine.SwapClients(first.Client, second.Client);
    public Task<bool> StopResourcePlansAsync(bool forgetPreferences = true) => engine.StopResourcePlansAsync(forgetPreferences);

    // The engine's clients, order or monitors changed: mirror them on the cards and lists.
    private void OnEngineState()
    {
        if (disposed) return;
        var selectedId = SelectedClient?.Identity;
        var current = engine.Clients;
        foreach (var gone in tiles.Keys.Where(c => !current.Contains(c)).ToArray()) tiles.Remove(gone);
        var desired = current.Select(c => tiles.TryGetValue(c, out var tile) ? tile : tiles[c] = NewTile(c)).ToArray();
        Reconcile(Clients, desired);
        SelectedClient = Clients.FirstOrDefault(c => c.Identity == selectedId);
        if (!Monitors.SequenceEqual(engine.Monitors))
        {
            var device = SelectedMonitor?.DeviceName;
            Monitors.Clear();
            foreach (var screen in engine.Monitors) Monitors.Add(screen);
            SelectedMonitor = Monitors.FirstOrDefault(m => m.DeviceName == device) ?? Monitors.FirstOrDefault(m => !m.IsPrimary) ?? Monitors.FirstOrDefault();
        }
        if (MainClient is { } main && !Clients.Contains(main)) MainClient = null;
        SavedCount = engine.SavedWindowCount;
        CurrentGrid = engine.LastGrid is { } grid ? $"{grid.Layout} on {grid.MonitorDevice.Replace(@"\\.\", "")}" : "No grid arranged";
        if (engine.DetectedAt is { } detected) LastRefresh = $"Updated {detected.ToLocalTime():HH:mm:ss}";
        RaiseResources();
        Raise(nameof(ClientCount)); Raise(nameof(TotalMemory)); RaiseSelection(); TrackRoster();
        NotifyCommands();
    }

    private ClientTileViewModel NewTile(EngineClient client)
    {
        var tile = new ClientTileViewModel(client, (t, ignored) => _ = engine.SetIgnoredAsync(t.Client, ignored));
        // A new name changes the lists and header counts; while saved names are being restored the engine reports once at the end.
        tile.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ClientTileViewModel.Label) && !engine.Restoring && !disposed) RaiseSelection(); };
        return tile;
    }

    private void NotifyCommands() { foreach (var command in Commands) command?.Notify(); }

    private void RaiseResources()
    {
        Raise(nameof(ResourcePlanSummary)); Raise(nameof(HasResourceChanges));
        Raise(nameof(MemoryCapSummary)); ApplyMemoryCapCommand?.Notify(); ResetMemoryCapCommand?.Notify();
        ApplyResourcePlanCommand?.Notify(); ResetResourcePlanCommand?.Notify();
    }

    private void RaiseSelection()
    {
        Reconcile(VisibleClients, Clients.Where(CanManageProfile).ToArray());
        Reconcile(IgnoredClients, Clients.Where(c => c.IsIgnored).ToArray());
        Remote.Feed.Publish(VisibleClients.Select(c => new RobloxAfkManager.App.Remote.MonitoredClient(c.Snapshot, c.Label, c.GameName, c.SessionStatus,
            c.Session?.Phase == SessionPhase.Joined ? c.Session.JoinedAt : null)), Clients.Where(c => !CanManageProfile(c)).Select(c => c.Identity), engine.DetectedAt);
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
        var screen = SelectedMonitor!;
        var mainScreen = Monitors.FirstOrDefault(m => m.IsPrimary) ?? screen;
        return engine.ArrangeAsync(new(cols, rows), screen, MainClient?.Client, mainScreen);
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
        engine.Dispose();
        disposed = true; monitor?.Dispose();
        Remote.Dispose();
    }
}
