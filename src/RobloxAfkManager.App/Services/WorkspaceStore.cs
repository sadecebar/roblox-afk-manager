using System.IO;
using System.Text.Json;
using RobloxAfkManager.Core;

namespace RobloxAfkManager.App.Services;

public sealed class WorkspaceStore(string? path = null) : IWorkspaceStore
{
    private readonly string path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RobloxAfkManager", "workspace.json");
    public WorkspaceSettings Read()
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 262144) return new([], []);
            var data = JsonSerializer.Deserialize<WorkspaceSettings>(File.ReadAllText(path));
            return data is null ? new([], []) : Sanitize(data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return new([], []); }
    }
    public void Write(WorkspaceSettings settings)
    {
        var safe = Sanitize(settings);
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temporary = full + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(safe)); File.Move(temporary, full, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static WorkspaceSettings Sanitize(WorkspaceSettings settings) => new(
        (settings.Profiles ?? []).Where(p => p is not null && p.UserId > 0 && p.Order >= 0 && p.Order <= 100000 &&
            (p.PreferredPlan is null || Enum.IsDefined(p.PreferredPlan.Value)))
            .Select(p => p with { CustomName = AccountNames.Normalize(p.CustomName), Username = AccountNames.Normalize(p.Username),
                LastGame = p.LastGame is { Length: > 0 and <= 120 } game ? game : null,
                LastSeen = p.LastSeen is { } seen && seen <= DateTimeOffset.UtcNow.AddDays(1) ? seen : null }).DistinctBy(p => p.UserId).Take(128).ToArray(),
        (settings.History ?? []).Where(e => e is not null && e.Label is { Length: <= 64 } && e.Message is { Length: <= 240 })
            .OrderByDescending(e => e.At).Take(100).ToArray(), settings.Notifications);
}
