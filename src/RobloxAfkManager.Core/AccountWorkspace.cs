using System.Globalization;
using System.Text.RegularExpressions;

namespace RobloxAfkManager.Core;

public enum SessionPhase { Unknown, Joining, Joined, Disconnected }
public sealed record SessionReport(long? UserId, long? PlaceId, long? UniverseId, SessionPhase Phase,
    DateTimeOffset? JoinedAt, DateTimeOffset? EventAt, DateTimeOffset CheckedAt);
public sealed record AccountDetails(SessionReport Session, string? Username, string? GameName, byte[]? AvatarPng);
public interface IAccountDetailsLookup : IAccountNameLookup
{
    Task<IReadOnlyDictionary<ProcessIdentity, AccountDetails>> ResolveDetailsAsync(IReadOnlyList<ProcessIdentity> processes, CancellationToken cancellationToken);
}

// Username, LastGame and LastSeen let the dashboard list recently used accounts that are no longer running.
public sealed record AccountProfile(long UserId, string? CustomName, int Order, bool Ignored, ResourcePlan? PreferredPlan,
    string? Username = null, string? LastGame = null, DateTimeOffset? LastSeen = null);
public sealed record OfflineAccount(long UserId, string Name, string? Game, DateTimeOffset LastSeen);
public static class AccountRoster
{
    // Accounts seen running within this window are expected back; older ones are assumed retired.
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);
    public static IReadOnlyList<OfflineAccount> Offline(IEnumerable<AccountProfile> profiles, IReadOnlySet<long> running, DateTimeOffset now) =>
        profiles.Where(p => !p.Ignored && p.LastSeen is { } seen && !running.Contains(p.UserId) && now - seen <= Window && seen <= now.AddMinutes(5))
            .OrderBy(p => p.Order).ThenBy(p => p.UserId)
            .Select(p => new OfflineAccount(p.UserId, p.CustomName ?? p.Username ?? $"Account {p.UserId}", p.LastGame, p.LastSeen!.Value))
            .ToArray();
    public static string Ago(DateTimeOffset seen, DateTimeOffset now)
    {
        var elapsed = now - seen;
        return elapsed.TotalMinutes < 1 ? "just now" : elapsed.TotalHours < 1 ? $"{(int)elapsed.TotalMinutes} min ago" : $"{(int)elapsed.TotalHours} h {elapsed.Minutes:00} min ago";
    }
}
public sealed record ActivityEntry(DateTimeOffset At, string Label, string Message, bool Warning)
{
    public string Time => At.ToLocalTime().ToString("g");
}
public sealed record WorkspaceSettings(IReadOnlyList<AccountProfile> Profiles, IReadOnlyList<ActivityEntry> History, bool Notifications = true);
public interface IWorkspaceStore
{
    WorkspaceSettings Read();
    void Write(WorkspaceSettings settings);
}
public sealed class MemoryWorkspaceStore : IWorkspaceStore
{
    private WorkspaceSettings settings = new([], []);
    public WorkspaceSettings Read() => settings;
    public void Write(WorkspaceSettings value) => settings = value;
}

