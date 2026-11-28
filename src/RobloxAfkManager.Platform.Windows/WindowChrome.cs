using System.Runtime.InteropServices;

namespace RobloxAfkManager.Platform.Windows;

// The manager's own window frame on Windows 11: a dark title bar in the theme's canvas colour.
public static class WindowChrome
{
    private const int UseImmersiveDarkMode = 20, CaptionColor = 35;

    // red, green, blue: the caption colour. Older Windows versions ignore what they don't support.
    public static void UseDarkTitleBar(nint window, byte red, byte green, byte blue)
    {
        int enabled = 1;
        _ = DwmSetWindowAttribute(window, UseImmersiveDarkMode, ref enabled, sizeof(int));
        int colorRef = red | (green << 8) | (blue << 16); // COLORREF is 0x00BBGGRR
        _ = DwmSetWindowAttribute(window, CaptionColor, ref colorRef, sizeof(int));
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
