using System.Text;

namespace RobloxAfkManager.Core;

// A plain-text, append-only record of everything that went wrong or was fixed, so the morning after shows what happened to
// which account and what the manager did about it. One line per event; the file is rotated so it never grows without bound.
public sealed class IncidentJournal(string path, long maxBytes = 1_000_000)
{
    private readonly object gate = new();
    public string Path { get; } = path;

    public static string Format(ActivityEntry entry)
    {
        string kind = entry.Warning ? "PROBLEM" : "INFO   ";
        string label = entry.Label.Replace('|', '/').ReplaceLineEndings(" ");
        string message = entry.Message.ReplaceLineEndings(" ");
        return $"{entry.At.ToLocalTime():yyyy-MM-dd HH:mm:ss} | {kind} | {label} | {message}";
    }

    public void Write(ActivityEntry entry)
    {
        lock (gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (System.IO.File.Exists(Path) && new System.IO.FileInfo(Path).Length > maxBytes) System.IO.File.Move(Path, Path + ".previous", true);
                System.IO.File.AppendAllText(Path, Format(entry) + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException) { }
        }
    }
}
