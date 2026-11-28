namespace RobloxAfkManager.Engine;

// What the engine needs from whatever starts Roblox. AccountLauncher implements it on top of IRobloxClients.
public sealed record SavedLogin(long UserId, string Username);

// A private server as the Roblox client needs to join it.
public sealed record PrivateServer(long PlaceId, string LinkCode);

public interface IAccountLauncher
{
    Task<SavedLogin> IdentifyAsync(string cookie, CancellationToken token);
    Task<string> PrepareAsync(string cookie, long placeId, CancellationToken token);
    // linkCode joins that private server of the place instead of a public server.
    Task<int> LaunchSafelyAsync(string cookie, long placeId, CancellationToken token, string? readyTicket = null, string? linkCode = null);
    // Turns the code of a roblox.com/share?code=…&type=Server link into the place and link code to join. Any saved account can ask.
    Task<PrivateServer> ResolveShareLinkAsync(string cookie, string shareCode, CancellationToken token);
}

// The Roblox client processes on this computer. Implemented by Platform.Windows (WindowsRobloxClients).
public interface IRobloxClients
{
    // Process ids of every running Roblox client.
    int[] ProcessIds();
    // Closes Roblox's idle background processes that would swallow the next launch. Returns how many were closed.
    int CloseIdleBackgroundClients();
    // Closes the single-instance event handle inside running clients so a new one doesn't make them leave. Returns how many.
    int ReleaseSingleton();
    // The client executable to start: the version the servers accept if installed, else a running one, else the newest.
    string ClientExecutable(string? acceptedVersion);
    // Starts the client with a roblox-player launch argument.
    void Start(string executable, string launchArgument);
}
