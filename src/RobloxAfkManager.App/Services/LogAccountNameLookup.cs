using System.IO;
using System.Text;
using RobloxAfkManager.Core;

namespace RobloxAfkManager.App.Services;

// Runs serially in the background, at most once per 30-second discovery interval.
public sealed class LogAccountNameLookup : IAccountDetailsLookup
{
    private sealed record LogBinding(ProcessIdentity Process, long UserId, DateTimeOffset CheckedAt, SessionReport Session);
    private readonly string directory;
    private readonly Func<string, IReadOnlyList<ProcessIdentity>> owners;
    private readonly Func<long[], CancellationToken, Task<IReadOnlyDictionary<long, string>>> usernames;
    private readonly Dictionary<string, DateTimeOffset> checkedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LogBinding> bindings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, (string Name, DateTimeOffset At)> cache = [];
    private readonly PublicAccountArtwork artwork = new();

    public async Task<IReadOnlyDictionary<ProcessIdentity, AccountDetails>> ResolveDetailsAsync(IReadOnlyList<ProcessIdentity> processes, CancellationToken cancellationToken)
    {
        var names = await ResolveAsync(processes, cancellationToken).ConfigureAwait(false);
        var sessions = bindings.Values.GroupBy(b => b.Process).Where(g => g.Select(b => b.UserId).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(b => b.Session.EventAt).First().Session);
        await artwork.RefreshAsync(sessions.Values, cancellationToken).ConfigureAwait(false);
        return sessions.ToDictionary(p => p.Key, p => new AccountDetails(p.Value, names.GetValueOrDefault(p.Key),
            artwork.Game(p.Value.UniverseId), artwork.Avatar(p.Value.UserId)));
    }

