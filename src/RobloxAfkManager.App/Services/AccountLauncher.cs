using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace RobloxAfkManager.App.Services;


// Starts Roblox as a specific account using Roblox's own one-time authentication ticket flow.
public sealed class AccountLauncher : IAccountLauncher, IDisposable
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
        CloseIdleBackgroundClients();
        var before = RobloxProcessIds();
        // Running clients must not hear the new client's start signal, so close their singleton event first.
        if (before.Length > 0) RobloxSingleton.Release();
        await LaunchAsync(cookie, placeId, token, readyTicket, linkCode);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        int[] fresh = [];
        while (DateTime.UtcNow < deadline && fresh.Length == 0)
        { await Task.Delay(300, token); fresh = RobloxProcessIds().Except(before).ToArray(); }
        // The new client creates its own event a moment after it starts; close it too so the next launch is quiet.
        for (int i = 0; i < 20 && fresh.Length > 0; i++) { if (RobloxSingleton.Release() > 0) break; await Task.Delay(250, token); }
        const string Blocked = "Roblox didn't open a new window. A Roblox client that was already running before the manager started holds the single-instance lock. Close all Roblox windows, then open the accounts from the manager.";
        if (fresh.Length == 0) throw new InvalidOperationException(before.Length > 0 ? Blocked : "Roblox didn't start within a minute.");
        if (before.Length > 0)
        {
            // A second client that Roblox rejects starts and exits again within seconds.
            await Task.Delay(8000, token);
            if (!RobloxProcessIds().Contains(fresh[0])) throw new InvalidOperationException(Blocked);
        }
        return fresh[0];
    }

    // Roblox leaves a hidden background process behind every time a client exits (started with --launch-to-tray). It has no window and
    // holds almost no memory. A client started afterwards tries to hand its launch to it, fails ("SingleInstanceGuard", error 1400) and
    // exits at once, which is the "didn't open a new window" failure. They are closed before each launch. Anything with a window,
    // anything big enough to be a running game, and anything started in the last minute (a client still opening) is left alone.
    private static readonly TimeSpan BackgroundClientAge = TimeSpan.FromSeconds(45);
    private const long BackgroundClientMemory = 400L * 1024 * 1024;

    public static int CloseIdleBackgroundClients()
    {
        int closed = 0;
        foreach (var process in Process.GetProcessesByName("RobloxPlayerBeta"))
            using (process)
            {
                try
                {
                    if (process.MainWindowHandle != IntPtr.Zero || process.WorkingSet64 >= BackgroundClientMemory || DateTime.Now - process.StartTime < BackgroundClientAge) continue;
                    process.Kill(); closed++;
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { /* it is already gone, or not ours to close */ }
            }
        return closed;
    }

    private static int[] RobloxProcessIds()
    {
        var ids = new List<int>();
        foreach (var process in Process.GetProcessesByName("RobloxPlayerBeta")) using (process) ids.Add(process.Id);
        return [.. ids];
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
        // Never go through the roblox-player: protocol handler. That is RobloxPlayerInstaller.exe, which treats the
        // launch as an install/update and terminates every running RobloxPlayerBeta.exe. Start the client itself.
        var info = new ProcessStartInfo(await ClientExecutableAsync(token)) { UseShellExecute = false };
        info.ArgumentList.Add(uri);
        Process.Start(info)?.Dispose();
    }

    // The version Roblox's servers currently accept, then a version that is already running, then the newest on disk.
    public static async Task<string> ClientExecutableAsync(CancellationToken token)
    {
        string versions = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "Versions");
        try
        {
            using var response = await Http.GetAsync("https://clientsettingscdn.roblox.com/v2/client-version/WindowsPlayer", token);
            if (response.IsSuccessStatusCode)
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                string? guid = json.RootElement.GetProperty("clientVersionUpload").GetString();
                if (guid is { Length: > 0 } && guid.All(c => char.IsLetterOrDigit(c) || c == '-') && Path.Combine(versions, guid, "RobloxPlayerBeta.exe") is { } current && File.Exists(current)) return current;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException) { }
        foreach (var process in Process.GetProcessesByName("RobloxPlayerBeta"))
            using (process) { try { if (process.MainModule?.FileName is { } running && File.Exists(running)) return running; } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { } }
        var newest = Directory.Exists(versions)
            ? new DirectoryInfo(versions).EnumerateDirectories().Select(d => Path.Combine(d.FullName, "RobloxPlayerBeta.exe")).Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
        return newest ?? throw new InvalidOperationException("Roblox isn't installed for this Windows user. Install it from roblox.com once, then try again.");
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
