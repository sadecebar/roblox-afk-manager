using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RobloxAfkManager.App;
using RobloxAfkManager.App.ViewModels;
using RobloxAfkManager.Core;
using RobloxAfkManager.App.Controls;

namespace RobloxAfkManager.Tests;

internal static class UiSmoke
{
    public static void Run()
    {
        var application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/RobloxAfkManager;component/Themes/Dark.xaml", UriKind.Relative) });
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var listener = new BindingErrors();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        var window = new MainWindow(new MainViewModel(accountNameLookup: new AccountNameTests.EmptyLookup(), clientNameStore: new MemoryClientNameStore(), workspaceStore: new MemoryWorkspaceStore()));
        var vm = (MainViewModel)window.DataContext;
        try
        {
            var refresh = vm.RefreshAsync();
            PumpUntil(() => refresh.IsCompleted, TimeSpan.FromSeconds(15));
            refresh.GetAwaiter().GetResult();
            Program.Check(!vm.Warning, vm.Message);
            Program.Check(vm.Monitors.Count > 0, "No monitors discovered by live read-only refresh");
            vm.Dispose();
            Directory.CreateDirectory("artifacts");
            byte[] pixels = new byte[48 * 48 * 4];
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 120; pixels[i + 1] = 90; pixels[i + 2] = 50; pixels[i + 3] = 255; }
            var avatarEncoder = new PngBitmapEncoder(); avatarEncoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(48, 48, 96, 96, PixelFormats.Bgra32, null, pixels, 48 * 4)));
            using var avatarStream = new MemoryStream(); avatarEncoder.Save(avatarStream); var fixtureAvatar = avatarStream.ToArray();
            foreach (var (count, ignored, width, height, name) in new[] {
                (9, 1, 1920, 1000, "eight-visible"), (12, 0, 1920, 1000, "twelve"),
                (12, 0, 1280, 720, "twelve-laptop"), (13, 1, 1024, 700, "twelve-ignored"),
                (16, 0, 1280, 720, "sixteen"),
                (24, 0, 1920, 1000, "twenty-four"), (32, 0, 1280, 720, "thirty-two"),
                (16, 0, 640, 360, "compact"), (64, 0, 1920, 1000, "sixty-four"),
                (9, 9, 920, 600, "all-ignored"), (0, 0, 920, 600, "empty") })
            {
                var fake = new FakeWindowSystem(count);
                foreach (var id in fake.Bounds.Keys.ToArray()) fake.Bounds[id] = fake.Bounds[id] with { Width = 974, Height = 638 };
                using var preview = new MainViewModel(fake, new TestLog());
                var ready = preview.RefreshAsync(); PumpUntil(() => ready.IsCompleted, TimeSpan.FromSeconds(5)); ready.GetAwaiter().GetResult();
                foreach (var client in preview.Clients.Take(ignored)) client.IsIgnored = true;
                window.DataContext = preview;
                if (count == 12) preview.SetClientName(preview.Clients[0], "FixtureAccountWithALongNameForLayoutTesting");
                foreach (var client in preview.Clients)
                    client.SetAccountDetails(new(new(1000 + client.ProcessId, 8, 18, SessionPhase.Joined,
                        DateTimeOffset.UtcNow.AddMinutes(-75), DateTimeOffset.UtcNow.AddMinutes(-75), DateTimeOffset.UtcNow),
                        "Fixture" + client.Slot, "Fixture game with a long title to check clipping", fixtureAvatar));
                Render(window, width, height, $"artifacts/dashboard-{name}.png", count - ignored);
                if (count == 12)
                {
                    var fit = (RadioButton)window.FindName("FitModeButton");
                    var fill = (RadioButton)window.FindName("FillModeButton");
                    fit.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true); Pump();
                    Program.Check(preview.FitPreviews && !preview.FillPreviews && NativeSmoke.Descendants(window.Content as DependencyObject ?? window).OfType<LiveWindowPreview>().All(p => !p.FillPreview), "Fit control did not update every preview");
                    fill.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true); Pump();
                    Program.Check(preview.FillPreviews && !preview.FitPreviews && NativeSmoke.Descendants(window.Content as DependencyObject ?? window).OfType<LiveWindowPreview>().All(p => p.FillPreview), "Fill control did not update every preview");
                }
                if (count == 9 && ignored == 1)
                {
                    preview.UnignoreClientCommand.Execute(preview.IgnoredClients[0]);
                    Render(window, width, height, "artifacts/dashboard-nine.png", 9);
                    // Simulate new clients arriving without rebuilding the host window.
                    var id = new WindowIdentity(99, 999, 9999);
                    fake.Bounds[id] = new(0, 0, 974, 638); fake.Placements[id] = new(0, 1, 0, 0, 0, 0, fake.Bounds[id]);
                    ready = preview.RefreshAsync(); PumpUntil(() => ready.IsCompleted, TimeSpan.FromSeconds(5)); ready.GetAwaiter().GetResult();
                    Render(window, 920, 600, "artifacts/dashboard-added-client.png", 10);
                }
            }
            Program.Check(listener.Errors.Count == 0, string.Join(Environment.NewLine, listener.Errors));
            VerifyActivityPanel();
        }
        finally
        {
            vm.Dispose(); window.Close();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        }
    }

    private static void Render(Window window, int width, int height, string path, int expectedTiles)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        Pump();
        var dashboard = (ListBox)window.FindName("ClientDashboard");
        var tiles = NativeSmoke.Descendants(dashboard).OfType<ListBoxItem>().ToArray();
        Program.Check(tiles.Length == expectedTiles, $"Expected {expectedTiles} visible tiles, got {tiles.Length}");
        Program.Check(!NativeSmoke.Descendants(dashboard).OfType<ScrollViewer>().Any(), "Dashboard must not scroll");
        var viewport = new Rect(0, 0, dashboard.ActualWidth, dashboard.ActualHeight);
        viewport.Inflate(1, 1); // Layout rounding at fractional logical pixels.
        foreach (var tile in tiles)
        {
            var bounds = tile.TransformToAncestor(dashboard).TransformBounds(new Rect(tile.RenderSize));
            Program.Check(viewport.Contains(bounds), $"Tile {bounds} is outside viewport {viewport}");
            var live = NativeSmoke.Descendants(tile).OfType<LiveWindowPreview>().Single();
            Program.Check(live.ActualWidth > 20 && live.ActualHeight > 10, "A preview disappeared at this density");
            Program.Check(Math.Abs(live.ActualWidth - (tile.ActualWidth - DashboardLayout.HorizontalChrome)) <= 1, "Preview inset differs from the compact card geometry");
            var ignore = NativeSmoke.Descendants(tile).OfType<CheckBox>().Single();
            Program.Check(ignore.ActualWidth >= 50 && ignore.ActualHeight >= 16, "Ignore control is clipped");
            var show = NativeSmoke.Descendants(tile).OfType<Button>().Single(b => b.Name == "ShowClientButton");
            var edit = NativeSmoke.Descendants(tile).OfType<Button>().Single(b => b.Name == "EditClientNameButton");
            Program.Check(show.ActualWidth >= 20, "Show control has no usable hit target");
            var tileBounds = new Rect(tile.RenderSize); tileBounds.Inflate(1, 1);
            foreach (var action in new FrameworkElement[] { show, ignore, edit })
            {
                var actionBounds = action.TransformToAncestor(tile).TransformBounds(new Rect(action.RenderSize));
                Program.Check(tileBounds.Contains(actionBounds), "A card action escapes its tile");
                var previewBounds = live.TransformToAncestor(tile).TransformBounds(new Rect(live.RenderSize));
                Program.Check(actionBounds.Top >= previewBounds.Bottom, "Preview overlaps a card action");
            }
        }
        if (tiles.Length > 0)
        {
            var bounds = tiles.Select(t => t.TransformToAncestor(dashboard).TransformBounds(new Rect(t.RenderSize))).ToArray();
            foreach (var row in bounds.GroupBy(b => Math.Round(b.Top)))
            {
                Program.Check(row.Min(b => b.Left) <= 1 && Math.Abs(row.Max(b => b.Right) - dashboard.ActualWidth) <= 2,
                    "Client row leaves unused space at the left or right of the viewport");
            }
            Program.Check(Math.Abs(bounds.Max(b => b.Bottom) - dashboard.ActualHeight) <= 2, "Client grid leaves unused space below the last row");
        }
        var shellBounds = new Rect(0, 0, width, height); shellBounds.Inflate(1, 1);
        var modes = (FrameworkElement)window.FindName("PreviewModes");
        Program.Check(shellBounds.Contains(modes.TransformToAncestor(root).TransformBounds(new Rect(modes.RenderSize))), "Preview mode selector is clipped");
        foreach (string control in new[] { "ResourceButton", "MobileButton", "IgnoredButton", "RefreshButton", "RestoreWindowsButton", "ActivityButton" })
        {
            var button = (Button)window.FindName(control);
            Program.Check(shellBounds.Contains(button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize))), $"{control} is clipped at {width}x{height}");
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
        Console.WriteLine($"Rendered manager UI only: {path}");
    }
    internal static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void VerifyActivityPanel()
    {
        var store = new MemoryWorkspaceStore(); store.Write(new([], [new(DateTimeOffset.UtcNow, "Fixture account", "Window is no longer detected.", true)]));
        using var vm = new MainViewModel(new FakeWindowSystem(0), new TestLog(), workspaceStore: store);
        var host = new MainWindow(vm) { ShowActivated = false, WindowState = WindowState.Normal, Width = 900, Height = 600 };
        try
        {
            host.Show(); PumpUntil(() => !vm.Busy, TimeSpan.FromSeconds(5));
            ((Button)host.FindName("ActivityButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            var popup = (System.Windows.Controls.Primitives.Popup)host.FindName("ActivityPopup");
            Program.Check(popup.IsOpen && NativeSmoke.Descendants(popup.Child).OfType<TextBlock>().Any(t => t.Text == "Fixture account"), "Activity popup did not show stored history");
            var notifications = NativeSmoke.Descendants(popup.Child).OfType<CheckBox>().Single();
            notifications.IsChecked = false; Pump(); Program.Check(!store.Read().Notifications, "Notification preference did not save");
            NativeSmoke.Descendants(popup.Child).OfType<Button>().Single(b => Equals(b.Content, "Clear")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Program.Check(!vm.HasActivity && store.Read().History.Count == 0, "Clear did not remove stored history");
            popup.IsOpen = false;
        }
        finally { host.Close(); Pump(); }
    }
    internal static void PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.Elapsed > timeout) throw new TimeoutException("Desktop smoke test timed out"); Pump(); Thread.Sleep(25); }
    }
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
