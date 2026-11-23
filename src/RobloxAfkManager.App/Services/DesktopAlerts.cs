using System.Runtime.InteropServices;
using RobloxAfkManager.Core;

namespace RobloxAfkManager.App.Services;

// A notification-area icon is created only when needed. Windows controls balloon visibility.
public sealed class DesktopAlerts(nint window) : IDisposable
{
    private bool added;
    private DateTimeOffset lastShown;
    private NotifyData Data() => new() { Size = (uint)Marshal.SizeOf<NotifyData>(), Window = window, Id = 1,
        Flags = 2 | 4, Icon = LoadIcon(0, (nint)32516), Tip = "Roblox AFK Manager", Info = "", Title = "" };
    public void Show(ActivityEntry entry)
    {
        if (DateTimeOffset.UtcNow - lastShown < TimeSpan.FromSeconds(10)) return;
        var data = Data();
        if (!added) added = Shell_NotifyIcon(0, ref data);
        if (!added) return;
        data.Flags = 0x10; data.InfoFlags = 2 | 0x10; // Warning, no sound. Respect Windows notification settings.
        data.Title = "Roblox client needs attention";
        data.Info = (entry.Label + ": " + entry.Message)[..Math.Min(255, entry.Label.Length + 2 + entry.Message.Length)];
        if (Shell_NotifyIcon(1, ref data)) lastShown = DateTimeOffset.UtcNow;
    }
    public void Dispose() { if (added) { var data = Data(); Shell_NotifyIcon(2, ref data); added = false; } }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyData
    {
        public uint Size; public nint Window; public uint Id, Flags, Callback; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Title;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Shell_NotifyIcon(uint operation, ref NotifyData data);
    [DllImport("user32.dll", EntryPoint = "LoadIconW")] private static extern nint LoadIcon(nint instance, nint name);
}
