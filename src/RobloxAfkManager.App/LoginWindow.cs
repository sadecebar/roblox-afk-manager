using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RobloxAfkManager.App.Services;

namespace RobloxAfkManager.App;

// Roblox's own login page in an embedded browser. Each account gets its own persistent browser profile, so it
// stays logged in like a normal browser would: the next visit finds the session already there and the window closes
// by itself. The session cookie goes straight into the encrypted login store. The profile is never logged out, so
// the cookie stays valid. You type your own username and password; the manager never sees or stores them.
public sealed class LoginWindow : Window
{
    public static string BrowserRoot => Path.Combine(AppFolders.Data, "browser");
    public static string ProfileFolder(long userId) => Path.Combine(BrowserRoot, userId.ToString());

    // The login window is for Roblox's own pages only. A link, redirect or pop-up that tries to open anything else is
    // refused, so a look-alike page can never appear inside a window that has just asked for a password.
    public static bool IsAllowedAddress(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.AbsoluteUri == "about:blank")
        && (uri.AbsoluteUri == "about:blank" || uri.Host == "roblox.com" || uri.Host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase));

    private readonly WebView2 web = new();
    private readonly TextBlock status = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xCD, 0xD6)), Margin = new Thickness(14, 10, 14, 10), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly AccountLauncher launcher = new(new WindowsRobloxClients());
    private readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromSeconds(2) };
    private string? lastTried;
    private bool done, checking;

    public string FolderPath { get; }
    public bool IsNewProfile { get; }
    public string? Cookie { get; private set; }
    public SavedLogin? Login { get; private set; }

    public LoginWindow(long? existingUserId)
    {
        IsNewProfile = existingUserId is null;
        FolderPath = existingUserId is { } id ? ProfileFolder(id) : Path.Combine(BrowserRoot, "new-" + Guid.NewGuid().ToString("N"));
        Title = existingUserId is null ? "Log in to Roblox: add an account" : "Log in to Roblox";
        Width = 1000; Height = 760; MinWidth = 640; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x11, 0x17));
        status.Text = existingUserId is null
            ? "Log in to the account you want to add. This window closes by itself once you're logged in. Don't press Log out."
            : "Checking this account's saved browser session. If Roblox asks you to log in, do it here.";
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.Children.Add(status);
        Grid.SetRow(web, 1);
        grid.Children.Add(web);
        Content = grid;
        web.CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = FolderPath };
        Loaded += async (_, _) => await StartAsync();
        poll.Tick += async (_, _) => await CheckAsync();
        Closed += (_, _) => { poll.Stop(); web.Dispose(); launcher.Dispose(); };
    }

    private async Task StartAsync()
    {
        try
        {
            Directory.CreateDirectory(BrowserRoot);
            await web.EnsureCoreWebView2Async();
            web.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (IsAllowedAddress(e.Uri)) return;
                e.Cancel = true; status.Text = "Only Roblox's own pages can be opened in this window.";
            };
            web.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true; // never open extra windows; stay on a Roblox page in this one
                if (IsAllowedAddress(e.Uri)) web.CoreWebView2.Navigate(e.Uri);
            };
            web.NavigationCompleted += async (_, _) => await CheckAsync();
            web.CoreWebView2.Navigate("https://www.roblox.com/login");
            poll.Start();
        }
        catch (Exception ex)
        {
            status.Text = "The embedded browser could not start (" + ex.Message + "). Install the Microsoft Edge WebView2 Runtime, or paste the cookie instead.";
        }
    }

    private async Task CheckAsync()
    {
        if (done || checking || web.CoreWebView2 is null) return;
        checking = true;
        try
        {
            var cookies = await web.CoreWebView2.CookieManager.GetCookiesAsync("https://www.roblox.com");
            string? raw = cookies.FirstOrDefault(c => c.Name == ".ROBLOSECURITY")?.Value;
            if (string.IsNullOrEmpty(raw) || raw == lastTried) return;
            lastTried = raw;
            if (AccountLauncher.CleanCookie(raw) is not { } clean) return;
            // A cookie left over from an old session isn't enough: only accept one Roblox says belongs to a logged-in user.
            try { Login = await launcher.IdentifyAsync(clean, CancellationToken.None); }
            catch (Exception) { status.Text = "Waiting for you to log in…"; return; }
            Cookie = clean; done = true; poll.Stop();
            Close();
        }
        catch (Exception) { /* The page is still loading; the next tick tries again. */ }
        finally { checking = false; }
    }

    // A new profile is renamed to its account id once the login is known, so that account finds it next time.
    public static async Task FinalizeFolderAsync(string folder, long userId)
    {
        string target = ProfileFolder(userId);
        if (string.Equals(Path.GetFullPath(folder), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) return;
        for (int attempt = 0; attempt < 15; attempt++)
        {
            try
            {
                if (Directory.Exists(target)) Directory.Delete(target, true);
                Directory.Move(folder, target);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Task.Delay(500); } // browser processes are still exiting
        }
    }

    public static async Task DiscardAsync(string folder)
    {
        for (int attempt = 0; attempt < 15; attempt++)
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Task.Delay(500); }
        }
    }
}
