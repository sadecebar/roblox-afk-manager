using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using RobloxAfkManager.Core;

namespace RobloxAfkManager.App.Services;

// Query only. Never calls Restart Manager shutdown/restart operations or opens game memory.
public static class LogFileOwners
{
    public static IReadOnlyList<ProcessIdentity> Read(string path)
    {
        int result = RmStartSession(out uint session, 0, new StringBuilder(33));
        if (result != 0) throw new Win32Exception(result);
        try
        {
            result = RmRegisterResources(session, 1, [path], 0, null, 0, null);
            if (result != 0) throw new Win32Exception(result);
            uint count = 0;
            result = RmGetList(session, out uint needed, ref count, null, out _);
            for (int attempt = 0; result == 234 && attempt < 3; attempt++)
            {
                if (needed > 128) return [];
                var processes = new ProcessInfo[needed]; count = needed;
                result = RmGetList(session, out needed, ref count, processes, out _);
                if (result == 0)
                    return processes.Take((int)count).Select(p => new ProcessIdentity((int)p.Process.Id,
                        DateTime.FromFileTimeUtc(((long)(uint)p.Process.Start.dwHighDateTime << 32) | (uint)p.Process.Start.dwLowDateTime).Ticks)).ToArray();
            }
            if (result != 0) throw new Win32Exception(result);
            return [];
        }
        finally { _ = RmEndSession(session); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct UniqueProcess { public uint Id; public FILETIME Start; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ProcessInfo
    {
        public UniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceName;
        public uint AppType, Status, SessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmStartSession(out uint session, uint flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmRegisterResources(uint session, uint fileCount, string[] files, uint appCount, UniqueProcess[]? apps, uint serviceCount, string[]? services);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] ProcessInfo[]? processes, out uint reasons);
    [DllImport("rstrtmgr.dll")] private static extern int RmEndSession(uint session);
}
