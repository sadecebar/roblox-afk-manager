using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Media.Imaging;
using RobloxAfkManager.Core;

namespace RobloxAfkManager.App.Services;

// Public metadata only; bounded memory caches, request sizes, response sizes and timeouts.
public sealed class PublicAccountArtwork
{
    private static readonly HttpClient Http = new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(4) };
    private readonly Dictionary<long, (string? Name, DateTimeOffset At)> games = [];
    private readonly Dictionary<long, (byte[]? Png, DateTimeOffset At)> avatars = [];
    public string? Game(long? id) => id is { } value ? games.GetValueOrDefault(value).Name : null;
    public byte[]? Avatar(long? id) => id is { } value ? avatars.GetValueOrDefault(value).Png : null;
    public async Task RefreshAsync(IEnumerable<SessionReport> reports, CancellationToken cancellation)
    {
        var now = DateTimeOffset.UtcNow;
        var sessions = reports.Take(128).ToArray();
        var userIds = sessions.Select(s => s.UserId).OfType<long>().Distinct().ToHashSet();
        var gameIds = sessions.Select(s => s.UniverseId).OfType<long>().Distinct().ToHashSet();
        foreach (long id in avatars.Keys.Where(id => !userIds.Contains(id) || now - avatars[id].At > (avatars[id].Png is null ? TimeSpan.FromMinutes(2) : TimeSpan.FromHours(1))).ToArray()) avatars.Remove(id);
        foreach (long id in games.Keys.Where(id => !gameIds.Contains(id) || now - games[id].At > (games[id].Name is null ? TimeSpan.FromMinutes(2) : TimeSpan.FromHours(1))).ToArray()) games.Remove(id);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        budget.CancelAfter(TimeSpan.FromSeconds(8));
        var token = budget.Token;
        try
        {
            long[] missingGames = gameIds.Where(id => !games.ContainsKey(id)).Take(50).ToArray();
            if (missingGames.Length > 0)
            {
                foreach (long id in missingGames) games[id] = (null, now);
                try
                {
                    using var json = JsonDocument.Parse(await GetAsync("https://games.roblox.com/v1/games?universeIds=" + string.Join(',', missingGames), 524288, token).ConfigureAwait(false));
                    if (json.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                        foreach (var item in data.EnumerateArray())
                            if (item.TryGetProperty("id", out var id) && id.TryGetInt64(out long key) && missingGames.Contains(key) &&
                                item.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 and <= 200 } value)
                                games[key] = (new string(value.Where(c => !char.IsControl(c)).ToArray()), now);
                }
                catch (Exception ex) when (Unavailable(ex)) { cancellation.ThrowIfCancellationRequested(); }
            }
            // At most six small images per pass. Later passes fill remaining placeholders.
            long[] missing = userIds.Where(id => !avatars.ContainsKey(id)).Take(6).ToArray();
            if (missing.Length == 0) return;
            foreach (long id in missing) avatars[id] = (null, now);
            using var thumbnails = JsonDocument.Parse(await GetAsync("https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds=" + string.Join(',', missing) + "&size=48x48&format=Png&isCircular=false", 65536, token).ConfigureAwait(false));
            if (!thumbnails.RootElement.TryGetProperty("data", out var images) || images.ValueKind != JsonValueKind.Array) return;
            foreach (var batch in images.EnumerateArray().Chunk(3))
            {
                var downloads = batch.Select(async item =>
                {
                    if (!item.TryGetProperty("targetId", out var target) || !target.TryGetInt64(out long id) || !missing.Contains(id) ||
                        !item.TryGetProperty("state", out var state) || state.GetString() != "Completed" ||
                        !item.TryGetProperty("imageUrl", out var url) || !IsAvatarUrl(url.GetString(), out var uri)) return (Id: 0L, Bytes: (byte[]?)null);
                    try
                    {
                        byte[] bytes = await GetAsync(uri!.AbsoluteUri, 131072, token).ConfigureAwait(false);
                        return (Id: id, Bytes: ValidPng(bytes) ? bytes : null);
                    }
                    catch (Exception ex) when (Unavailable(ex)) { return (Id: id, Bytes: (byte[]?)null); }
                });
                foreach (var result in await Task.WhenAll(downloads).ConfigureAwait(false))
                    if (result.Id > 0) avatars[result.Id] = (result.Bytes, now);
            }
        }
        catch (Exception ex) when (Unavailable(ex)) { cancellation.ThrowIfCancellationRequested(); }
    }
    private static bool Unavailable(Exception ex) => ex is HttpRequestException or IOException or JsonException or OperationCanceledException or InvalidOperationException or FormatException;
    public static bool IsAvatarUrl(string? value, out Uri? uri) => Uri.TryCreate(value, UriKind.Absolute, out uri) &&
        uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Host.EndsWith(".rbxcdn.com", StringComparison.OrdinalIgnoreCase);
    public static bool ValidPng(byte[] bytes) => bytes.Length is >= 24 and <= 131072 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
        System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)) is > 0 and <= 256 &&
        System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)) is > 0 and <= 256;
    public static BitmapSource? Decode(byte[]? bytes)
    {
        if (bytes is null || !ValidPng(bytes)) return null;
        try
        {
            using var stream = new MemoryStream(bytes, false);
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 48; image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or InvalidOperationException or FormatException or System.Runtime.InteropServices.COMException) { return null; }
    }
    private static async Task<byte[]> GetAsync(string url, int maximum, CancellationToken cancellation)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maximum) throw new IOException("Public response too large.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(4));
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
        { if (output.Length + count > maximum) throw new IOException("Public response too large."); output.Write(buffer, 0, count); }
        return output.ToArray();
    }
}
