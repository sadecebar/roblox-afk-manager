using System.IO;
using System.Text;
using System.Text.Json;

namespace RobloxAfkManager.Engine;

// Stores each account's session cookie encrypted with Windows DPAPI (current user only).
// The cookie is only ever sent to roblox.com, never logged, and never written in clear text.
public sealed class LoginVault(string? path = null, ISecretProtector? protector = null)
{
    private readonly ISecretProtector protector = protector ?? DpapiSecretProtector.Instance;
    private sealed record Entry(long UserId, string Username, string Cipher);
    private readonly string path = path ?? Path.Combine(AppFolders.Data, "logins.dat");
    private List<Entry> entries = Load(path ?? Path.Combine(AppFolders.Data, "logins.dat"));
    public IReadOnlyList<SavedLogin> Logins => entries.Select(e => new SavedLogin(e.UserId, e.Username)).ToArray();

    public void Save(long userId, string username, string cookie)
    {
        entries.RemoveAll(e => e.UserId == userId);
        entries.Add(new(userId, username, Convert.ToBase64String(this.protector.Protect(Encoding.UTF8.GetBytes(cookie)))));
        Write();
    }
    public void Remove(long userId) { entries.RemoveAll(e => e.UserId == userId); Write(); }
    public string? Cookie(long userId)
    {
        var entry = entries.FirstOrDefault(e => e.UserId == userId);
        if (entry is null) return null;
        try { return Encoding.UTF8.GetString(protector.Unprotect(Convert.FromBase64String(entry.Cipher))); }
        catch (Exception ex) when (ex is SecretProtectionException or FormatException) { return null; }
    }

    private void Write()
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temporary = full + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(entries)); File.Move(temporary, full, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static List<Entry> Load(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length < 1_000_000 ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? [] : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

}
