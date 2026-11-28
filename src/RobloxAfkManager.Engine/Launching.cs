namespace RobloxAfkManager.Engine;

// What the engine needs from whatever starts Roblox. The Windows implementation is AccountLauncher.
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
