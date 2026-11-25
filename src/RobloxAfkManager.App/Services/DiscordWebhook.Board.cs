using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using RobloxAfkManager.Core;

namespace RobloxAfkManager.App.Services;

public enum BoardState { Ok, Warn, Down, Idle }
// Detail is text made by the manager itself and may contain Discord timestamp markup such as <t:1700000000:R>.
public sealed record BoardRow(string Name, BoardState State, string Detail);

// One live status message: every account on one line, edited in place whenever something changes, instead of a stream
// of new messages. The message id is remembered so the same message is picked up again after a restart.
public sealed partial class DiscordWebhook
{
    private string BoardFile => Path.Combine(folder, "board.txt");
    private string? boardId, boardSig;
    private bool boardEnabled;
    private DateTimeOffset boardLast = DateTimeOffset.MinValue;
    private BoardRow[]? boardPending, boardLastRows;
    private Task? boardTask;
    private HttpStatusCode? lastStatus;

    private string BoardWebhookFile => Path.Combine(folder, "webhook-board.dat");
    // The status board can have its own channel. Without one it shares the alerts channel.
    private string? BoardTarget => boardUrl ?? url;
    public bool BoardEnabled => boardEnabled && BoardTarget is not null;
    public bool BoardHasOwnWebhook => boardUrl is not null;

