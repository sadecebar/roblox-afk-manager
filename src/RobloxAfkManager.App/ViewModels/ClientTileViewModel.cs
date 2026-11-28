using RobloxAfkManager.Core;

namespace RobloxAfkManager.App.ViewModels;

// One card on the dashboard: how the window shows an engine client. All the facts come from EngineClient; this only turns them into
// text, an avatar image and a tone, and passes the Ignore checkbox back to the engine.
public sealed class ClientTileViewModel : ObservableObject
{
    private readonly Action<ClientTileViewModel, bool> requestIgnore;
    private ClientWindow shownWindow;
    private int previewRevision;
    private DateTimeOffset clock = DateTimeOffset.UtcNow;
    private string shownLabel;

    public ClientTileViewModel(EngineClient client, Action<ClientTileViewModel, bool> requestIgnore)
    {
        Client = client; this.requestIgnore = requestIgnore; shownWindow = client.Window; shownLabel = client.Label;
        client.Changed += _ => Refresh();
    }

    // A card for a client no engine manages (previews, design views and tests).
    public ClientTileViewModel(ClientWindow window, int slot) : this(EngineClient.Detached(window, slot), (_, _) => { }) { }

    public EngineClient Client { get; }
    // The card's notion of "now" for durations and the "log too old" check; moved forward on every change.
    public void UpdateSessionClock(DateTimeOffset now) { clock = now; foreach (var name in Properties) Raise(name); }
    public long? AccountId => Client.AccountId;
    public SessionReport? Session => Client.Session;
    // The avatar as PNG bytes; the window turns them into an image (AvatarImageConverter). Null until known.
    public byte[]? AvatarPng => Client.Account?.AvatarPng;
    public string AvatarFallback => Label.Length > 0 ? Label[..1].ToUpperInvariant() : "?";
    public string ProfileHint => AccountId is { } id ? $"Account ID {id}. Name, order, ignore choice and preferred plan are remembered across restarts." : "Waiting for an account ID. Names and choices currently apply to this process only.";
    public string? KnownGameName => Client.KnownGameName;
    public string GameName => Client.KnownGameName ?? (Session?.PlaceId is { } place ? $"Place {place}" : "Game not identified");

    // ---- status: the engine decides, the card words it ----
    private ClientStatus State => Client.StatusAt(clock);
    public string SessionStatus => State.Session switch
    {
        SessionState.NotResponding => "Not responding",
        SessionState.Joined => "Joined (log)",
        SessionState.Disconnected => "Disconnected (log)",
        SessionState.StuckJoining => "Possibly stuck joining",
        SessionState.Joining => "Joining (log)",
        _ => "Session unknown",
    };
    public string SessionDuration
    {
        get
        {
            if (Session?.JoinedAt is not { } joined || Session.Phase != SessionPhase.Joined) return "";
            var elapsed = clock - joined;
            return elapsed.TotalMinutes < 1 ? "<1m" : elapsed.TotalHours < 1 ? $"{(int)elapsed.TotalMinutes}m" : $"{(int)elapsed.TotalHours}h {elapsed.Minutes:00}m";
        }
    }
    public string SessionSummary => GameName + (SessionDuration.Length > 0 ? " · " + SessionDuration : "");
    public string SessionHint => $"{GameName}\n{SessionStatus}" + (Session?.JoinedAt is { } joined ? $"\nLast join report: {joined.ToLocalTime():g}" : "") +
        "\nLog evidence is best-effort; a join report does not prove the game is currently healthy or farming.";
    // Short card headline: window problems first, then log-reported session state.
    public string HeadlineStatus => State.Window switch
    {
        ClientWindowState.Minimized => "Minimized",
        ClientWindowState.OtherDesktop => "Other desktop",
        _ => SessionStatus,
    };
    // Good: joined per log. Pending: joining. Alert: needs a look. Warn: window out of view. Idle: unknown.
    public string StatusTone => State.Tone.ToString();
    public bool SessionNeedsAttention => State.SessionNeedsAttention;
    public bool NeedsAttention => State.NeedsAttention;
    public bool CanPreview => State.CanPreview;

