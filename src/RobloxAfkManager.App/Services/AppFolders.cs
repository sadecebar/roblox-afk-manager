using System.IO;

namespace RobloxAfkManager.App.Services;

// Where the manager keeps its own files. Automated tests point ROBLOXAFK_DATA at a throwaway folder so that they can
// never read or write a real person's settings, saved logins or Discord webhook.
public static class AppFolders
{
    public const string OverrideVariable = "ROBLOXAFK_DATA";
    public static string Data => Environment.GetEnvironmentVariable(OverrideVariable) is { Length: > 0 } custom
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RobloxAfkManager");
}
