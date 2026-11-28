using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RobloxAfkManager.Core;

namespace RobloxAfkManager.Platform.Windows;

public sealed class Win32ProcessResources : IProcessResourceSystem, IWorkingSetSystem
{
    public IProcessResourceHandle Open(ProcessIdentity process) => new ResourceHandle(OpenChecked(process, 0x1000 | 0x0200));
    public IWorkingSetHandle OpenWorkingSet(ProcessIdentity process) => new WorkingSetHandle(OpenChecked(process, 0x1000 | 0x0100));
    public bool IsAlive(ProcessIdentity process)
    {
        try { using var handle = OpenChecked(process, 0x1000); return true; }
        catch (ProcessGoneException) { return false; }
        catch (Win32Exception) { return true; } // Access denied is not proof of exit; keep the saved original.
    }
    private static SafeProcessHandle OpenChecked(ProcessIdentity process, uint access)
    {
        var handle = OpenProcess(access, false, (uint)process.ProcessId);
        try
        {
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 87) throw new ProcessGoneException("Client process has closed.");
                throw new Win32Exception(error, "Windows denied access to this client's resource settings.");
            }
            if (!GetProcessTimes(handle, out long created, out long exited, out _, out _)) throw Error("Read process identity");
            if (exited != 0 || DateTime.FromFileTimeUtc(created).Ticks != process.StartTimeUtcTicks)
                throw new ProcessGoneException("Client process closed or its PID was reused.");
            var path = new StringBuilder(32768); int length = path.Capacity;
            if (!QueryFullProcessImageName(handle, 0, path, ref length)) throw Error("Verify process image");
            if (!string.Equals(Path.GetFileName(path.ToString()), "RobloxPlayerBeta.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Resource changes are restricted to detected Roblox clients.");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }
    private sealed class ResourceHandle(SafeProcessHandle handle) : IProcessResourceHandle
    {
        public ResourceState Read()
        {
            uint priority = GetPriorityClass(handle);
            if (priority == 0) throw Error("Read CPU priority");
            var memory = new MemoryPriority(); var power = new PowerThrottling { Version = 1 };
            if (!GetMemoryInformation(handle, 0, ref memory, 4)) throw Error("Read memory priority");
            if (!GetPowerInformation(handle, 4, ref power, 12)) throw Error("Read power policy");
            return new(priority, memory.Value, power.ControlMask, power.StateMask);
        }
        public void Write(ResourceState state)
        {
            // Attempt every field even during restoration, so one denied field cannot prevent other restores.
            var errors = new List<string>();
            if (!SetPriorityClass(handle, state.PriorityClass)) errors.Add(Error("Set CPU priority").Message);
            var memory = new MemoryPriority { Value = state.MemoryPriority };
            if (!SetMemoryInformation(handle, 0, in memory, 4)) errors.Add(Error("Set memory priority").Message);
            var power = new PowerThrottling { Version = 1, ControlMask = state.PowerControl, StateMask = state.PowerState };
            if (!SetPowerInformation(handle, 4, in power, 12)) errors.Add(Error("Set power policy").Message);
            if (errors.Count > 0) throw new InvalidOperationException(string.Join("; ", errors));
        }
        public void Dispose() => handle.Dispose();
    }
    private sealed class WorkingSetHandle(SafeProcessHandle handle) : IWorkingSetHandle
    {
        public WorkingSetLimits ReadLimits()
        {
            if (!GetProcessWorkingSetSizeEx(handle, out nuint minimum, out nuint maximum, out uint flags)) throw Error("Read resident RAM limits");
            return new((ulong)minimum, (ulong)maximum, flags);
        }
        public void WriteLimits(WorkingSetLimits limits)
        {
            if (!SetProcessWorkingSetSizeEx(handle, checked((nuint)limits.MinimumBytes), checked((nuint)limits.MaximumBytes), limits.NativeFlags)) throw Error("Set resident RAM limits");
        }
        public void Dispose() => handle.Dispose();
    }
    private static Win32Exception Error(string action) => new(Marshal.GetLastWin32Error(), action + " failed");
    [StructLayout(LayoutKind.Sequential)] private struct MemoryPriority { public uint Value; }
    [StructLayout(LayoutKind.Sequential)] private struct PowerThrottling { public uint Version, ControlMask, StateMask; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle handle, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle handle, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetPriorityClass(SafeProcessHandle handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessWorkingSetSizeEx(SafeProcessHandle handle, out nuint minimum, out nuint maximum, out uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetProcessWorkingSetSizeEx(SafeProcessHandle handle, nuint minimum, nuint maximum, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetPriorityClass(SafeProcessHandle handle, uint priority);
    [DllImport("kernel32.dll", EntryPoint = "GetProcessInformation", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMemoryInformation(SafeProcessHandle handle, int kind, ref MemoryPriority value, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetProcessInformation", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetPowerInformation(SafeProcessHandle handle, int kind, ref PowerThrottling value, uint size);
    [DllImport("kernel32.dll", EntryPoint = "SetProcessInformation", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetMemoryInformation(SafeProcessHandle handle, int kind, in MemoryPriority value, uint size);
    [DllImport("kernel32.dll", EntryPoint = "SetProcessInformation", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetPowerInformation(SafeProcessHandle handle, int kind, in PowerThrottling value, uint size);
}
