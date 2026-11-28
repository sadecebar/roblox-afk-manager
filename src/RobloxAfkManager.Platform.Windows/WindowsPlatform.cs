using RobloxAfkManager.Engine;

namespace RobloxAfkManager.Platform.Windows;

// Plugs the Windows implementations into the engine's defaults. Called once when the app starts.
public static class WindowsPlatform
{
    public static void Install() => SecretProtection.Default ??= DpapiSecretProtector.Instance;
}
