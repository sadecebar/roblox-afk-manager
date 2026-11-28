using RobloxAfkManager.Core;

namespace RobloxAfkManager.Engine;

// One running Roblox client as the engine sees it: no WPF, no display text beyond its label.
public interface IClientView
{
    WindowIdentity Identity { get; }
    int ProcessId { get; }
    long? AccountId { get; }
    SessionReport? Session { get; }
    bool IsIgnored { get; }
    string Label { get; }
    // The window's own state, for example "Not responding".
    string Status { get; }
    PixelRect WindowBounds { get; }
    // The Roblox username and game name read for this client (null until known).
    string? DetectedName { get; }
    string? KnownGameName { get; }
}

// What the engine's services use from AfkEngine (the client list, status messages, closing a window). Internal: a window
// never implements it; it talks to AfkEngine instead.
internal interface IEngineHost
{
    // Every detected client, in dashboard order.
    IReadOnlyList<IClientView> Clients { get; }
    // Accounts that have a running, identified client (as of the last refresh).
    IReadOnlySet<long> RunningAccounts { get; }
    // The saved card position of an account, for the server split ("dashboard order"). Null when it has no profile.
    int? ProfileOrder(long userId);
    // Every saved account profile (names, last seen), for the status board.
    IReadOnlyCollection<AccountProfile> Profiles { get; }
    // The name shown for an account (its card label, saved username or a fallback).
    string NameOf(long userId);
    // True while the dashboard is refreshing or the manager is closing; automatic fixes wait.
    bool Busy { get; }
    bool Disposed { get; }
    // True while another automatic fix (login restart, reopen) is running.
    bool OtherFixRunning { get; }

    void SetStatus(string message, bool warning);
    void AddActivity(string label, string message, bool warning, bool announce = true, bool info = false, bool quiet = false);
    // Saved logins, server or launch state changed: refresh what depends on it.
    void LaunchStateChanged();
    OperationResult CloseClient(WindowIdentity identity);
    // Reads the account of windows that are not identified yet, so they are not mistaken for closed accounts.
    Task SettleAccountsAsync();
}
