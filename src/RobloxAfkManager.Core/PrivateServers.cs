using System.Text.RegularExpressions;

namespace RobloxAfkManager.Core;

// A private server is shared as https://www.roblox.com/share?code=<32 hex>&type=Server. Only the code matters.
public static partial class PrivateServerLink
{
    // Accepts the whole share link or just the code. Anything else (a game link, a wrong link type) is rejected.
    public static string? ParseCode(string? text)
    {
        string value = (text ?? "").Trim();
        if (value.Length == 0 || value.Length > 600) return null;
        if (BareCode().IsMatch(value)) return value.ToLowerInvariant();
        var link = ShareLink().Match(value);
        if (!link.Success) return null;
        string query = link.Groups[1].Value;
        string? code = null, type = null;
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = part.IndexOf('=');
            if (equals <= 0) continue;
            string key = part[..equals], val = part[(equals + 1)..];
            if (key.Equals("code", StringComparison.OrdinalIgnoreCase)) code = val;
            else if (key.Equals("type", StringComparison.OrdinalIgnoreCase)) type = val;
        }
        return code is not null && BareCode().IsMatch(code) && type is not null && type.Equals("Server", StringComparison.OrdinalIgnoreCase) ? code.ToLowerInvariant() : null;
    }

    [GeneratedRegex(@"^[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant, 200)]
    private static partial Regex BareCode();
    [GeneratedRegex(@"^https?://(?:www\.)?roblox\.com/share\?([^#\s]+)(?:#.*)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 200)]
    private static partial Regex ShareLink();
}

// Which private server an account joins. Each account is given a server once and keeps it, so restarting, reopening or
// reordering the cards never moves an account and the two servers stay at exactly the sizes the person chose.
public static class ServerAssignment
{
    public const int DefaultFirstCount = 6;

    // order: every saved account, in the order the dashboard shows them. existing: servers already given (1 or 2).
    // With nothing given yet, the first firstCount accounts get server 1 and the rest server 2. Accounts added later
    // fill server 1 up to firstCount, then go to server 2. Accounts that are no longer in order are dropped.
    public static IReadOnlyDictionary<long, int> Assign(IReadOnlyList<long> order, int firstCount, IReadOnlyDictionary<long, int>? existing)
    {
        var result = new Dictionary<long, int>();
        var ids = order.Distinct().ToArray();
        if (existing is not null) foreach (var id in ids) if (existing.TryGetValue(id, out var n) && n is 1 or 2) result[id] = n;
        if (result.Count == 0)
        {
            for (int i = 0; i < ids.Length; i++) result[ids[i]] = i < firstCount ? 1 : 2;
            return result;
        }
        int onFirst = result.Values.Count(v => v == 1);
        foreach (var id in ids)
        {
            if (result.ContainsKey(id)) continue;
            if (onFirst < firstCount) { result[id] = 1; onFirst++; } else result[id] = 2;
        }
        return result;
    }
}