    private void LoadBoardWebhook()
    {
        try
        {
            if (!File.Exists(BoardWebhookFile) || new FileInfo(BoardWebhookFile).Length > 16384) return;
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(BoardWebhookFile));
            if (stored is null) return;
            string plain = System.Text.Encoding.UTF8.GetString(LoginVault.Unprotect(Convert.FromBase64String(stored.Cipher)));
            if (IsValidUrl(plain)) boardUrl = plain;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or LoginVault.CryptographicException) { }
    }

    // A message can only be edited through the webhook that posted it, so changing the channel starts a new board message.
    public void SaveBoardWebhook(string webhookUrl)
    {
        string clean = (webhookUrl ?? "").Trim();
        if (!IsValidUrl(clean)) throw new ArgumentException("That doesn't look like a Discord webhook address. Copy it from the channel's Integrations → Webhooks page.");
        Directory.CreateDirectory(folder);
        string full = Path.GetFullPath(BoardWebhookFile), temporary = full + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Stored(Convert.ToBase64String(LoginVault.Protect(System.Text.Encoding.UTF8.GetBytes(clean))), null)));
            File.Move(temporary, full, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        MoveBoard(clean);
    }

    // Back to posting the board in the alerts channel.
    public void ClearBoardWebhook()
    {
        try { if (File.Exists(BoardWebhookFile)) File.Delete(BoardWebhookFile); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        MoveBoard(null);
    }

    private void MoveBoard(string? newUrl)
    {
        string? oldTarget = BoardTarget;
        lock (gate) boardUrl = newUrl;
        RehomeBoard(oldTarget);
    }

    // The board's channel changed: forget the old message (tidying it away if it still exists) so a new one is posted.
    private void RehomeBoard(string? oldTarget)
    {
        string? oldId; lock (gate) { oldId = boardId; boardId = null; boardSig = null; boardPending = null; boardLastRows = null; }
        SaveBoard();
        if (oldTarget is not null && oldId is not null && oldTarget != BoardTarget) _ = SendAsync(HttpMethod.Delete, oldTarget + "/messages/" + oldId, null);
    }

    private void LoadBoard()
    {
        try
        {
            if (!File.Exists(BoardFile) || new FileInfo(BoardFile).Length > 200) return;
            var parts = File.ReadAllText(BoardFile).Trim().Split(':');
            boardEnabled = parts[0] == "1";
            boardId = parts.Length > 1 && parts[1].Length is > 0 and <= 25 && parts[1].All(char.IsDigit) ? parts[1] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void SaveBoard()
    {
        try
        {
            if (!boardEnabled && boardId is null) { if (File.Exists(BoardFile)) File.Delete(BoardFile); return; }
            Directory.CreateDirectory(folder); File.WriteAllText(BoardFile, (boardEnabled ? "1" : "0") + ":" + boardId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // Turning it off removes the message from the channel, so nothing stale is left behind.
    public void SetBoard(bool on)
    {
        if (on == boardEnabled || (on && BoardTarget is null)) return;
        string? old; lock (gate) { boardEnabled = on; old = boardId; boardId = null; boardSig = null; boardPending = null; boardLastRows = null; }
        SaveBoard();
        if (!on && old is not null && BoardTarget is not null) _ = SendAsync(HttpMethod.Delete, BoardTarget + "/messages/" + old, null);
    }

    private void ForgetBoard() { lock (gate) { boardEnabled = false; boardId = null; boardSig = null; boardPending = null; boardLastRows = null; } SaveBoard(); }

    // Hands over the latest picture of every account. Changes are sent at most once per interval, always the newest.
    public void QueueBoard(IReadOnlyList<BoardRow> rows)
    {
        if (!BoardEnabled || disposed) return;
        var snapshot = rows.ToArray();
        lock (gate)
        {
            boardPending = snapshot;
            if (boardTask is { IsCompleted: false }) return;
            boardTask = Task.Run(BoardLoopAsync);
        }
    }

    private async Task BoardLoopAsync()
    {
        while (true)
        {
            BoardRow[]? rows;
            lock (gate) { rows = boardPending; boardPending = null; }
            if (rows is null || !BoardEnabled) return;
            if (Signature(rows) == boardSig) continue;
            var wait = boardInterval - (DateTimeOffset.UtcNow - boardLast);
            if (wait > TimeSpan.Zero) await Task.Delay(wait).ConfigureAwait(false);
            lock (gate) { rows = boardPending ?? rows; boardPending = null; }
            string sig = Signature(rows);
            if (sig == boardSig) continue;
            boardLast = DateTimeOffset.UtcNow;
            if (await SendBoardAsync(rows, closed: false).ConfigureAwait(false)) { boardSig = sig; lock (gate) boardLastRows = rows; }
        }
    }

    // Marks the board as no longer live, so it never claims everything is fine after the manager has stopped.
    public async Task CloseBoardAsync()
    {
        BoardRow[]? rows; lock (gate) rows = boardLastRows;
        if (!BoardEnabled || boardId is null || rows is null) return;
        await SendBoardAsync(rows, closed: true).ConfigureAwait(false);
        boardSig = null;
    }

    private static string Signature(IEnumerable<BoardRow> rows) => string.Join("|", rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Select(r => $"{(int)r.State}:{r.Name}:{r.Detail}"));

    private async Task<bool> SendBoardAsync(BoardRow[] rows, bool closed)
    {
        if (BoardTarget is not { } target) return false;
        var now = DateTimeOffset.UtcNow;
        if (boardId is { } id)
        {
            var (ok, _) = await SendAsync(HttpMethod.Patch, target + "/messages/" + id, BoardPayload(rows, closed, now, edit: true)).ConfigureAwait(false);
            if (ok) return true;
            if (lastStatus != HttpStatusCode.NotFound) return false; // a hiccup: try the same message again later
            boardId = null; // deleted in Discord: start a new one
        }
        var (posted, body) = await SendAsync(HttpMethod.Post, target + "?wait=true", BoardPayload(rows, closed, now, edit: false)).ConfigureAwait(false);
        if (posted && MessageId(body) is { } created) { boardId = created; SaveBoard(); return true; }
        return false;
    }

    private string BoardPayload(IReadOnlyList<BoardRow> rows, bool closed, DateTimeOffset now, bool edit)
    {
        int online = rows.Count(r => r.State == BoardState.Ok), down = rows.Count(r => r.State == BoardState.Down), warn = rows.Count(r => r.State == BoardState.Warn);
        var lines = rows.OrderBy(r => r.State switch { BoardState.Down => 0, BoardState.Warn => 1, BoardState.Ok => 2, _ => 3 }).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Take(40).Select(r => $"{r.State switch { BoardState.Ok => "🟢", BoardState.Warn => "🟡", BoardState.Down => "🔴", _ => "⚪" }} **{Clip(r.Name, 40)}**: {(r.Detail.Length > 120 ? r.Detail[..120] : r.Detail)}").ToList();
        if (rows.Count > 40) lines.Add($"…and {rows.Count - 40} more.");
        long stamp = now.ToUnixTimeSeconds();
        string header = closed ? $"Manager closed <t:{stamp}:R>. This is the last known status and may be out of date."
            : $"{online} of {rows.Count} online · updated <t:{stamp}:R>";
        var payload = new Dictionary<string, object?>
        {
            ["content"] = null,
            ["allowed_mentions"] = new Dictionary<string, object?> { ["parse"] = Array.Empty<string>(), ["users"] = Array.Empty<string>() },
            ["embeds"] = new[] { new Dictionary<string, object?>
            {
                ["title"] = closed ? "Account status (manager closed)" : "Account status",
                ["description"] = header + "\n\n" + string.Join("\n", lines),
                ["color"] = closed ? 0x6E7681 : down > 0 ? 0xE5534B : warn > 0 ? 0xD29922 : 0x3FB950,
            } },
        };
        if (!edit) payload["username"] = "Roblox AFK Manager";
        return JsonSerializer.Serialize(payload);
    }
}
