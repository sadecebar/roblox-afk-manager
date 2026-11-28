using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace RobloxAfkManager.Engine;

public static class PublicRobloxUsers
{
    // Public ID-to-username lookup only. No cookies, credentials, redirects or account login.
    private static readonly HttpClient Http = new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(6) };

    public static async Task<IReadOnlyDictionary<long, string>> LookupAsync(long[] ids, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://users.roblox.com/v1/users")
        { Content = JsonContent.Create(new { userIds = ids.Take(100).ToArray(), excludeBannedUsers = false }) };
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        byte[] buffer = new byte[65537];
        int count = 0, read;
        while (count < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(count), timeout.Token).ConfigureAwait(false)) > 0) count += read;
        if (count > 65536) throw new IOException("Public user response exceeds its size limit.");
        using var json = JsonDocument.Parse(buffer.AsMemory(0, count));
        var result = new Dictionary<long, string>();
        if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return result;
        foreach (var user in data.EnumerateArray())
            if (user.ValueKind == JsonValueKind.Object && user.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out long value) && ids.Contains(value) &&
                user.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 and <= 64 } username)
                result[value] = username;
        return result;
    }
}
