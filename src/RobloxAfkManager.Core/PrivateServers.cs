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

// Which private server an account joins: the first N accounts (by their place in the dashboard order) go to server 1, the rest to server 2.
public static class ServerAssignment
{
    public const int DefaultFirstCount = 6;
    // position is 1-based. Accounts the manager has no position for yet (int.MaxValue) go to server 2.
    public static int ServerFor(int position, int firstCount) => position >= 1 && position <= firstCount ? 1 : 2;

    // The 1-based position of each account: by the saved dashboard order, then by id so it never depends on who is running.
    public static IReadOnlyDictionary<long, int> Positions(IEnumerable<long> accounts, IReadOnlyDictionary<long, int> order)
    {
        var sorted = accounts.Distinct().OrderBy(id => order.TryGetValue(id, out var o) ? o : int.MaxValue).ThenBy(id => id).ToArray();
        var result = new Dictionary<long, int>();
        for (int i = 0; i < sorted.Length; i++) result[sorted[i]] = i + 1;
        return result;
    }
}
