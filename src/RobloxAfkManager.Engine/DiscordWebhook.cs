using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RobloxAfkManager.Core;

namespace RobloxAfkManager.Engine;

// Sends account alerts to a Discord channel through a webhook the user creates. Off until a webhook is saved.
// The webhook address is a secret (anyone with it can post to the channel), so it is stored encrypted for the
// current Windows user, sent only to Discord, and never written to the log.
public sealed partial class DiscordWebhook : IDisposable
{
    private static readonly Regex UrlPattern = new(@"^https://(?:(?:ptb|canary)\.)?(?:discord|discordapp)\.com/api/webhooks/\d{15,25}/[A-Za-z0-9_\-]{20,120}$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
    private static readonly Regex UserPattern = new(@"^\d{15,25}$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
    private const int MaxLinesPerMessage = 20, MaxMessagesPerHour = 30, MaxQueued = 100;

    private sealed record Stored(string Cipher, string? UserId);
    private readonly string path, folder;
    private readonly TimeSpan boardInterval;
    private readonly HttpClient http;
    private readonly bool ownsClient;
    private readonly TimeSpan batchDelay;
    private readonly IAppLog? log;
    private readonly object gate = new();
    private readonly List<(ActivityEntry Entry, AlertKind Kind)> queue = [];
    private readonly Queue<DateTimeOffset> sentLog = new();
    private string? url, boardUrl;
    private Task? flushing;
    private bool disposed;

    public string? UserId { get; private set; }
    public bool Configured => url is not null;
    // Plain-language result of the last send, for the settings panel. Never contains the address.
    public string LastResult { get; private set; } = "";

    private readonly ISecretProtector secrets;
    public DiscordWebhook(string? folder = null, HttpMessageHandler? handler = null, TimeSpan? batchDelay = null, IAppLog? log = null, TimeSpan? boardInterval = null, ISecretProtector? protector = null)
    {
        secrets = SecretProtection.Resolve(protector);
        this.folder = folder ?? AppFolders.Data;
        path = Path.Combine(this.folder, "webhook.dat");
        this.boardInterval = boardInterval ?? TimeSpan.FromSeconds(10);
        ownsClient = handler is null;
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RobloxAfkManager (alerts)");
        this.batchDelay = batchDelay ?? TimeSpan.FromSeconds(5);
        this.log = log;
        Load(); LoadBoardWebhook(); LoadBoard();
    }

    public static bool IsValidUrl(string? value) => value is not null && UrlPattern.IsMatch(value.Trim());
    public static bool IsValidUserId(string? value) => value is not null && UserPattern.IsMatch(value.Trim());

    private void Load()
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 16384) return;
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path));
            if (stored is null) return;
            string plain = Encoding.UTF8.GetString(secrets.Unprotect(Convert.FromBase64String(stored.Cipher)));
            if (!IsValidUrl(plain)) return;
            url = plain; UserId = IsValidUserId(stored.UserId) ? stored.UserId : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or SecretProtectionException) { }
    }

    public void Save(string webhookUrl, string? userId)
    {
        string clean = (webhookUrl ?? "").Trim();
        if (!IsValidUrl(clean)) throw new ArgumentException("That doesn't look like a Discord webhook address. Copy it from the channel's Integrations → Webhooks page.");
        string? id = string.IsNullOrWhiteSpace(userId) ? null : userId.Trim();
        if (id is not null && !IsValidUserId(id)) throw new ArgumentException("A Discord user ID is only digits (turn on Developer Mode, then right-click yourself → Copy User ID).");
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temporary = full + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Stored(Convert.ToBase64String(secrets.Protect(Encoding.UTF8.GetBytes(clean))), id)));
            File.Move(temporary, full, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        string? oldBoardTarget = BoardTarget;
        url = clean; UserId = id; LastResult = "";
        if (boardUrl is null && oldBoardTarget != clean) RehomeBoard(oldBoardTarget);
    }

    // Changes only the user ID, keeping the saved webhook.
    public void SetUserId(string? userId)
    {
        if (url is null) throw new InvalidOperationException("Save a webhook first.");
        Save(url, userId);
    }

    public void Clear()
    {
        url = null; UserId = null; LastResult = "";
        lock (gate) queue.Clear();
        if (boardUrl is null) ForgetBoard();
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // What an alert is about. Problems are posted (and ping). Recoveries edit the message that reported the problem
    // instead of posting again. Info is a plain message, such as "manager started".
    // Notice is a problem the manager is already fixing: posted in red and turned green when fixed, but it never pings.
    public enum AlertKind { Problem, Notice, Recovery, Info }
    private enum LineState { Down, Back, Info }
    private sealed class Line(string label, string message, DateTimeOffset at, LineState state, bool quiet = false)
    {
        public readonly bool Quiet = quiet;
        public readonly string Label = label, Message = message;
        public readonly DateTimeOffset At = at;
        public DateTimeOffset BackAt;
        public LineState State = state;
    }
    private sealed class Posted(string id, List<Line> lines, string? content) { public readonly string Id = id; public readonly List<Line> Lines = lines; public readonly string? Content = content; }
    // The newest message that reported each account as down, so a later "back" can edit it. Kept in memory only.
    private readonly Dictionary<string, (Posted Message, Line Line)> open = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<DateTimeOffset> editLog = new();
    private const int MaxEditsPerHour = 120;

    public void Enqueue(ActivityEntry entry, bool bad) => Enqueue(entry, bad ? AlertKind.Problem : AlertKind.Info);

    // Alerts that arrive close together are handled as one batch, so a PC-wide problem is one ping.
    public void Enqueue(ActivityEntry entry, AlertKind kind)
    {
        if (url is null || disposed) return;
        lock (gate)
        {
            if (queue.Count >= MaxQueued) return;
            queue.Add((entry, kind));
            if (flushing is { IsCompleted: false }) return;
            flushing = Task.Run(async () => { await Task.Delay(batchDelay).ConfigureAwait(false); await FlushAsync().ConfigureAwait(false); });
        }
    }

    public async Task FlushAsync()
    {
        (ActivityEntry Entry, AlertKind Kind)[] batch;
        lock (gate) { batch = [.. queue]; queue.Clear(); }
        if (batch.Length == 0 || url is null) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var stale in open.Where(p => now - p.Value.Line.At > TimeSpan.FromHours(24)).Select(p => p.Key).ToArray()) open.Remove(stale);

        var fresh = new List<Line>(); var freshByLabel = new Dictionary<string, Line>(StringComparer.OrdinalIgnoreCase);
        var infos = new List<Line>(); var edited = new HashSet<Posted>();
        foreach (var (entry, kind) in batch)
        {
            switch (kind)
            {
                case AlertKind.Problem:
                case AlertKind.Notice:
                    var line = new Line(entry.Label, entry.Message, entry.At, LineState.Down, kind == AlertKind.Notice); fresh.Add(line); freshByLabel[entry.Label] = line; break;
                case AlertKind.Recovery:
                    if (freshByLabel.TryGetValue(entry.Label, out var pending) && pending.State == LineState.Down) { pending.State = LineState.Back; pending.BackAt = entry.At; }
                    else if (open.TryGetValue(entry.Label, out var posted) && posted.Line.State == LineState.Down) { posted.Line.State = LineState.Back; posted.Line.BackAt = entry.At; edited.Add(posted.Message); open.Remove(entry.Label); }
                    break; // a recovery nobody was told about needs no message
                default:
                    infos.Add(new Line(entry.Label, entry.Message, entry.At, LineState.Info)); break;
            }
        }

        if (fresh.Count > 0 && TakeMessageSlot(now))
        {
            string? content = UserId is not null && fresh.Any(l => l.State == LineState.Down && !l.Quiet) ? $"<@{UserId}>" : null;
            var (ok, body) = await SendAsync(HttpMethod.Post, url + "?wait=true", BuildPayload(fresh, content, UserId is not null && content is not null)).ConfigureAwait(false);
            if (ok && MessageId(body) is { } id)
            {
                var message = new Posted(id, fresh, content);
                foreach (var l in fresh.Where(l => l.State == LineState.Down)) open[l.Label] = (message, l);
            }
        }
        if (infos.Count > 0 && TakeMessageSlot(now)) await SendAsync(HttpMethod.Post, url + "?wait=true", BuildPayload(infos, null, false)).ConfigureAwait(false);
        foreach (var message in edited)
        {
            lock (gate)
            {
                while (editLog.Count > 0 && now - editLog.Peek() > TimeSpan.FromHours(1)) editLog.Dequeue();
                if (editLog.Count >= MaxEditsPerHour) continue;
                editLog.Enqueue(now);
            }
            bool allBack = message.Lines.All(l => l.State != LineState.Down);
            await SendAsync(HttpMethod.Patch, url + "/messages/" + message.Id, BuildPayload(message.Lines, allBack ? "" : message.Content, false, edit: true)).ConfigureAwait(false);
        }
    }

    private bool TakeMessageSlot(DateTimeOffset now)
    {
        lock (gate)
        {
            while (sentLog.Count > 0 && now - sentLog.Peek() > TimeSpan.FromHours(1)) sentLog.Dequeue();
            if (sentLog.Count >= MaxMessagesPerHour) { LastResult = "Too many alerts this hour; some were not sent."; return false; }
            sentLog.Enqueue(now); return true;
        }
    }

    private static string? MessageId(string? body)
    {
        try { using var doc = JsonDocument.Parse(body ?? ""); var id = doc.RootElement.GetProperty("id").GetString(); return id is { Length: > 0 and <= 25 } && id.All(char.IsDigit) ? id : null; }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    public async Task<string> TestAsync()
    {
        if (url is null) return "No webhook saved.";
        var line = new Line("Test", "This is a test alert from Roblox AFK Manager. Real alerts look like this.", DateTimeOffset.UtcNow, LineState.Info);
        await SendAsync(HttpMethod.Post, url + "?wait=true", BuildPayload([line], null, false)).ConfigureAwait(false);
        return LastResult;
    }

    // The mention is the only thing that makes Discord notify a phone, so it is sent when a user ID is saved. Nothing
    // else can be mentioned: allowed_mentions lists only that user, so a name containing "@everyone" is plain text.
    // Edits never ping: Discord only notifies for a mention a message gains, and edits are sent with no mentions allowed.
    private string BuildPayload(IReadOnlyList<Line> all, string? content, bool ping, bool edit = false)
    {
        int down = all.Count(l => l.State == LineState.Down);
        var lines = all.Take(MaxLinesPerMessage).Select(l => l.State switch
        {
            LineState.Down when l.Quiet => $"🟠 **{Clip(l.Label, 60)}**: {Clip(l.Message, 200)} · <t:{l.At.ToUnixTimeSeconds()}:R>",
            LineState.Down => $"🔴 **{Clip(l.Label, 60)}**: {Clip(l.Message, 200)} · <t:{l.At.ToUnixTimeSeconds()}:R>",
            LineState.Back => $"🟢 **{Clip(l.Label, 60)}**: back at <t:{l.BackAt.ToUnixTimeSeconds()}:T> · <t:{l.BackAt.ToUnixTimeSeconds()}:R> (was: {Clip(l.Message, 80)})",
            _ => $"🟢 **{Clip(l.Label, 60)}**: {Clip(l.Message, 200)} · <t:{l.At.ToUnixTimeSeconds()}:R>",
        }).ToList();
        if (all.Count > MaxLinesPerMessage) lines.Add($"…and {all.Count - MaxLinesPerMessage} more.");
        bool allQuiet = down > 0 && all.Where(l => l.State == LineState.Down).All(l => l.Quiet);
        string title = allQuiet ? (down == 1 ? "An account had a problem, fixing it" : $"{down} accounts had a problem, fixing them")
            : down > 0 ? (down == 1 ? "An account needs attention" : $"{down} accounts need attention")
            : all.Any(l => l.State == LineState.Back) ? (all.Count == 1 ? "Account is back" : "All accounts are back") : "Account update";
        var payload = new Dictionary<string, object?>
        {
            ["content"] = content,
            ["allowed_mentions"] = new Dictionary<string, object?> { ["parse"] = Array.Empty<string>(), ["users"] = ping && UserId is not null ? new[] { UserId } : Array.Empty<string>() },
            ["embeds"] = new[] { new Dictionary<string, object?> { ["title"] = title, ["description"] = string.Join("\n", lines), ["color"] = allQuiet ? 0xD29922 : down > 0 ? 0xE5534B : 0x3FB950 } },
        };
        if (!edit) payload["username"] = "Roblox AFK Manager";
        return JsonSerializer.Serialize(payload);
    }

    private static string Clip(string text, int length)
    {
        var clean = Regex.Replace(text ?? "", @"[\r\n\t]+", " ", RegexOptions.None, TimeSpan.FromMilliseconds(200)).Replace("*", "").Replace("`", "").Replace("_", " ").Trim();
        return clean.Length <= length ? clean : clean[..(length - 1)] + "…";
    }

    private async Task<(bool Ok, string? Body)> SendAsync(HttpMethod method, string target, string? json)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(method, target) { Content = json is null ? null : new StringContent(json, Encoding.UTF8, "application/json") };
                using var response = await http.SendAsync(request).ConfigureAwait(false);
                lastStatus = response.StatusCode;
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (response.IsSuccessStatusCode) { LastResult = "Last alert sent."; return (true, body); }
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 0)
                {
                    double wait = 2;
                    try { using var doc = JsonDocument.Parse(body); if (doc.RootElement.TryGetProperty("retry_after", out var r)) wait = Math.Clamp(r.GetDouble(), 0.5, 30); }
                    catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
                    await Task.Delay(TimeSpan.FromSeconds(wait)).ConfigureAwait(false);
                    continue;
                }
                // A message that was deleted in Discord can't be edited; that is not a problem with the webhook itself.
                if (method == HttpMethod.Patch && response.StatusCode == HttpStatusCode.NotFound) return (false, null);
                LastResult = response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? "Discord says this webhook no longer exists. Create a new one and save it again."
                    : $"Discord did not accept the alert ({(int)response.StatusCode}).";
                return (false, null);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastStatus = null;
                LastResult = "Couldn't reach Discord. Check the internet connection.";
                log?.Write("A Discord alert could not be sent (" + ex.GetType().Name + ").");
                return (false, null);
            }
        }
        return (false, null);
    }

    public void Dispose()
    {
        disposed = true;
        if (ownsClient) http.Dispose();
    }
}
