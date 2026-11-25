using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RobloxAfkManager.App.Services;

// Roblox clients share a named event, ROBLOX_singletonEvent. When a new client starts it signals that event, and the
// client that is already running leaves its game. Closing that one handle inside a running client (what Process
// Explorer's "Close Handle" does) stops it from hearing the signal, so several clients can stay open.
// Nothing is injected and no memory is read or written: it only closes that single handle in RobloxPlayerBeta.exe.
public static class RobloxSingleton
{
    private const string EventName = "ROBLOX_singletonEvent";
    private const uint DuplicateCloseSource = 1, DuplicateSameAccess = 2, ProcessDupHandle = 0x40;
    private const int SystemExtendedHandleInformation = 64, ObjectNameInformation = 1;

    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int cls, nint buffer, int length, out int needed);
    [DllImport("ntdll.dll")] private static extern int NtQueryObject(nint handle, int cls, nint buffer, int length, out int needed);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DuplicateHandle(nint sourceProcess, nint sourceHandle, nint targetProcess, out nint target, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateEvent(nint attributes, bool manual, bool initial, string? name);

    [StructLayout(LayoutKind.Sequential)]
    private struct HandleEntry { public nint Object; public nint ProcessId; public nint Handle; public uint Access; public ushort Backtrace; public ushort TypeIndex; public uint Attributes; public uint Reserved; }

    // Closes ROBLOX_singletonEvent in every running Roblox client. Returns how many handles were closed.
    public static int Release()
    {
        var pids = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName("RobloxPlayerBeta")) using (process) pids.Add(process.Id);
        if (pids.Count == 0) return 0;

        // Event handles are the only ones we ever ask for a name, so find the event type index with an event of our own.
        nint own = CreateEvent(0, false, false, null);
        if (own == 0) return 0;
        try
        {
            var entries = Handles();
            if (entries.Count == 0) return 0;
            int me = Environment.ProcessId;
            ushort eventType = entries.FirstOrDefault(e => e.ProcessId == me && e.Handle == own).TypeIndex;
            if (eventType == 0) return 0;

            int closed = 0;
            foreach (int pid in pids)
            {
                nint source = OpenProcess(ProcessDupHandle, false, pid);
                if (source == 0) continue;
                try
                {
                    foreach (var entry in entries.Where(e => e.ProcessId == pid && e.TypeIndex == eventType))
                    {
                        if (!DuplicateHandle(source, entry.Handle, Process.GetCurrentProcess().Handle, out nint copy, 0, false, DuplicateSameAccess)) continue;
                        bool match;
                        try { match = NameOf(copy)?.EndsWith(EventName, StringComparison.OrdinalIgnoreCase) == true; }
                        finally { CloseHandle(copy); }
                        if (match && DuplicateHandle(source, entry.Handle, 0, out _, 0, false, DuplicateCloseSource)) closed++;
                    }
                }
                finally { CloseHandle(source); }
            }
            return closed;
        }
        finally { CloseHandle(own); }
    }

    private static List<HandleEntry> Handles()
    {
        int size = 1 << 20;
        for (int attempt = 0; attempt < 12; attempt++)
        {
            nint buffer = Marshal.AllocHGlobal(size);
            try
            {
                int status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, size, out int needed);
                if (status == unchecked((int)0xC0000004)) { size = Math.Max(size * 2, needed + (1 << 16)); continue; }
                if (status != 0) return [];
                long count = Marshal.ReadIntPtr(buffer).ToInt64();
                var list = new List<HandleEntry>((int)Math.Min(count, 1_000_000));
                int stride = Marshal.SizeOf<HandleEntry>();
                for (long i = 0; i < count; i++) list.Add(Marshal.PtrToStructure<HandleEntry>(buffer + 2 * nint.Size + (nint)(i * stride)));
                return list;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return [];
    }

    private static string? NameOf(nint handle)
    {
        const int size = 1024;
        nint buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (NtQueryObject(handle, ObjectNameInformation, buffer, size, out _) != 0) return null;
            int length = Marshal.ReadInt16(buffer);
            nint text = Marshal.ReadIntPtr(buffer + nint.Size);
            return length == 0 || text == 0 ? null : Marshal.PtrToStringUni(text, length / 2);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