// Log formats are not a stable Roblox API. Only explicit, timestamped engine markers are accepted.
// Text emitted by a game through FLog::Output is never treated as session evidence.
public static partial class SessionLogParser
{
    public static SessionReport Read(string text, ProcessIdentity process, DateTimeOffset checkedAt)
    {
        long? user = null, place = null, universe = null;
        DateTimeOffset? joined = null, last = null;
        SessionPhase phase = SessionPhase.Unknown;
        var start = new DateTimeOffset(new DateTime(process.StartTimeUtcTicks, DateTimeKind.Utc));
        foreach (var line in text.Split('\n'))
        {
            var marker = Marker().Match(line);
            if (!marker.Success || !DateTimeOffset.TryParse(marker.Groups[1].Value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) || at < start.AddSeconds(-2) || at > checkedAt.AddMinutes(1)) continue;
            if (last is { } previous && at < previous) continue;
            string tag = marker.Groups[2].Value, message = marker.Groups[3].Value.Trim();
            if (tag == "GameJoinLoadTime" && message.StartsWith("Report game_join_loadtime:", StringComparison.Ordinal))
            {
                long? ReadId(string key)
                {
                    foreach (Match m in Fields().Matches(message))
                        if (m.Groups[1].Value == key && long.TryParse(m.Groups[2].Value, out long id) && id > 0) return id;
                    return null;
                }
                if (ReadId("userid") is not { } id) continue;
                user = id; place = ReadId("placeid"); universe = ReadId("universeid");
                phase = SessionPhase.Joined; joined = last = at;
            }
            else if (tag == "Network" && (message.StartsWith("Client:Disconnect", StringComparison.Ordinal) || message.StartsWith("Connection lost:", StringComparison.Ordinal)))
            { phase = SessionPhase.Disconnected; last = at; }
            else if (tag == "GameJoinUtil" && message.StartsWith("GameJoinUtil::joinGamePostStandard:", StringComparison.Ordinal))
            { phase = SessionPhase.Joining; joined = null; place = universe = null; last = at; }
        }
        return new(user, place, universe, phase, joined, last, checkedAt);
    }
    [GeneratedRegex(@"^\s*(\d{4}-\d{2}-\d{2}T[^,\s]+),[^\r\n\[]*\[FLog::(GameJoinLoadTime|Network|GameJoinUtil)\]\s*(.*)$", RegexOptions.CultureInvariant, 100)]
    private static partial Regex Marker();
    [GeneratedRegex(@"\b(userid|placeid|universeid):\s*([1-9][0-9]{0,18})(?=\s|,|$)", RegexOptions.CultureInvariant, 100)]
    private static partial Regex Fields();
}

public sealed record ClientHealth(ProcessIdentity Process, string Label, bool Ignored, string WindowStatus, SessionReport? Session);

// Transition-only alerts, with grace periods and a bounded history owned by the view model.
public sealed class SessionHealthTracker
{
    private sealed class State(ClientHealth client)
    {
        public ClientHealth Client = client;
        public DateTimeOffset? MissingSince, HungSince;
        public bool HungAlerted;
        public DateTimeOffset? LastDisconnect, LastJoinWarning;
    }
    private readonly Dictionary<ProcessIdentity, State> states = [];
    public IReadOnlyList<ActivityEntry> Observe(IEnumerable<ClientHealth> clients, DateTimeOffset now)
    {
        var result = new List<ActivityEntry>();
        var snapshot = clients.Take(128).ToArray();
        var live = snapshot.Select(c => c.Process).ToHashSet();
        foreach (var client in snapshot)
        {
            if (client.Ignored) { states.Remove(client.Process); continue; }
            if (!states.TryGetValue(client.Process, out var state)) states[client.Process] = state = new(client);
            state.Client = client; state.MissingSince = null;
            if (client.WindowStatus == "Not responding")
            {
                state.HungSince ??= now;
                if (!state.HungAlerted && now - state.HungSince >= TimeSpan.FromSeconds(20))
                { result.Add(new(now, client.Label, "Window has not responded for at least 20 seconds.", true)); state.HungAlerted = true; }
            }
            else
            {
                if (state.HungAlerted) result.Add(new(now, client.Label, "Window is responding again.", false));
                state.HungSince = null; state.HungAlerted = false;
            }
            if (client.Session is { } session && now - session.CheckedAt < TimeSpan.FromMinutes(3))
            {
                if (session.Phase == SessionPhase.Disconnected && session.EventAt is { } disconnected && state.LastDisconnect != disconnected)
                { state.LastDisconnect = disconnected; result.Add(new(now, client.Label, "Roblox's log reports a disconnect. Check the client.", true)); }
                if (session.Phase == SessionPhase.Joined && state.LastDisconnect is { } lost && session.JoinedAt > lost)
                { state.LastDisconnect = null; result.Add(new(now, client.Label, "A new game join was reported by Roblox.", false)); }
                if (session.Phase == SessionPhase.Joining && session.EventAt is { } joining && now - joining > TimeSpan.FromMinutes(2) && state.LastJoinWarning != joining)
                { state.LastJoinWarning = joining; result.Add(new(now, client.Label, "Possibly stuck joining: no completed join report after two minutes.", true)); }
            }
        }
        foreach (var pair in states.Where(p => !live.Contains(p.Key)).ToArray())
        {
            pair.Value.MissingSince ??= now;
            if (now - pair.Value.MissingSince < TimeSpan.FromSeconds(15)) continue;
            result.Add(new(now, pair.Value.Client.Label, "Window closed or is no longer detected.", true));
            states.Remove(pair.Key);
        }
        return result;
    }
}
