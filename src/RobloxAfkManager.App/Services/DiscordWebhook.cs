using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RobloxAfkManager.Core;

namespace RobloxAfkManager.App.Services;

// Sends account alerts to a Discord channel through a webhook the user creates. Off until a webhook is saved.
// The webhook address is a secret (anyone with it can post to the channel), so it is stored encrypted for the
// current Windows user, sent only to Discord, and never written to the log.
public sealed class DiscordWebhook : IDisposable
{
    private static readonly Regex UrlPattern = new(@"^https://(?:(?:ptb|canary)\.)?(?:discord|discordapp)\.com/api/webhooks/\d{15,25}/[A-Za-z0-9_\-]{20,120}$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
    private static readonly Regex UserPattern = new(@"^\d{15,25}$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
    private const int MaxLinesPerMessage = 20, MaxMessagesPerHour = 30, MaxQueued = 100;

    private sealed record Stored(string Cipher, string? UserId);
    private readonly string path;
    private readonly HttpClient http;
    private readonly bool ownsClient;
    private readonly TimeSpan batchDelay;
    private readonly IAppLog? log;
    private readonly object gate = new();
    private readonly List<(ActivityEntry Entry, bool Bad)> queue = [];
    private readonly Queue<DateTimeOffset> sentLog = new();
    private string? url;
    private Task? flushing;
    private bool disposed;

    public string? UserId { get; private set; }
    public bool Configured => url is not null;
    // Plain-language result of the last send, for the settings panel. Never contains the address.
    public string LastResult { get; private set; } = "";

    public DiscordWebhook(string? folder = null, HttpMessageHandler? handler = null, TimeSpan? batchDelay = null, IAppLog? log = null)
    {
        path = Path.Combine(folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RobloxAfkManager"), "webhook.dat");
        ownsClient = handler is null;
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RobloxAfkManager (alerts)");
        this.batchDelay = batchDelay ?? TimeSpan.FromSeconds(5);
        this.log = log;
        Load();
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
            string plain = Encoding.UTF8.GetString(LoginVault.Unprotect(Convert.FromBase64String(stored.Cipher)));
            if (!IsValidUrl(plain)) return;
            url = plain; UserId = IsValidUserId(stored.UserId) ? stored.UserId : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or LoginVault.CryptographicException) { }
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
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Stored(Convert.ToBase64String(LoginVault.Protect(Encoding.UTF8.GetBytes(clean))), id)));
            File.Move(temporary, full, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        url = clean; UserId = id; LastResult = "";
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
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // Queues an alert. Alerts that arrive close together are sent as one message, so a PC-wide problem is one ping.
    public void Enqueue(ActivityEntry entry, bool bad)
    {
        if (url is null || disposed) return;
        lock (gate)
        {
            if (queue.Count >= MaxQueued) return;
            queue.Add((entry, bad));
            if (flushing is { IsCompleted: false }) return;
            flushing = Task.Run(async () => { await Task.Delay(batchDelay).ConfigureAwait(false); await FlushAsync().ConfigureAwait(false); });
        }
    }

    public async Task FlushAsync()
    {
        (ActivityEntry Entry, bool Bad)[] batch;
        lock (gate) { batch = [.. queue]; queue.Clear(); }
        if (batch.Length == 0 || url is null) return;
        var now = DateTimeOffset.UtcNow;
        lock (gate)
        {
            while (sentLog.Count > 0 && now - sentLog.Peek() > TimeSpan.FromHours(1)) sentLog.Dequeue();
            if (sentLog.Count >= MaxMessagesPerHour) { LastResult = "Too many alerts this hour; some were not sent."; return; }
            sentLog.Enqueue(now);
        }
        await PostAsync(BuildPayload(batch)).ConfigureAwait(false);
    }

    public async Task<string> TestAsync()
    {
        if (url is null) return "No webhook saved.";
        var entry = new ActivityEntry(DateTimeOffset.UtcNow, "Test", "This is a test alert from Roblox AFK Manager. Real alerts look like this.", false);
        await PostAsync(BuildPayload([(entry, false)])).ConfigureAwait(false);
        return LastResult;
    }

    // The mention is the only thing that makes Discord notify a phone, so it is sent when a user ID is saved. Nothing
    // else can be mentioned: allowed_mentions lists only that user, so a name containing "@everyone" is plain text.
    private string BuildPayload((ActivityEntry Entry, bool Bad)[] batch)
    {
        bool bad = batch.Any(b => b.Bad);
        var lines = batch.Take(MaxLinesPerMessage).Select(b =>
            $"{(b.Bad ? "🔴" : "🟢")} **{Clip(b.Entry.Label, 60)}**: {Clip(b.Entry.Message, 200)} · <t:{b.Entry.At.ToUnixTimeSeconds()}:R>").ToList();
        if (batch.Length > MaxLinesPerMessage) lines.Add($"…and {batch.Length - MaxLinesPerMessage} more.");
        string title = bad ? (batch.Count(b => b.Bad) == 1 ? "An account needs attention" : $"{batch.Count(b => b.Bad)} accounts need attention") : "Account update";
        var payload = new Dictionary<string, object?>
        {
            ["username"] = "Roblox AFK Manager",
            ["content"] = bad && UserId is not null ? $"<@{UserId}>" : null,
            ["allowed_mentions"] = new Dictionary<string, object?> { ["parse"] = Array.Empty<string>(), ["users"] = UserId is not null && bad ? new[] { UserId } : Array.Empty<string>() },
            ["embeds"] = new[] { new Dictionary<string, object?> { ["title"] = title, ["description"] = string.Join("\n", lines), ["color"] = bad ? 0xE5534B : 0x3FB950 } },
        };
        return JsonSerializer.Serialize(payload);
    }

    private static string Clip(string text, int length)
    {
        var clean = Regex.Replace(text ?? "", @"[\r\n\t]+", " ", RegexOptions.None, TimeSpan.FromMilliseconds(200)).Replace("*", "").Replace("`", "").Replace("_", " ").Trim();
        return clean.Length <= length ? clean : clean[..(length - 1)] + "…";
    }

    private async Task PostAsync(string json)
    {
        if (url is null) return;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                using var response = await http.SendAsync(request).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) { LastResult = "Last alert sent."; return; }
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 0)
                {
                    double wait = 2;
                    try { using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)); if (doc.RootElement.TryGetProperty("retry_after", out var r)) wait = Math.Clamp(r.GetDouble(), 0.5, 30); }
                    catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
                    await Task.Delay(TimeSpan.FromSeconds(wait)).ConfigureAwait(false);
                    continue;
                }
                LastResult = response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? "Discord says this webhook no longer exists. Create a new one and save it again."
                    : $"Discord did not accept the alert ({(int)response.StatusCode}).";
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                LastResult = "Couldn't reach Discord. Check the internet connection.";
                log?.Write("A Discord alert could not be sent (" + ex.GetType().Name + ").");
                return;
            }
        }
    }

    public void Dispose()
    {
        disposed = true;
        if (ownsClient) http.Dispose();
    }
}