    // ---- names ----
    public string? CustomName => Client.CustomName;
    public string? DetectedName => Client.DetectedName;
    public string Label => Client.Label;
    public string NameHint => CustomName is not null ? "Custom account label. Use the pencil to edit it or return to automatic naming." :
        DetectedName is not null ? "Username found for this client's account. Use the pencil to set your own label." :
        "Account name not found yet. Use the pencil to enter it yourself.";
    public string ClientDetails => $"Client {Slot} · PID {ProcessId}";

    // ---- resources ----
    public string ResourceStatus => Client.ResourceStatus;
    public string PlanLabel => ResourceStatus.StartsWith("Low applied", StringComparison.Ordinal) ? "Low" :
        ResourceStatus.StartsWith("Balanced applied", StringComparison.Ordinal) ? "Balanced" :
        ResourceStatus.StartsWith("High applied", StringComparison.Ordinal) ? "High" :
        ResourceStatus.StartsWith("Original settings", StringComparison.Ordinal) ? "Default" : "Review plan";

    // ---- the window ----
    public WindowIdentity Identity => Client.Identity;
    public int ProcessId => Client.ProcessId;
    public string Title => Client.Window.Title;
    public string Slot => $"{Client.Slot:00}";
    public string Memory => Client.Window.MemoryBytes is long bytes ? $"{bytes / 1048576d:N0} MB" : "Unavailable";
    public string Cpu => Client.Window.CpuUsagePercent is double percent ? $"{percent:N1}%" : "—";
    public double CpuMeter => Client.Window.CpuUsagePercent ?? 0;
    public string Position => Client.Window.Bounds.ToString();
    public string Status => Client.Status;
    public ClientWindow Snapshot => Client.Window;
    public PixelRect WindowBounds => Client.WindowBounds;
    public int PreviewRevision => previewRevision;

    // The Ignore checkbox: a request to the engine, which may refuse (busy, or resources could not be restored).
    public bool IsIgnored { get => Client.IsIgnored; set { if (value != Client.IsIgnored) requestIgnore(this, value); } }
    public string ManagementStatus => IsIgnored ? "Ignored · bulk actions leave it alone" : "Click preview to bring forward";

    // Stages account details on this card's client (tests and design views). The engine sets them from the Roblox log.
    public void SetAccountDetails(AccountDetails details) => Client.SetAccountDetails(details);

    private void Refresh()
    {
        clock = DateTimeOffset.UtcNow;
        if (!ReferenceEquals(shownWindow, Client.Window)) { shownWindow = Client.Window; previewRevision++; }
        foreach (var name in Properties) Raise(name);
        // Label is raised only when it really changed: the dashboard recounts its lists when a card's name changes.
        if (shownLabel != Client.Label) { shownLabel = Client.Label; Raise(nameof(Label)); }
    }
    private static readonly string[] Properties =
    [
        nameof(AccountId), nameof(Session), nameof(AvatarPng), nameof(ProfileHint), nameof(GameName), nameof(KnownGameName), nameof(SessionStatus), nameof(SessionDuration),
        nameof(SessionSummary), nameof(SessionHint), nameof(HeadlineStatus), nameof(StatusTone), nameof(NeedsAttention), nameof(SessionNeedsAttention),
        nameof(CustomName), nameof(DetectedName), nameof(NameHint), nameof(AvatarFallback), nameof(ClientDetails),
        nameof(ResourceStatus), nameof(PlanLabel), nameof(Identity), nameof(Title), nameof(Slot), nameof(Memory), nameof(Cpu), nameof(CpuMeter),
        nameof(Position), nameof(Status), nameof(WindowBounds), nameof(CanPreview), nameof(PreviewRevision), nameof(IsIgnored), nameof(ManagementStatus),
    ];
}
