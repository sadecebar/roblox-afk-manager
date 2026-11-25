using System.IO;
using System.Text.Json;
using RobloxAfkManager.Core;

namespace RobloxAfkManager.App.Services;

public sealed class ClientNameStore : IClientNameStore
{
    private sealed record Entry(int ProcessId, long StartTimeUtcTicks, string Name);
    private readonly string path;
    private readonly Dictionary<ProcessIdentity, string> names = [];
    public ClientNameStore(string? path = null)
    {
        this.path = path ?? Path.Combine(AppFolders.Data, "client-names.json");
        try
        {
            if (!File.Exists(this.path) || new FileInfo(this.path).Length > 65536) return;
            foreach (var entry in (JsonSerializer.Deserialize<Entry[]>(File.ReadAllText(this.path)) ?? []).Take(128))
                if (entry is not null && entry.ProcessId > 0 && entry.StartTimeUtcTicks > 0 && AccountNames.Normalize(entry.Name) is { } name)
                    names[new(entry.ProcessId, entry.StartTimeUtcTicks)] = name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { names.Clear(); }
    }
    public string? Get(ProcessIdentity process) => names.GetValueOrDefault(process);
    public void Set(ProcessIdentity process, string? name)
    {
        var value = AccountNames.Normalize(name);
        var next = new Dictionary<ProcessIdentity, string>(names);
        if (value is null) next.Remove(process); else next[process] = value;
        var entries = next.OrderByDescending(p => p.Key.StartTimeUtcTicks).Take(128)
            .Select(p => new Entry(p.Key.ProcessId, p.Key.StartTimeUtcTicks, p.Value)).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        names.Clear();
        foreach (var entry in entries) names[new(entry.ProcessId, entry.StartTimeUtcTicks)] = entry.Name;
    }
}
