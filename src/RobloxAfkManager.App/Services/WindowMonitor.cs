using System.Diagnostics;
using System.Windows.Threading;
using RobloxAfkManager.App.Native;
using RobloxAfkManager.Core;

namespace RobloxAfkManager.App.Services;

// Installed and disposed on the WPF dispatcher thread, which supplies the WinEvent message loop.
public sealed class WindowMonitor : IDisposable
{
    private readonly NativeMethods.WinEventProc callback;
    private readonly List<nint> hooks = [];
    private readonly DispatcherTimer debounce;
    private readonly DispatcherTimer fallback;
    private readonly Win32WindowSystem windows;
    private readonly Action changed;
    private bool disposed;

    public WindowMonitor(Win32WindowSystem windows, IAppLog log, Action changed)
    {
        this.windows = windows;
        this.changed = changed;
        debounce = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(750) };
        debounce.Tick += OnDebounce;
        fallback = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(10) };
        fallback.Tick += OnFallback;
        callback = OnWindowEvent; // Root the delegate for the complete hook lifetime.
        foreach (var range in new[] { (0x8000u, 0x8003u), (0x16u, 0x17u) })
        {
            // OUTOFCONTEXT (0) | SKIPOWNPROCESS (2); never loads code into Roblox.
            var hook = NativeMethods.SetWinEventHook(range.Item1, range.Item2, 0, callback, 0, 0, 2);
            if (hook == 0) log.Write("Window event monitoring unavailable; using the 10-second recovery refresh.");
            else hooks.Add(hook);
        }
        fallback.Start();
    }

    public void RequestRefresh()
    {
        if (!disposed && !debounce.IsEnabled) debounce.Start();
    }
    private void OnDebounce(object? sender, EventArgs e) { debounce.Stop(); if (!disposed) changed(); }
    private void OnFallback(object? sender, EventArgs e) { if (!disposed) changed(); }

    private void OnWindowEvent(nint hook, uint type, nint hwnd, int objectId, int childId, uint thread, uint time)
    {
        if (disposed || hwnd == 0 || objectId != 0 || childId != 0) return;
        try
        {
            if (windows.IsKnown(hwnd))
            {
                if (type == 0x8001) windows.Invalidate(hwnd);
                RequestRefresh();
                return;
            }
            if (type is not (0x8000 or 0x8002)) return;
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            using var process = Process.GetProcessById((int)pid);
            if (process.ProcessName.Equals("RobloxPlayerBeta", StringComparison.OrdinalIgnoreCase)) RequestRefresh();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        { /* A process can exit before its queued accessibility event arrives. */ }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        debounce.Stop(); fallback.Stop();
        debounce.Tick -= OnDebounce; fallback.Tick -= OnFallback;
        foreach (var hook in hooks) NativeMethods.UnhookWinEvent(hook);
        GC.KeepAlive(callback);
    }
}
