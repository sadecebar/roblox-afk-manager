using System.Runtime.InteropServices;
using System.Text;
using RobloxAfkManager.Core;
using Microsoft.Win32.SafeHandles;

namespace RobloxAfkManager.App.Native;

internal static class NativeMethods
{
    internal const uint NoActivate = 0x10, NoZOrder = 0x4, NoOwnerZOrder = 0x200, AsyncWindowPos = 0x4000;
    internal const uint AsyncPlacement = 0x4;
    internal const int ShowNormalNoActivate = 4;
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect
    {
        public int Left, Top, Right, Bottom;
        public readonly PixelRect ToPixels() => new(Left, Top, Right - Left, Bottom - Top);
        public static Rect From(PixelRect r) => new() { Left = r.X, Top = r.Y, Right = r.Right, Bottom = r.Bottom };
    }
    [StructLayout(LayoutKind.Sequential)] internal struct WindowPlacement
    {
        public int Length;
        public uint Flags, ShowCommand;
        public Point MinPosition, MaxPosition;
        public Rect NormalPosition;
        public static WindowPlacement Empty => new() { Length = Marshal.SizeOf<WindowPlacement>() };
        public readonly Placement ToModel() => new(Flags, ShowCommand, MinPosition.X, MinPosition.Y, MaxPosition.X, MaxPosition.Y, NormalPosition.ToPixels());
        public static WindowPlacement From(Placement p) => new()
        {
            Length = Marshal.SizeOf<WindowPlacement>(), Flags = AsyncPlacement,
            // Never restore SW_SHOWMINIMIZED, SW_MINIMIZE or SW_SHOWMINNOACTIVE.
            ShowCommand = p.ShowCommand == 3 ? 3u : 4u,
            MinPosition = new() { X = p.MinX, Y = p.MinY }, MaxPosition = new() { X = p.MaxX, Y = p.MaxY },
            NormalPosition = Rect.From(p.NormalBounds)
        };
    }
    [StructLayout(LayoutKind.Sequential)] internal struct MinMaxInfo
    { public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] internal struct Size { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] internal struct ThumbnailProperties
    {
        public uint Flags;
        public Rect Destination, Source;
        public byte Opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool Visible;
        [MarshalAs(UnmanagedType.Bool)] public bool SourceClientAreaOnly;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    internal delegate bool EnumWindowsProc(nint hwnd, nint param);
    internal delegate bool MonitorEnumProc(nint monitor, nint hdc, ref Rect rect, nint param);
    internal delegate void WinEventProc(nint hook, uint eventType, nint hwnd, int objectId, int childId, uint threadId, uint time);

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumWindows(EnumWindowsProc callback, nint param);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] internal static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] internal static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint hwnd, StringBuilder text, int maximum);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsZoomed(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsHungAppWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowPlacement(nint hwnd, ref WindowPlacement placement);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowPlacement(nint hwnd, in WindowPlacement placement);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShowWindowAsync(nint hwnd, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint SendMessageTimeout(nint hwnd, uint message, nuint wParam, ref MinMaxInfo lParam, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyWindow(nint hwnd);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out int value, int size);
    [DllImport("dwmapi.dll")] internal static extern int DwmRegisterThumbnail(nint destination, nint source, out nint thumbnail);
    [DllImport("dwmapi.dll")] internal static extern int DwmUnregisterThumbnail(nint thumbnail);
    [DllImport("dwmapi.dll")] internal static extern int DwmQueryThumbnailSourceSize(nint thumbnail, out Size size);
    [DllImport("dwmapi.dll")] internal static extern int DwmUpdateThumbnailProperties(nint thumbnail, in ThumbnailProperties properties);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ScreenToClient(nint hwnd, ref Point point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] internal static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc callback, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWinEvent(nint hook);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int length);
}
