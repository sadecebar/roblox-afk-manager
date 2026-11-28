using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using RobloxAfkManager.Core;
using static RobloxAfkManager.Platform.Windows.NativeMethods;

namespace RobloxAfkManager.Platform.Windows;

public sealed class Win32WindowSystem(IAppLog log) : IWindowSystem
{
    private const string ProcessName = "RobloxPlayerBeta";
    private readonly ConcurrentDictionary<nint, WindowIdentity> identities = new();
    private readonly CpuUsageSampler cpuUsage = new();
    private long generation;
    public void Invalidate(nint hwnd) => identities.TryRemove(hwnd, out _);
    public bool IsKnown(nint hwnd) => identities.ContainsKey(hwnd);

    private static bool IsGameWindow(nint hwnd) => IsWindow(hwnd) && GetAncestor(hwnd, 2) == hwnd &&
        GetWindow(hwnd, 4) == 0 && (GetWindowLongPtrW(hwnd, -20).ToInt64() & 0x80) == 0 &&
        (GetWindowLongPtrW(hwnd, -16).ToInt64() & 0x40000000) == 0;

    private static bool IsCloaked(nint hwnd) => DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static bool IsRobloxImage(int pid)
    {
        using var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, (uint)pid);
        if (handle.IsInvalid) return false;
        var path = new StringBuilder(32768);
        int size = path.Capacity;
        return QueryFullProcessImageName(handle, 0, path, ref size) &&
            string.Equals(Path.GetFileName(path.ToString()), ProcessName + ".exe", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ClientWindow> Detect()
    {
        var processes = new Dictionary<int, (long Start, long? Memory, double? Cpu)>();
        foreach (var process in Process.GetProcessesByName(ProcessName))
        {
            using (process)
            {
                try
                {
                    if (process.HasExited || !IsRobloxImage(process.Id)) continue;
                    long? memory = null;
                    try { memory = process.WorkingSet64; } catch (Win32Exception) { /* Optional metric. */ }
                    long start = process.StartTime.ToUniversalTime().Ticks;
                    double? cpu = null;
                    try { cpu = cpuUsage.Sample(new(process.Id, start), process.TotalProcessorTime,
                        (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency, Environment.ProcessorCount); }
                    catch (Win32Exception) { /* Optional metric; discovery still works. */ }
                    processes[process.Id] = (start, memory, cpu);
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
                { log.Write("A Roblox process exited or could not be queried during detection.", ex); }
            }
        }
        cpuUsage.Retain(processes.Select(p => new ProcessIdentity(p.Key, p.Value.Start)));
        var found = new List<ClientWindow>();
        if (!EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (!processes.TryGetValue((int)pid, out var info) || !IsGameWindow(hwnd) || !IsWindowVisible(hwnd)) return true;
            if (!GetWindowRect(hwnd, out var rect)) return true;
            var title = new StringBuilder(1024);
            GetWindowText(hwnd, title, title.Capacity);
            string state = IsCloaked(hwnd) ? "Other desktop / cloaked" : IsHungAppWindow(hwnd) ? "Not responding" :
                IsIconic(hwnd) ? "Minimized externally" : IsZoomed(hwnd) ? "Maximized" : GetForegroundWindow() == hwnd ? "Foreground" : "Open";
            var identity = identities.AddOrUpdate(hwnd,
                _ => new(hwnd, (int)pid, info.Start, Interlocked.Increment(ref generation)),
                (_, prior) => prior.ProcessId == (int)pid && prior.StartTimeUtcTicks == info.Start ? prior : new(hwnd, (int)pid, info.Start, Interlocked.Increment(ref generation)));
            found.Add(new(identity, title.Length > 0 ? title.ToString() : "Roblox", rect.ToPixels(), info.Memory, state) { CpuUsagePercent = info.Cpu });
            return true;
        }, 0)) throw new Win32Exception("Windows could not enumerate top-level windows.");

        // A process may briefly create a splash/helper window; pick its largest unowned game window.
        foreach (var pair in identities.ToArray())
            if (!IsWindow(pair.Key) || !processes.TryGetValue(pair.Value.ProcessId, out var p) || p.Start != pair.Value.StartTimeUtcTicks)
                identities.TryRemove(pair.Key, out _);
        return found.GroupBy(w => w.Identity.ProcessId).Select(g => g.OrderByDescending(w => (long)w.Bounds.Width * w.Bounds.Height).First())
            .OrderBy(w => w.Identity.StartTimeUtcTicks).ThenBy(w => w.Identity.ProcessId).ToArray();
    }

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var result = new List<MonitorInfo>();
        Exception? error = null;
        bool success = EnumDisplayMonitors(0, 0, (nint monitor, nint hdc, ref Rect rect, nint data) =>
        {
            try
            {
                var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>(), Device = "" };
                if (!GetMonitorInfo(monitor, ref info)) throw new Win32Exception("Could not read monitor working area.");
                // A hidden, manager-owned PMv2 window gives the monitor's effective DPI
                // through GetDpiForWindow, without the legacy GetDpiForMonitor ambiguity.
                var probe = CreateWindowEx(0x08000080, "STATIC", "", 0x80000000, info.Work.Left + 1, info.Work.Top + 1, 1, 1, 0, 0, 0, 0);
                if (probe == 0) throw LastError("Create DPI probe");
                uint dpi;
                try { dpi = GetDpiForWindow(probe); }
                finally { DestroyWindow(probe); }
                if (dpi == 0) throw new InvalidOperationException("Could not determine monitor scaling.");
                result.Add(new(info.Device, info.Work.ToPixels(), dpi, (info.Flags & 1) != 0));
                return true;
            }
            catch (Exception ex) { error = ex; return false; }
        }, 0);
        if (error is not null) throw error;
        if (!success) throw new Win32Exception("Windows could not enumerate monitors.");
        return result.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.WorkArea.X).ToArray();
    }

    public bool IsCurrent(WindowIdentity id)
    {
        if (!identities.TryGetValue(id.Handle, out var current) || current != id) return false;
        if (!IsGameWindow(id.Handle)) return false;
        GetWindowThreadProcessId(id.Handle, out uint pid);
        if (pid != id.ProcessId) return false;
        try
        {
            using var process = Process.GetProcessById(id.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == id.StartTimeUtcTicks && IsRobloxImage(id.ProcessId);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException) { return false; }
    }

    private void Ensure(WindowIdentity id)
    {
        if (!IsCurrent(id)) throw new InvalidOperationException($"Client PID {id.ProcessId} closed, restarted, or is no longer the same game window. Detect again.");
        if (!IsWindowVisible(id.Handle) || IsCloaked(id.Handle))
            throw new InvalidOperationException($"Client PID {id.ProcessId} is hidden or on another virtual desktop. Move it to this desktop first.");
    }

    public WindowLimits GetLimits(WindowIdentity id, uint targetDpi)
    {
        Ensure(id);
        if ((GetWindowLongPtrW(id.Handle, -16).ToInt64() & 0x40000 /* WS_THICKFRAME */) == 0)
            throw new InvalidOperationException($"Client PID {id.ProcessId} is not a resizable window. Leave fullscreen and try again.");
        uint dpi = GetDpiForWindow(id.Handle);
        if (dpi == 0) throw new InvalidOperationException("The client closed while querying its scaling.");
        var limits = new MinMaxInfo
        {
            MinTrackSize = new() { X = GetSystemMetricsForDpi(34, dpi), Y = GetSystemMetricsForDpi(35, dpi) },
            MaxTrackSize = new() { X = 32767, Y = 32767 }
        };
        // WM_GETMINMAXINFO is an OS-marshalled system message, not process memory access.
        if (SendMessageTimeout(id.Handle, 0x24, 0, ref limits, 0x2 | 0x20, 250, out _) == 0)
            throw new InvalidOperationException($"Client PID {id.ProcessId} did not answer its size query. It may be busy or running at a different privilege level. No windows moved.");
        // Conservatively account for moving up in DPI. Never assume moving down in
        // DPI relaxes an app's constraints; verify exact physical bounds after moving.
        double ratio = Math.Max(1, (double)targetDpi / dpi);
        int minW = Math.Max(GetSystemMetricsForDpi(34, targetDpi), (int)Math.Ceiling(limits.MinTrackSize.X * ratio));
        int minH = Math.Max(GetSystemMetricsForDpi(35, targetDpi), (int)Math.Ceiling(limits.MinTrackSize.Y * ratio));
        if (minW <= 0 || minH <= 0 || limits.MaxTrackSize.X <= 0 || limits.MaxTrackSize.Y <= 0)
            throw new InvalidOperationException("The client returned invalid window size constraints.");
        return new(minW, minH, limits.MaxTrackSize.X, limits.MaxTrackSize.Y);
    }

    public Placement Capture(WindowIdentity id)
    {
        Ensure(id);
        var p = WindowPlacement.Empty;
        if (!GetWindowPlacement(id.Handle, ref p)) throw LastError("Save window placement");
        return p.ToModel();
    }

    public PixelRect ReadBounds(WindowIdentity id)
    {
        Ensure(id);
        if (!GetWindowRect(id.Handle, out var rect)) throw LastError("Read window bounds");
        return rect.ToPixels();
    }

    public bool IsNormal(WindowIdentity id) => IsCurrent(id) && IsWindowVisible(id.Handle) && !IsIconic(id.Handle) && !IsZoomed(id.Handle) && !IsCloaked(id.Handle);

    public void MoveNormal(WindowIdentity id, PixelRect bounds)
    {
        Ensure(id);
        if (IsHungAppWindow(id.Handle)) throw new InvalidOperationException($"Client PID {id.ProcessId} is not responding.");
        if (IsIconic(id.Handle) || IsZoomed(id.Handle))
        {
            Ensure(id);
            if (!ShowWindowAsync(id.Handle, ShowNormalNoActivate)) throw LastError("Restore normal window state");
            WaitFor(id, () => IsNormal(id), "The client did not leave its minimized/maximized state.");
        }
        Ensure(id);
        if (!SetWindowPos(id.Handle, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, NoActivate | NoZOrder | NoOwnerZOrder | AsyncWindowPos))
            throw LastError("Arrange window");
        WaitFor(id, () => IsNormal(id) && ReadBounds(id) == bounds, "The client rejected the requested size or position.");
    }

    public void Maximize(WindowIdentity id, PixelRect monitorArea)
    {
        Ensure(id);
        if (IsHungAppWindow(id.Handle)) throw new InvalidOperationException($"Client PID {id.ProcessId} is not responding.");
        if (IsIconic(id.Handle) || IsZoomed(id.Handle))
        {
            if (!ShowWindowAsync(id.Handle, ShowNormalNoActivate)) throw LastError("Restore normal window state");
            WaitFor(id, () => IsNormal(id), "The client did not leave its minimized/maximized state.");
        }
        // Windows maximizes onto the monitor the window currently sits on, so move it there first.
        if (!SetWindowPos(id.Handle, 0, monitorArea.X + 40, monitorArea.Y + 40, Math.Max(400, monitorArea.Width / 2), Math.Max(300, monitorArea.Height / 2), NoActivate | NoZOrder | NoOwnerZOrder | AsyncWindowPos))
            throw LastError("Move window to main monitor");
        Thread.Sleep(120);
        Ensure(id);
        if (!ShowWindowAsync(id.Handle, 3 /* SW_MAXIMIZE */)) throw LastError("Maximize window");
        WaitFor(id, () => IsZoomed(id.Handle), "The client did not maximize.");
    }

    public void Restore(WindowIdentity id, Placement placement)
    {
        Ensure(id);
        var p = WindowPlacement.From(placement);
        if (!SetWindowPlacement(id.Handle, in p)) throw LastError("Restore original placement");
        WaitFor(id, () =>
        {
            var current = WindowPlacement.Empty;
            return GetWindowPlacement(id.Handle, ref current) && !IsIconic(id.Handle) &&
                (placement.ShowCommand == 3 ? IsZoomed(id.Handle) : !IsZoomed(id.Handle)) &&
                current.NormalPosition.ToPixels() == placement.NormalBounds;
        }, "Could not verify the restored placement. The display configuration may have changed.");
    }

    public bool Focus(WindowIdentity id)
    {
        Ensure(id);
        if (IsIconic(id.Handle))
        {
            if (!ShowWindowAsync(id.Handle, ShowNormalNoActivate)) throw LastError("Restore selected client");
            WaitFor(id, () => !IsIconic(id.Handle), "The selected client did not restore.");
        }
        Ensure(id);
        // Respect Windows foreground restrictions. No AttachThreadInput or fake keystrokes.
        return SetForegroundWindow(id.Handle);
    }

    public void Close(WindowIdentity id)
    {
        if (!IsCurrent(id)) return; // same pid + start time + image check as every other operation
        using var process = Process.GetProcessById(id.ProcessId);
        process.CloseMainWindow();
        if (!process.WaitForExit(4000)) { process.Kill(); process.WaitForExit(5000); }
    }

    public void RestoreForPreview(WindowIdentity id)
    {
        Ensure(id);
        if (!IsIconic(id.Handle)) return; // Existing normal/maximized clients keep their size and placement.
        if (!ShowWindowAsync(id.Handle, ShowNormalNoActivate)) throw LastError("Restore preview client");
        WaitFor(id, () => !IsIconic(id.Handle), "The client did not restore for its preview.");
    }

    private void WaitFor(WindowIdentity id, Func<bool> condition, string failure)
    {
        var timeout = Stopwatch.StartNew();
        do
        {
            Ensure(id);
            if (condition()) return;
            Thread.Sleep(35); // Only during an explicit operation, never a background frame loop.
        } while (timeout.ElapsedMilliseconds < 1800);
        throw new InvalidOperationException($"PID {id.ProcessId}: {failure}");
    }

    private static Win32Exception LastError(string action) => new(Marshal.GetLastWin32Error(), action + " failed.");
}
