using System.Runtime.InteropServices;

namespace RobloxAfkManager.App.Services;

// How long the person has not touched the keyboard or mouse. Used to tell "at the PC" from "away".
public interface IUserIdle { TimeSpan IdleFor(); }

public sealed class NeverIdle : IUserIdle { public TimeSpan IdleFor() => TimeSpan.Zero; }

public sealed class Win32UserIdle : IUserIdle
{
    [StructLayout(LayoutKind.Sequential)] private struct LastInput { public uint Size; public uint Time; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput info);
    public TimeSpan IdleFor()
    {
        var info = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time));
    }
}
