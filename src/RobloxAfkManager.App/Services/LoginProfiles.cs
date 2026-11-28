using System.IO;

namespace RobloxAfkManager.App.Services;

// The login window keeps one browser profile per account, so Roblox remembers the device and the login stays valid.
public static class LoginProfiles
{
    public static string BrowserRoot => Path.Combine(AppFolders.Data, "browser");
    public static string ProfileFolder(long userId) => Path.Combine(BrowserRoot, userId.ToString());

    // When a saved login is removed, its browser profile goes too. A folder that is in use is left behind; that is harmless.
    public static void Forget(long userId)
    {
        try { var folder = ProfileFolder(userId); if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
