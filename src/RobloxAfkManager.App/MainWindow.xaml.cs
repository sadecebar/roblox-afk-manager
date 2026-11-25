using System.Windows;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using RobloxAfkManager.App.Controls;
using RobloxAfkManager.App.ViewModels;

namespace RobloxAfkManager.App;
public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel;
    private bool restoringResources;
    private Services.DesktopAlerts? desktopAlerts;
    public MainWindow() : this(new MainViewModel()) { }
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = this.viewModel = viewModel;
        SettingsPopup.Opened += (_, _) => AnimateIn(SettingsPopup);
        foreach (var popup in new[] { IgnoredPopup, NamePopup, ActivityPopup, OfflinePopup })
        {
            popup.PopupAnimation = SystemParameters.ClientAreaAnimation ? PopupAnimation.Fade : PopupAnimation.None;
            popup.Opened += (_, _) =>
            {
                AnimateIn(popup);
                if (popup.Child is not Border border) return;
                double availableHeight = Math.Max(160, SystemParameters.WorkArea.Height - 80);
                if (border.Child is ScrollViewer existing) existing.MaxHeight = Math.Min(500, availableHeight);
                else
                {
                    var content = border.Child; border.Child = null;
                    border.Child = new ScrollViewer { Content = content, MaxHeight = Math.Min(500, availableHeight),
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
                }
            };
        }
        Loaded += async (_, _) => await viewModel.RefreshAsync();
        viewModel.AlertRaised += OnAlert;
        Closed += (_, _) => { viewModel.AlertRaised -= OnAlert; desktopAlerts?.Dispose(); viewModel.Dispose(); };
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape && (IgnoredPopup.IsOpen || SettingsPopup.IsOpen || NamePopup.IsOpen || ActivityPopup.IsOpen || OfflinePopup.IsOpen)) { IgnoredPopup.IsOpen = false; SettingsPopup.IsOpen = false; NamePopup.IsOpen = false; ActivityPopup.IsOpen = false; OfflinePopup.IsOpen = false; e.Handled = true; } };
    }
    // Panels ease in: a short fade and a few pixels of upward slide. Skipped when Windows animations are off.
    private static void AnimateIn(Popup popup)
    {
        if (!SystemParameters.ClientAreaAnimation || popup.Child is not FrameworkElement child) return;
        var shift = new System.Windows.Media.TranslateTransform(0, 10);
        child.RenderTransform = shift; child.Opacity = 0;
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease };
        fade.Completed += (_, _) => { child.BeginAnimation(UIElement.OpacityProperty, null); child.Opacity = 1; };
        child.BeginAnimation(UIElement.OpacityProperty, fade);
        shift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(210)) { EasingFunction = ease });
    }
    private void WorkspaceRoot_SizeChanged(object sender, SizeChangedEventArgs e) => WorkspaceAppearance.SetIsCompact(WorkspaceRoot, e.NewSize.Width < 980);
    private void TogglePanel(Popup target)
    {
        bool open = !target.IsOpen;
        IgnoredPopup.IsOpen = SettingsPopup.IsOpen = NamePopup.IsOpen = ActivityPopup.IsOpen = OfflinePopup.IsOpen = false;
        target.IsOpen = open;
    }
    private async void LoginNew_Click(object sender, RoutedEventArgs e) => await RunLoginAsync(null);
    private async void LoginRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SavedLoginView row }) await RunLoginAsync(row.UserId, row.Name);
    }
    private async Task RunLoginAsync(long? userId, string? username = null)
    {
        OfflinePopup.IsOpen = false;
        var window = new LoginWindow(userId) { Owner = this };
        window.ShowDialog();
        if (window.Cookie is { } cookie && window.Login is { } login)
        {
            if (await viewModel.AddLoginAsync(cookie)) await LoginWindow.FinalizeFolderAsync(window.FolderPath, login.UserId);
        }
        else if (window.IsNewProfile) await LoginWindow.DiscardAsync(window.FolderPath); // closed without logging in
    }
    private void SaveDiscord_Click(object sender, RoutedEventArgs e)
    {
        string url = DiscordUrlBox.Password; DiscordUrlBox.Clear();
        viewModel.SaveDiscord(url, DiscordUserBox.Text);
    }
    private void ClearDiscord_Click(object sender, RoutedEventArgs e) { DiscordUserBox.Clear(); viewModel.ClearDiscord(); }
    private void Backup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Back up manager settings", FileName = "RobloxAfkManager-settings.json", Filter = "Settings backup (*.json)|*.json" };
        if (dialog.ShowDialog(this) == true) viewModel.BackupSettings(dialog.FileName);
    }
    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Restore manager settings", Filter = "Settings backup (*.json)|*.json" };
        if (dialog.ShowDialog(this) == true) viewModel.RestoreSettings(dialog.FileName);
    }
    private async void AddLogin_Click(object sender, RoutedEventArgs e)
    {
        string cookie = CookieBox.Password; CookieBox.Clear();
        await viewModel.AddLoginAsync(cookie);
    }
    private void AlertsChip_Click(object sender, RoutedEventArgs e)
    {
        SettingsTabAlerts.IsChecked = true;
        if (!SettingsPopup.IsOpen) TogglePanel(SettingsPopup);
    }
    private void ReopenChip_Click(object sender, RoutedEventArgs e) => viewModel.ToggleReopen();
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => TogglePanel(SettingsPopup);
    private void AwayChip_Click(object sender, RoutedEventArgs e) => viewModel.ToggleSleep();
    private void ClearMain_Click(object sender, RoutedEventArgs e) => viewModel.MainClient = null;
    private void ArrangeNow_Click(object sender, RoutedEventArgs e) => SettingsPopup.IsOpen = false;
    private void IgnoredButton_Click(object sender, RoutedEventArgs e) => TogglePanel(IgnoredPopup);
    private void ActivityButton_Click(object sender, RoutedEventArgs e) { TogglePanel(ActivityPopup); if (ActivityPopup.IsOpen) viewModel.MarkActivityRead(); }
    private void OfflineButton_Click(object sender, RoutedEventArgs e) => TogglePanel(OfflinePopup);
    private void CopyOfflineName_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: OfflineAccountView account }) return;
        try { Clipboard.SetText(account.Name); viewModel.ReportCopied(account.Name); }
        catch (COMException) { /* Clipboard busy: the name stays visible in the list. */ }
    }
    private void ClearActivity_Click(object sender, RoutedEventArgs e) => viewModel.ClearActivity();
    private void OnAlert(Core.ActivityEntry entry) => desktopAlerts?.Show(entry);
    private void CopyMobileCode_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(viewModel.Remote.AccessCode); }
        catch (COMException) { /* Clipboard busy: the read-only code field also supports manual copy. */ }
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        desktopAlerts = new(handle);
        int enabled = 1;
        _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
        int captionColor = 0x000A0707; // COLORREF (BGR) for #07070A, matching the workspace canvas on Windows 11.
        _ = DwmSetWindowAttribute(handle, 35, ref captionColor, sizeof(int));
        HwndSource.FromHwnd(handle)?.AddHook(WindowMessage);
    }
    private nint WindowMessage(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg is 0x7E or 0x1A or 0x2E0) viewModel.NotifyDisplayChanged();
        return 0;
    }
    protected override async void OnClosing(CancelEventArgs e)
    {
        if (viewModel.Busy || viewModel.Remote.Busy || restoringResources) { e.Cancel = true; return; }
        if (viewModel.Remote.Running)
        {
            e.Cancel = true; restoringResources = true;
            try { await viewModel.Remote.StopAsync(); }
            finally { restoringResources = false; }
            Close(); return;
        }
        if (viewModel.HasResourceChanges)
        {
            e.Cancel = true; restoringResources = true;
            try { if (await viewModel.StopResourcePlansAsync(forgetPreferences: false)) { restoringResources = false; Close(); } }
            finally { restoringResources = false; }
            return;
        }
        base.OnClosing(e);
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
