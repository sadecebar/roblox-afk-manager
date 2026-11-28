using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace RobloxAfkManager.Engine;


// Starts Roblox as a specific account using Roblox's own one-time authentication ticket flow. The web calls and the launch
// sequence live here; finding, closing and starting the Roblox processes is the platform's job (IRobloxClients).
public sealed class AccountLauncher(IRobloxClients clients, Func<CancellationToken, Task<string?>>? acceptedVersion = null) : IAccountLauncher, IDisposable
{
    private static readonly HttpClient Http = CreateHttp();
    // Identifies itself plainly as this tool: no browser or Roblox-client disguise.
    private static HttpClient CreateHttp()
    {
        var client = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RobloxAfkManager/0.11 (open-source account manager)");
        return client;
    }

    // Starts one account and returns the new client's process id. Only "Roblox never started" is an error.
    public async Task<int> LaunchSafelyAsync(string cookie, long placeId, CancellationToken token, string? readyTicket = null, string? linkCode = null)
    {
        clients.CloseIdleBackgroundClients();
        var before = clients.ProcessIds();
        // Running clients must not hear the new client's start signal, so close their singleton event first.
        if (before.Length > 0) clients.ReleaseSingleton();
        await LaunchAsync(cookie, placeId, token, readyTicket, linkCode);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        int[] fresh = [];
        while (DateTime.UtcNow < deadline && fresh.Length == 0)
        { await Task.Delay(300, token); fresh = clients.ProcessIds().Except(before).ToArray(); }
        // The new client creates its own event a moment after it starts; close it too so the next launch is quiet.
        for (int i = 0; i < 20 && fresh.Length > 0; i++) { if (clients.ReleaseSingleton() > 0) break; await Task.Delay(250, token); }
        const string Blocked = "Roblox didn't open a new window. A Roblox client that was already running before the manager started holds the single-instance lock. Close all Roblox windows, then open the accounts from the manager.";
        if (fresh.Length == 0) throw new InvalidOperationException(before.Length > 0 ? Blocked : "Roblox didn't start within a minute.");
        if (before.Length > 0)
        {
            // A second client that Roblox rejects starts and exits again within seconds.
            await Task.Delay(8000, token);
            if (!clients.ProcessIds().Contains(fresh[0])) throw new InvalidOperationException(Blocked);
        }
        return fresh[0];
    }

    public static string? CleanCookie(string? raw)
    {
        string value = (raw ?? "").Trim().Trim('"');
        int mark = value.IndexOf(".ROBLOSECURITY=", StringComparison.OrdinalIgnoreCase);
        if (mark >= 0) value = value[(mark + 15)..];
        int end = value.IndexOf(';'); if (end >= 0) value = value[..end];
        value = value.Trim();
        return value.Length is > 100 and < 4000 && value.All(c => c > 32 && c < 127 && c != ';' && c != ',') ? value : null;
    }

    public async Task<SavedLogin> IdentifyAsync(string cookie, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://users.roblox.com/v1/users/authenticated");
        request.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
        using var response = await Http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Roblox rejected that session. The cookie may be expired or copied wrong.");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return new(json.RootElement.GetProperty("id").GetInt64(), json.RootElement.GetProperty("name").GetString() ?? "Account");
    }

    public Task<string> PrepareAsync(string cookie, long placeId, CancellationToken token) => TicketAsync(cookie, placeId, token);

