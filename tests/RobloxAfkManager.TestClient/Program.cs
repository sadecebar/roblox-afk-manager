using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RobloxAfkManager.TestClient;

internal static class Program
{
    private static byte[]? memoryLoad;
    [STAThread]
    private static void Main(string[] args)
    {
        var app = new Application();
        if (args.Contains("--memory-load"))
        {
            // Test-owned data for checking paging, never an allocation inside a real game.
            memoryLoad = new byte[80 * 1048576];
            for (int i = 0; i < memoryLoad.Length; i += 4096) memoryLoad[i] = 1;
        }
        var window = new Window
        {
            Title = "AFK Manager synthetic test window (not Roblox)", Width = 800, Height = 600,
            MinWidth = 600, MinHeight = 400, Left = 60, Top = 60, ShowActivated = args.Contains("--maximized"),
            Background = new SolidColorBrush(Color.FromRgb(30, 44, 62)), Foreground = Brushes.White,
            Content = new TextBlock { Text = "Synthetic window-management test\nThis is not a Roblox session.", FontSize = 24, Margin = new Thickness(30), TextWrapping = TextWrapping.Wrap }
        };
        if (args.Contains("--maximized")) window.WindowState = WindowState.Maximized;
        app.Run(window);
    }
}