    public LogAccountNameLookup(string? directory = null, Func<string, IReadOnlyList<ProcessIdentity>>? owners = null,
        Func<long[], CancellationToken, Task<IReadOnlyDictionary<long, string>>>? usernames = null)
    {
        this.directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "logs");
        this.owners = owners ?? LogFileOwners.Read;
        this.usernames = usernames ?? PublicRobloxUsers.LookupAsync;
    }

    public async Task<IReadOnlyDictionary<ProcessIdentity, string>> ResolveAsync(IReadOnlyList<ProcessIdentity> processes, CancellationToken cancellationToken)
    {
        var live = processes.Take(128).ToHashSet();
        var ids = await Task.Run(() => FindUserIds(live, cancellationToken), cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        foreach (var id in cache.Keys.Where(id => !ids.Values.Contains(id) || now - cache[id].At > TimeSpan.FromHours(1)).ToArray()) cache.Remove(id);
        var missing = ids.Values.Distinct().Where(id => !cache.ContainsKey(id)).Take(100).ToArray();
        if (missing.Length > 0)
        {
            try
            {
                var result = await usernames(missing, cancellationToken).ConfigureAwait(false);
                foreach (var pair in result)
                    if (missing.Contains(pair.Key) && AccountNames.Normalize(pair.Value) is { } name) cache[pair.Key] = (name, now);
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or System.Text.Json.JsonException or OperationCanceledException or ArgumentException)
            { cancellationToken.ThrowIfCancellationRequested(); /* Offline/rate limited: leave unresolved names blank. */ }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return ids.Where(p => cache.ContainsKey(p.Value)).ToDictionary(p => p.Key, p => cache[p.Value].Name);
    }

    // HTTP 401s are counted as the log grows instead of from a small slice of it, so a slow trickle and a heavy burst
    // are both measured over the same ten minutes. Each file keeps the position it was read up to.
    private sealed class AuthLog { public long Offset = -1; public readonly Queue<DateTimeOffset> Seen = new(), Active = new(); }
    // Lines that show real activity (not the heartbeat) in the last ten minutes, kept up to date by CountAuthFailures.
    public int RecentActivity(string path, DateTimeOffset now) => authLogs.TryGetValue(path, out var log) ? log.Active.Count(t => now - t <= TimeSpan.FromMinutes(10)) : -1;
    private readonly Dictionary<string, AuthLog> authLogs = [];
    private const int AuthReadLimit = 4 * 1024 * 1024;

    public int CountAuthFailures(string path, ProcessIdentity process, DateTimeOffset now)
    {
        if (!authLogs.TryGetValue(path, out var log)) authLogs[path] = log = new();
        var start = new DateTimeOffset(new DateTime(process.StartTimeUtcTicks, DateTimeKind.Utc));
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long length = stream.Length;
        bool skipPartial = false;
        if (log.Offset < 0 || log.Offset > length || length - log.Offset > AuthReadLimit)
        { log.Offset = Math.Max(0, length - AuthReadLimit); skipPartial = log.Offset > 0; }
        int count = (int)Math.Min(length - log.Offset, AuthReadLimit);
        if (count <= 0) { while (log.Active.Count > 0 && now - log.Active.Peek() > TimeSpan.FromMinutes(10)) log.Active.Dequeue(); while (log.Seen.Count > 0 && now - log.Seen.Peek() > TimeSpan.FromMinutes(10)) log.Seen.Dequeue(); return log.Seen.Count; }
        var buffer = new byte[count];
        stream.Seek(log.Offset, SeekOrigin.Begin);
        int total = 0, read;
        while (total < count && (read = stream.Read(buffer, total, count - total)) > 0) total += read;
        int end = Array.LastIndexOf(buffer, (byte)'\n', Math.Max(0, total - 1));
        if (end < 0) end = skipPartial ? total : -1; // no complete line yet: wait for more
        if (end >= 0)
        {
            int from = 0;
            if (skipPartial) { int first = Array.IndexOf(buffer, (byte)'\n', 0, Math.Max(0, end)); from = first < 0 ? end : first + 1; }
            foreach (var line in Encoding.UTF8.GetString(buffer, from, Math.Max(0, end - from)).Split('\n'))
            {
                if (SessionLogParser.TryReadAuthFailure(line, out var at) && at >= start.AddSeconds(-2) && at <= now.AddMinutes(1)) log.Seen.Enqueue(at);
                else if (SessionLogParser.TryReadActivity(line, out var active) && active >= start.AddSeconds(-2) && active <= now.AddMinutes(1)) log.Active.Enqueue(active);
            }
            log.Offset += end + 1;
        }
        while (log.Active.Count > 0 && now - log.Active.Peek() > TimeSpan.FromMinutes(10)) log.Active.Dequeue();
        while (log.Seen.Count > 0 && now - log.Seen.Peek() > TimeSpan.FromMinutes(10)) log.Seen.Dequeue();
        return log.Seen.Count;
    }

    private Dictionary<ProcessIdentity, long> FindUserIds(HashSet<ProcessIdentity> live, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (live.Count == 0) { bindings.Clear(); checkedAt.Clear(); return []; }
        foreach (var path in bindings.Where(p => !live.Contains(p.Value.Process) || now - p.Value.CheckedAt > TimeSpan.FromMinutes(5)).Select(p => p.Key).ToArray()) bindings.Remove(path);
        var directories = new[] { directory }.Where(Directory.Exists).ToArray();
        if (directories.Length == 0) { bindings.Clear(); return []; }
        FileInfo[] files;
        try
        {
            var earliest = new DateTime(live.Min(p => p.StartTimeUtcTicks), DateTimeKind.Utc);
            files = directories.SelectMany(d => new DirectoryInfo(d).EnumerateFiles("*Player*.log"))
                .Where(f => f.LastWriteTimeUtc >= earliest && (f.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(64).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        var paths = files.Select(f => f.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in checkedAt.Keys.Where(p => !paths.Contains(p)).ToArray()) { checkedAt.Remove(path); bindings.Remove(path); }
        foreach (var path in authLogs.Keys.Where(p => !paths.Contains(p)).ToArray()) authLogs.Remove(path);
        foreach (var file in files.OrderBy(f => checkedAt.GetValueOrDefault(f.FullName)).Take(16))
        {
            cancellationToken.ThrowIfCancellationRequested();
            checkedAt[file.FullName] = now;
            bindings.Remove(file.FullName);
            try
            {
                // A filename timestamp alone is never accepted as a process/account match.
                var matches = owners(file.FullName).Where(live.Contains).Distinct().ToArray();
                if (matches.Length != 1) continue;
                string text = ReadBoundedLog(file.FullName);
                if (AccountNames.ReadLocalUserId(text) is { } userId)
                    bindings[file.FullName] = new(matches[0], userId, now, SessionLogParser.Read(text, matches[0], now) with { UserId = userId, AuthFailures = CountAuthFailures(file.FullName, matches[0], now), RecentActivity = RecentActivity(file.FullName, now) });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException or System.Text.RegularExpressions.RegexMatchTimeoutException)
            { /* No raw log contents, names, URLs or file paths are logged. */ }
        }
        return bindings.Values.GroupBy(b => b.Process).Where(g => g.Select(b => b.UserId).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().UserId);
    }

    private static string ReadBoundedLog(string path)
    {
        const int limit = 128 * 1024;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[limit];
        string ReadPart()
        {
            int total = 0, read;
            while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0) total += read;
            return Encoding.UTF8.GetString(buffer, 0, total);
        }
        string head = ReadPart();
        if (stream.Length <= limit) return head;
        stream.Seek(Math.Max(limit, stream.Length - limit), SeekOrigin.Begin);
        return head + "\n" + ReadPart();
    }
}
