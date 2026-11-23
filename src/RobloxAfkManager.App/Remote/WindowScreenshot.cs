using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RobloxAfkManager.Core;

namespace RobloxAfkManager.App.Remote;

public interface IWindowScreenshot { byte[] Capture(WindowIdentity identity); }

// Used by the remote worker only. The desktop continues to use its existing DWM thumbnails.
public sealed class WindowScreenshot(Func<WindowIdentity, bool> isCurrent) : IWindowScreenshot
{
    public byte[] Capture(WindowIdentity identity)
    {
        if (!isCurrent(identity)) throw new InvalidOperationException("Window closed or replaced.");
        var pixels = new byte[640 * 360 * 4];
        int result = CaptureWindow(identity.Handle, identity.ProcessId,
            new DateTime(identity.StartTimeUtcTicks, DateTimeKind.Utc).ToFileTimeUtc(),
            pixels, pixels.Length, 640, 360, out int width, out int height);
        if (result < 0) throw new COMException("Windows could not capture this window.", result);
        if (!isCurrent(identity)) throw new InvalidOperationException("Window closed or replaced.");
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        source.Freeze();
        var encoder = new JpegBitmapEncoder { QualityLevel = 65 };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    [DllImport("RobloxAfkManager.Capture.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int CaptureWindow(nint hwnd, int pid, long started, byte[] pixels, int capacity,
        int maxWidth, int maxHeight, out int width, out int height);
}
