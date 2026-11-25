using RobloxAfkManager.Core;
using System.Windows.Media;
using RobloxAfkManager.App.Services;

namespace RobloxAfkManager.App.ViewModels;

public sealed class ClientTileViewModel(ClientWindow client, int slot) : ObservableObject
{
    private ClientWindow client = client;
    private readonly int slot = slot;
    private bool isIgnored;
    private int previewRevision;
    private string? customName, detectedName;
    private AccountDetails? account;
    private byte[]? avatarBytes;
    private DateTimeOffset clock = DateTimeOffset.UtcNow;
    public long? AccountId => account?.Session.UserId;
    public SessionReport? Session => account?.Session;
    public ImageSource? Avatar { get; private set; }
    public string AvatarFallback => Label.Length > 0 ? Label[..1].ToUpperInvariant() : "?";
    public string ProfileHint => AccountId is { } id ? $"Account ID {id}. Name, order, ignore choice and preferred plan are remembered across restarts." : "Waiting for an account ID. Names and choices currently apply to this process only.";
    public string? KnownGameName => account?.GameName;
    public string GameName => account?.GameName ?? (Session?.PlaceId is { } place ? $"Place {place}" : "Game not identified");
    public string SessionStatus => Status == "Not responding" ? "Not responding" : Session is null || clock - Session.CheckedAt > TimeSpan.FromMinutes(3) ? "Session unknown" : Session.Phase switch
    {
        SessionPhase.Joined when IsQuiet => "Quiet, may be stuck",
        SessionPhase.Joined => "Joined (log)",
        SessionPhase.Disconnected => "Disconnected (log)",
        SessionPhase.Joining when clock - Session.EventAt > TimeSpan.FromMinutes(2) => "Possibly stuck joining",
        SessionPhase.Joining => "Joining (log)",
        _ => "Session unknown"
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
    public string HeadlineStatus => Status switch
    {
        "Minimized externally" => "Minimized",
        "Other desktop / cloaked" => "Other desktop",
        _ => SessionStatus
    };
    // Good: joined per log. Pending: joining. Alert: needs a look. Idle: unknown.
    public string StatusTone => Status == "Not responding" || SessionNeedsAttention ? "Alert" :
        Status is "Minimized externally" or "Other desktop / cloaked" ? "Warn" :
        SessionStatus == "Joined (log)" ? "Good" : SessionStatus == "Joining (log)" ? "Pending" : "Idle";
    public bool IsQuiet => SessionLogParser.IsQuiet(Session, clock);
    public bool SessionNeedsAttention => SessionStatus is "Disconnected (log)" or "Possibly stuck joining" or "Quiet, may be stuck";
    public void SetAccountDetails(AccountDetails details)
    {
        account = details;
        SetDetectedName(details.Username);
        if (!ReferenceEquals(avatarBytes, details.AvatarPng)) { avatarBytes = details.AvatarPng; Avatar = PublicAccountArtwork.Decode(avatarBytes); Raise(nameof(Avatar)); }
        Raise(nameof(AccountId)); Raise(nameof(ProfileHint)); UpdateSessionClock(DateTimeOffset.UtcNow);
    }
    public void UpdateSessionClock(DateTimeOffset now)
    {
        clock = now; Raise(nameof(GameName)); Raise(nameof(SessionStatus)); Raise(nameof(SessionDuration)); Raise(nameof(SessionSummary)); Raise(nameof(SessionHint)); Raise(nameof(NeedsAttention)); Raise(nameof(HeadlineStatus)); Raise(nameof(StatusTone));
    }
    public string? CustomName => customName;
    public string? DetectedName => detectedName;
    public string Label => customName ?? detectedName ?? $"Client {Slot}";
    public string NameHint => customName is not null ? "Custom account label. Use the pencil to edit it or return to automatic naming." :
        detectedName is not null ? "Username found for this client's account. Use the pencil to set your own label." :
        "Account name not found yet. Use the pencil to enter it yourself.";
    public string ClientDetails => $"Client {Slot} · PID {ProcessId}";
    public void SetCustomName(string? name) { if (Set(ref customName, AccountNames.Normalize(name), nameof(CustomName))) RaiseName(); }
    public void SetDetectedName(string? name) { if (Set(ref detectedName, AccountNames.Normalize(name), nameof(DetectedName))) RaiseName(); }
    private void RaiseName() { Raise(nameof(Label)); Raise(nameof(NameHint)); Raise(nameof(AvatarFallback)); }
    private string resourceStatus = "Original settings";
    public string ResourceStatus { get => resourceStatus; set { if (Set(ref resourceStatus, value)) Raise(nameof(PlanLabel)); } }
    public string PlanLabel => resourceStatus.StartsWith("Low applied", StringComparison.Ordinal) ? "Low" :
        resourceStatus.StartsWith("Balanced applied", StringComparison.Ordinal) ? "Balanced" :
        resourceStatus.StartsWith("High applied", StringComparison.Ordinal) ? "High" :
        resourceStatus.StartsWith("Original settings", StringComparison.Ordinal) ? "Default" : "Review plan";
    public WindowIdentity Identity => client.Identity;
    public int ProcessId => Identity.ProcessId;
    public string Title => client.Title;
    public string Slot => $"{slot:00}";
    public string Memory => client.MemoryBytes is long bytes ? $"{bytes / 1048576d:N0} MB" : "Unavailable";
    public string Cpu => client.CpuUsagePercent is double percent ? $"{percent:N1}%" : "—";
    public double CpuMeter => client.CpuUsagePercent ?? 0;
    public string Position => client.Bounds.ToString();
    public string Status => client.Status;
    public bool NeedsAttention => SessionNeedsAttention || Status is "Minimized externally" or "Not responding" or "Other desktop / cloaked";
    public ClientWindow Snapshot => client;
    public PixelRect WindowBounds => client.Bounds;
    public int PreviewRevision => previewRevision;
    public bool CanPreview => Status is not ("Minimized externally" or "Other desktop / cloaked");
    public bool IsIgnored { get => isIgnored; set { if (Set(ref isIgnored, value)) Raise(nameof(ManagementStatus)); } }
    public string ManagementStatus => IsIgnored ? "Ignored · bulk actions leave it alone" : "Click preview to bring forward";
    public void Update(ClientWindow update)
    {
        bool identityChanged = client.Identity != update.Identity;
        client = update;
        UpdateSessionClock(DateTimeOffset.UtcNow);
        if (identityChanged) { Raise(nameof(Identity)); Raise(nameof(ClientDetails)); }
        previewRevision++;
        Raise(nameof(Title)); Raise(nameof(Slot)); Raise(nameof(Memory)); Raise(nameof(Cpu)); Raise(nameof(CpuMeter)); Raise(nameof(Position)); Raise(nameof(Status)); Raise(nameof(NeedsAttention)); Raise(nameof(HeadlineStatus)); Raise(nameof(StatusTone)); Raise(nameof(WindowBounds)); Raise(nameof(CanPreview)); Raise(nameof(PreviewRevision));
    }
}
