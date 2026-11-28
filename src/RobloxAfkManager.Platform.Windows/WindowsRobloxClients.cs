using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using RobloxAfkManager.Engine;

namespace RobloxAfkManager.Platform.Windows;

public sealed class WindowsRobloxClients : IRobloxClients
{
    // Roblox leaves a hidden background process behind every time a client exits (started with --launch-to-tray). It has no window and
    // holds almost no memory. A client started afterwards tries to hand its launch to it, fails ("SingleInstanceGuard", error 1400) and
    // exits at once, which is the "didn't open a new window" failure. They are closed before each launch. Anything with a window,
    // anything big enough to be a running game, and anything started in the last minute (a client still opening) is left alone.
    private static readonly TimeSpan BackgroundClientAge = TimeSpan.FromSeconds(45);
    private const long BackgroundClientMemory = 400L * 1024 * 1024;

    public int CloseIdleBackgroundClients()
    {
        int closed = 0;
        foreach (var process in Process.GetProcessesByName("RobloxPlayerBeta"))
            using (process)
            {
                try
                {
                    if (process.MainWindowHandle != IntPtr.Zero || process.WorkingSet64 >= BackgroundClientMemory || DateTime.Now - process.StartTime < BackgroundClientAge) continue;
                    process.Kill(); closed++;
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { /* it is already gone, or not ours to close */ }
            }
        return closed;
    }

    public int[] ProcessIds()
    {
        var ids = new List<int>();
        foreach (var process in Process.GetProcessesByName("RobloxPlayerBeta")) using (process) ids.Add(process.Id);
        return [.. ids];
    }

    public int ReleaseSingleton() => RobloxSingleton.Release();

    // The version Roblox's servers currently accept, then a version that is already running, then the newest on disk.
    public string ClientExecutable(string? acceptedVersion)
    {
        string versions = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "Versions");
        if (acceptedVersion is { Length: > 0 } guid && guid.All(c => char.IsLetterOrDigit(c) || c == '-') && Path.Combine(versions, guid, "RobloxPlayerBeta.exe") is { } current && File.Exists(current)) return current;
        foreach (var process in Process.GetProcessesByName("RobloxPlayerBeta"))
            using (process) { try { if (process.MainModule?.FileName is { } running && File.Exists(running)) return running; } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { } }
        var newest = Directory.Exists(versions)
            ? new DirectoryInfo(versions).EnumerateDirectories().Select(d => Path.Combine(d.FullName, "RobloxPlayerBeta.exe")).Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
        return newest ?? throw new InvalidOperationException("Roblox isn't installed for this Windows user. Install it from roblox.com once, then try again.");
    }

    // Never go through the roblox-player: protocol handler. That is RobloxPlayerInstaller.exe, which treats the
    // launch as an install/update and terminates every running RobloxPlayerBeta.exe. Start the client itself.
    public void Start(string executable, string launchArgument)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false };
        info.ArgumentList.Add(launchArgument);
        Process.Start(info)?.Dispose();
    }
}