    public async Task LaunchAsync(string cookie, long placeId, CancellationToken token, string? readyTicket = null, string? linkCode = null)
    {
        string ticket = readyTicket ?? await TicketAsync(cookie, placeId, token);
        string tracker = Random.Shared.NextInt64(100_000_000_000, 130_000_000_000).ToString();
        // The link code is the one Roblox itself returned for the share link (letters, digits, dashes), never text typed by the person.
        if (linkCode is not null && !linkCode.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')) throw new InvalidOperationException("Roblox returned an unexpected private server code.");
        string request = linkCode is null ? "RequestGame" : "RequestPrivateGame";
        string privatePart = linkCode is null ? "" : $"&linkCode={linkCode}";
        string launcher = Uri.EscapeDataString($"https://assetgame.roblox.com/game/PlaceLauncher.ashx?request={request}&browserTrackerId={tracker}&placeId={placeId}{privatePart}&isPlayTogetherGame=false");
        string uri = $"roblox-player:1+launchmode:play+gameinfo:{ticket}+launchtime:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}+placelauncherurl:{launcher}+browsertrackerid:{tracker}+robloxLocale:en_us+gameLocale:en_us+channel:+LaunchExp:InApp";
        clients.Start(clients.ClientExecutable(await (acceptedVersion ?? AcceptedVersionAsync)(token)), uri);
    }

    // The client version Roblox's servers currently accept (a folder name such as "version-2c6fd5e09aaf4cfa"), or null when unknown.
    private static async Task<string?> AcceptedVersionAsync(CancellationToken token)
    {
        try
        {
            using var response = await Http.GetAsync("https://clientsettingscdn.roblox.com/v2/client-version/WindowsPlayer", token);
            if (!response.IsSuccessStatusCode) return null;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            string? guid = json.RootElement.GetProperty("clientVersionUpload").GetString();
            return guid is { Length: > 0 } && guid.All(c => char.IsLetterOrDigit(c) || c == '-') ? guid : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    public async Task<PrivateServer> ResolveShareLinkAsync(string cookie, string shareCode, CancellationToken token)
    {
        if (shareCode.Length != 32 || !shareCode.All(Uri.IsHexDigit)) throw new ArgumentException("That is not a private server share code.");
        string? csrf = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://apis.roblox.com/sharelinks/v1/resolve-link")
            { Content = new StringContent(JsonSerializer.Serialize(new { linkId = shareCode, linkType = "Server" }), Encoding.UTF8, "application/json") };
            request.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
            if (csrf is not null) request.Headers.TryAddWithoutValidation("x-csrf-token", csrf);
            using var response = await Http.SendAsync(request, token);
            if (response.StatusCode == HttpStatusCode.Forbidden && csrf is null && response.Headers.TryGetValues("x-csrf-token", out var tokens)) { csrf = tokens.First(); continue; }
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw new InvalidOperationException("The saved session has expired. Add this account again.");
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Roblox would not open the private server link ({(int)response.StatusCode}). It may have been reset or deleted.");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (ReadPrivateServer(json.RootElement) is { } server) return server;
            throw new InvalidOperationException("That private server link is no longer valid. Copy a fresh link from the server's page.");
        }
        throw new InvalidOperationException("Roblox did not accept the private server link request.");
    }

    // The answer nests the data under privateServerInviteData; only a "Valid" link with a place and a code is accepted.
    public static PrivateServer? ReadPrivateServer(JsonElement root)
    {
        if (!root.TryGetProperty("privateServerInviteData", out var data) || data.ValueKind != JsonValueKind.Object) return null;
        if (data.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String && !string.Equals(status.GetString(), "Valid", StringComparison.OrdinalIgnoreCase)) return null;
        if (!data.TryGetProperty("placeId", out var place) || !place.TryGetInt64(out long placeId) || placeId <= 0) return null;
        if (!data.TryGetProperty("linkCode", out var code) || code.ValueKind != JsonValueKind.String || code.GetString() is not { Length: > 0 and <= 128 } linkCode) return null;
        return new(placeId, linkCode);
    }

    private static async Task<string> TicketAsync(string cookie, long placeId, CancellationToken token)
    {
        string? csrf = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://auth.roblox.com/v1/authentication-ticket") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            request.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
            request.Headers.TryAddWithoutValidation("Referer", $"https://www.roblox.com/games/{placeId}");
            if (csrf is not null) request.Headers.TryAddWithoutValidation("x-csrf-token", csrf);
            using var response = await Http.SendAsync(request, token);
            if (response.StatusCode == HttpStatusCode.Forbidden && response.Headers.TryGetValues("x-csrf-token", out var tokens)) { csrf = tokens.First(); continue; }
            if (response.Headers.TryGetValues("rbx-authentication-ticket", out var ticket) && ticket.First() is { Length: > 0 } value) return value;
            throw new InvalidOperationException(response.StatusCode == HttpStatusCode.Unauthorized ? "The saved session has expired. Add this account again." : $"Roblox did not give a launch ticket ({(int)response.StatusCode}).");
        }
        throw new InvalidOperationException("Roblox did not accept the launch request.");
    }

    public void Dispose() { }
}
