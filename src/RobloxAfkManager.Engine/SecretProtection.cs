namespace RobloxAfkManager.Engine;

// Encrypts saved secrets (login cookies, Discord webhook URLs) so only this Windows user can read them.
public interface ISecretProtector
{
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}

public sealed class SecretProtectionException(string message) : Exception(message);

// The protector used when none is passed in. The app (and the test runner) set it once at startup to the Windows DPAPI
// protector from Platform.Windows. With none set, saving or reading a secret fails instead of ever storing it readable.
public static class SecretProtection
{
    public static ISecretProtector? Default { get; set; }
    public static ISecretProtector Resolve(ISecretProtector? given) =>
        given ?? Default ?? throw new InvalidOperationException("No secret protector is configured. The app sets one at startup (WindowsPlatform.Install).");
}
