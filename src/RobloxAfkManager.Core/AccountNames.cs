using System.Globalization;
using System.Text.RegularExpressions;

namespace RobloxAfkManager.Core;

public interface IAccountNameLookup
{
    Task<IReadOnlyDictionary<ProcessIdentity, string>> ResolveAsync(IReadOnlyList<ProcessIdentity> processes, CancellationToken cancellationToken);
}

public interface IClientNameStore
{
    string? Get(ProcessIdentity process);
    void Set(ProcessIdentity process, string? name);
}

public sealed class MemoryClientNameStore : IClientNameStore
{
    private readonly Dictionary<ProcessIdentity, string> names = [];
    public string? Get(ProcessIdentity process) => names.GetValueOrDefault(process);
    public void Set(ProcessIdentity process, string? name)
    {
        if (AccountNames.Normalize(name) is { } value) names[process] = value;
        else names.Remove(process);
    }
}

public static partial class AccountNames
{
    public static string? Normalize(string? name)
    {
        var value = name?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Length > 64 || value.Any(c => char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format))
            throw new ArgumentException("Use up to 64 characters without line breaks or hidden formatting.");
        return value;
    }

    // Only the local join-load report identifies this client's user. Other user IDs in a log may be other players.
    public static long? ReadLocalUserId(string text)
    {
        long? result = null;
        foreach (Match match in LocalUserPattern().Matches(text))
            if (long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long id) && id > 0) result = id;
        return result;
    }

    [GeneratedRegex(@"(?m)^[^\r\n\[]*\[FLog::GameJoinLoadTime\]\s+Report game_join_loadtime:[^\r\n]*?\buserid:\s*([1-9][0-9]{0,18})(?=\s|,|$)", RegexOptions.CultureInvariant, 100)]
    private static partial Regex LocalUserPattern();
}
