using RobloxAfkManager.Core;

namespace RobloxAfkManager.Engine;

// What the log says about a client's game session.
public enum SessionState { Unknown, Joining, StuckJoining, Joined, Disconnected, NotResponding }
// What Windows says about the client's window.
public enum ClientWindowState { Normal, Minimized, OtherDesktop, NotResponding }
// How a window should colour the client: Good = joined, Pending = joining, Warn = window out of view, Alert = needs a look, Idle = unknown.
public enum ClientTone { Idle, Good, Pending, Warn, Alert }

// A client's status as plain data. A window turns it into words and colours however it likes.
public readonly record struct ClientStatus(SessionState Session, ClientWindowState Window, ClientTone Tone)
{
    // A session problem (disconnected, stuck joining) or a window problem (not responding, minimized, other desktop).
    public bool NeedsAttention => SessionNeedsAttention || Window is ClientWindowState.Minimized or ClientWindowState.NotResponding or ClientWindowState.OtherDesktop;
    public bool SessionNeedsAttention => Session is SessionState.Disconnected or SessionState.StuckJoining;
    // A preview can only be shown of a window that is on screen.
    public bool CanPreview => Window is not (ClientWindowState.Minimized or ClientWindowState.OtherDesktop);

    // The log is only trusted while it is recent; a joining client that has not joined after two minutes is probably stuck.
    public static ClientStatus Of(string windowStatus, SessionReport? session, DateTimeOffset now)
    {
        var window = windowStatus switch
        {
            "Not responding" => ClientWindowState.NotResponding,
            "Minimized externally" => ClientWindowState.Minimized,
            "Other desktop / cloaked" => ClientWindowState.OtherDesktop,
            _ => ClientWindowState.Normal,
        };
        var state = window == ClientWindowState.NotResponding ? SessionState.NotResponding
            : session is null || now - session.CheckedAt > TimeSpan.FromMinutes(3) ? SessionState.Unknown
            : session.Phase switch
            {
                SessionPhase.Joined => SessionState.Joined,
                SessionPhase.Disconnected => SessionState.Disconnected,
                SessionPhase.Joining when now - session.EventAt > TimeSpan.FromMinutes(2) => SessionState.StuckJoining,
                SessionPhase.Joining => SessionState.Joining,
                _ => SessionState.Unknown,
            };
        var tone = window == ClientWindowState.NotResponding || state is SessionState.Disconnected or SessionState.StuckJoining ? ClientTone.Alert
            : window is ClientWindowState.Minimized or ClientWindowState.OtherDesktop ? ClientTone.Warn
            : state == SessionState.Joined ? ClientTone.Good : state == SessionState.Joining ? ClientTone.Pending : ClientTone.Idle;
        return new(state, window, tone);
    }
}

// One detected Roblox client, owned by the engine. A window shows it; it changes it only through AfkEngine.
public sealed class EngineClient : IClientView
{
    internal EngineClient(ClientWindow window, int slot) { Window = window; Slot = slot; }
    // A client that no engine manages, for previews, design views and tests.
    public static EngineClient Detached(ClientWindow window, int slot) => new(window, slot);

    public ClientWindow Window { get; private set; }
    // The card number shown as "Client 01" until the account is known. Never reused while the manager runs.
    public int Slot { get; }
    public AccountDetails? Account { get; private set; }
    public string? CustomName { get; private set; }
    public string? DetectedName { get; private set; }
    public bool IsIgnored { get; internal set; }
    // The resource plan and memory limit applied to this client, in words.
    public string ResourceStatus { get; internal set; } = "Original settings";

    public WindowIdentity Identity => Window.Identity;
    public ProcessIdentity Process => ProcessIdentity.From(Window.Identity);
    public int ProcessId => Identity.ProcessId;
    public string Status => Window.Status;
    public PixelRect WindowBounds => Window.Bounds;
    public long? AccountId => Account?.Session.UserId;
    public SessionReport? Session => Account?.Session;
    public string? KnownGameName => Account?.GameName;
    public string Label => CustomName ?? DetectedName ?? $"Client {Slot:00}";
    public ClientStatus StatusAt(DateTimeOffset now) => ClientStatus.Of(Status, Session, now);

    // Raised whenever anything about this client changed.
    public event Action<EngineClient>? Changed;
    internal void RaiseChanged() => Changed?.Invoke(this);

    internal void Update(ClientWindow window) { Window = window; RaiseChanged(); }
    // Public so a test or a design-time view can stage a client; the engine sets it from the Roblox log.
    public void SetAccountDetails(AccountDetails details) { Account = details; DetectedName = AccountNames.Normalize(details.Username); RaiseChanged(); }
    internal void SetDetectedName(string? name) { DetectedName = AccountNames.Normalize(name); RaiseChanged(); }
    internal void SetCustomName(string? name) { CustomName = AccountNames.Normalize(name); RaiseChanged(); }
}

// Questions the engine needs a person to answer. A window shows them; without one the engine proceeds.
public interface IUserPrompts
{
    // "Close N accounts, wait a minute and rejoin?" before Reset servers closes anything.
    bool ConfirmResetServers(int count);
}
