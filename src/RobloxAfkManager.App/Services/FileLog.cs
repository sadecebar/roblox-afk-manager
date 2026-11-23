using System.Diagnostics;
using System.IO;
using RobloxAfkManager.Core;

namespace RobloxAfkManager.App.Services;

public sealed class FileLog : IAppLog
{
    private readonly object gate = new();
    public string DirectoryPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RobloxAfkManager", "Logs");
    public void Write(string message, Exception? exception = null)
    {
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                var path = Path.Combine(DirectoryPath, "manager.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Move(path, path + ".previous", true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{(exception is null ? "" : " " + exception)}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Debug.WriteLine($"Logging unavailable: {ex.Message}. {message}"); }
        }
    }
}
