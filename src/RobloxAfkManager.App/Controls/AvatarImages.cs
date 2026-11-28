using System.IO;
using System.Windows.Media.Imaging;

namespace RobloxAfkManager.App.Controls;

// Turns the avatar PNG the engine downloaded into a small frozen WPF image for the cards.
public static class AvatarImages
{
    public static BitmapSource? Decode(byte[]? bytes)
    {
        if (bytes is null || !PublicAccountArtwork.ValidPng(bytes)) return null;
        try
        {
            using var stream = new MemoryStream(bytes, false);
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 48; image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or InvalidOperationException or FormatException or System.Runtime.InteropServices.COMException) { return null; }
    }
}
