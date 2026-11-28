using System.Runtime.InteropServices;

namespace RobloxAfkManager.Engine;

// Encrypts saved secrets (login cookies, Discord webhook URLs) so only this Windows user can read them.
public interface ISecretProtector
{
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}

public sealed class SecretProtectionException(string message) : Exception(message);

// Windows DPAPI, current user only. Moves to Platform.Windows in M2 (docs/ARCHITECTURE-PLAN.md).
public sealed class DpapiSecretProtector : ISecretProtector
{
    public static readonly DpapiSecretProtector Instance = new();
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public nint Data; }
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptProtectData(ref Blob input, string? description, nint entropy, nint reserved, nint prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
    private static byte[] Transform(byte[] data, bool protect)
    {
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new Blob { Size = data.Length, Data = handle.AddrOfPinnedObject() };
            Blob output;
            bool ok = protect ? CryptProtectData(ref input, null, 0, 0, 0, 1 /* UI forbidden */, out output) : CryptUnprotectData(ref input, 0, 0, 0, 0, 1, out output);
            if (!ok) throw new SecretProtectionException("Windows could not protect or unprotect the saved login.");
            var bytes = new byte[output.Size];
            Marshal.Copy(output.Data, bytes, 0, output.Size);
            LocalFree(output.Data);
            return bytes;
        }
        finally { handle.Free(); }
    }
    public byte[] Protect(byte[] data) => Transform(data, true);
    public byte[] Unprotect(byte[] data) => Transform(data, false);
}
