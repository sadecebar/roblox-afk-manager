using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using RobloxAfkManager.App.Services;
using RobloxAfkManager.Core;
using RobloxAfkManager.App.Controls;
using System.Windows.Controls;
using System.Windows.Media;
using RobloxAfkManager.App;
using RobloxAfkManager.App.ViewModels;

namespace RobloxAfkManager.Tests;

internal static class NativeSmoke
{
    public static void RealPreviews()
    {
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        var application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (!application.Resources.Contains("CanvasBrush")) application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/RobloxAfkManager;component/Themes/Dark.xaml", UriKind.Relative) });
        var windows = new Win32WindowSystem(new TestLog());
        var before = windows.Detect().ToDictionary(c => c.Identity, c => windows.Capture(c.Identity));
        Program.Check(before.Count > 0, "No real Roblox clients are available for this optional check");
        var realResources = new Win32ProcessResources();
        var resourceBefore = new Dictionary<ProcessIdentity, ResourceState>();
        var workingSetBefore = new Dictionary<ProcessIdentity, WorkingSetLimits>();
        foreach (var id in before.Keys)
        {
            try { using var resource = realResources.Open(ProcessIdentity.From(id)); resourceBefore[ProcessIdentity.From(id)] = resource.Read().Normalized; }
            catch (Exception ex) { Console.WriteLine($"Read-only resource query unavailable for PID {id.ProcessId}: {ex.Message}"); }
            try { using var memory = realResources.OpenWorkingSet(ProcessIdentity.From(id)); workingSetBefore[ProcessIdentity.From(id)] = memory.ReadLimits().Normalized; }
            catch (Exception ex) { Console.WriteLine($"Resident-limit access unavailable for PID {id.ProcessId}: {ex.Message}"); }
        }
        using var vm = new MainViewModel(windows, new TestLog(), accountNameLookup: new AccountNameTests.EmptyLookup(), clientNameStore: new MemoryClientNameStore(), workspaceStore: new MemoryWorkspaceStore());
        var host = new MainWindow(vm) { ShowActivated = false, WindowState = WindowState.Normal };
        try
        {
            host.Show();
            UiSmoke.PumpUntil(() => !vm.Busy && vm.ClientCount == before.Count, TimeSpan.FromSeconds(10));
            var previews = Descendants(host).OfType<LiveWindowPreview>().ToArray();
            Program.Check(previews.Length == before.Count, "Not every real client has a dashboard tile");
            var eligible = previews.Where(p => p.CanPreview).ToArray();
            UiSmoke.PumpUntil(() => eligible.All(p => p.HasLiveThumbnail && p.DisplayedBounds is { Width: > 0, Height: > 0 }), TimeSpan.FromSeconds(10));
            foreach (var preview in eligible) VerifyFilledSurface(preview);
            vm.FillPreviews = false; UiSmoke.Pump();
            Program.Check(eligible.All(p => !p.FillPreview && p.DisplayedSourceBounds is { X: 0, Y: 0 }), "Fit mode did not restore full-source previews");
            vm.FillPreviews = true; UiSmoke.Pump();
            foreach (var preview in eligible) VerifyFilledSurface(preview);
            var ignored = vm.Clients[0];
            var oldPreview = previews.Single(p => p.Identity == ignored.Identity);
            ignored.IsIgnored = true;
            UiSmoke.PumpUntil(() => !oldPreview.HasLiveThumbnail && Descendants(host).OfType<LiveWindowPreview>().Count() == before.Count - 1, TimeSpan.FromSeconds(5));
            // Exercise the real popup and its bound Show command without touching the source game.
            var menu = (Button)host.FindName("IgnoredButton"); menu.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); UiSmoke.Pump();
            var popup = (System.Windows.Controls.Primitives.Popup)host.FindName("IgnoredPopup");
            Program.Check(popup.IsOpen, "Ignored menu did not open");
            var show = Descendants(popup.Child).OfType<Button>().Single(b => ReferenceEquals(b.CommandParameter, ignored));
            Program.Check(show.Command?.CanExecute(show.CommandParameter) == true, "Show ignored command is unavailable");
            show.Command!.Execute(show.CommandParameter); popup.IsOpen = false;
            UiSmoke.PumpUntil(() => Descendants(host).OfType<LiveWindowPreview>().Count() == before.Count, TimeSpan.FromSeconds(5));
            var restoredPreview = Descendants(host).OfType<LiveWindowPreview>().Single(p => p.Identity == ignored.Identity);
            if (restoredPreview.CanPreview) UiSmoke.PumpUntil(() => restoredPreview.HasLiveThumbnail && restoredPreview.DisplayedBounds is { Width: > 0, Height: > 0 }, TimeSpan.FromSeconds(5));
            foreach (var (id, placement) in before)
                Program.Check(windows.Capture(id) == placement, "A source placement changed during the read-only preview check");
            foreach (var (id, state) in resourceBefore)
            {
                using var resource = realResources.Open(id);
                Program.Check(resource.Read().Normalized == state, "Read-only dashboard changed a real client's resource settings");
            }
            Console.WriteLine($"Read-only resource query: {resourceBefore.Count}/{before.Count} real clients readable; no resource preset applied to real clients.");
            foreach (var (id, limits) in workingSetBefore)
            {
                using var memory = realResources.OpenWorkingSet(id);
                Program.Check(memory.ReadLimits().Normalized == limits, "Read-only dashboard changed a real client's resident limits");
            }
            Console.WriteLine($"Resident-limit query/access: {workingSetBefore.Count}/{before.Count} real clients available; limits unchanged, no RAM cap applied to real games.");
            Console.WriteLine($"Read-only real-client check: {previews.Length} tiles, {eligible.Length} native preview relationships; Fill covers each preview and Fit restores full-source views; ignore released its preview and the Ignored menu restored it; source placements/show states unchanged. Pixel animation/rejoin behavior was not assessed.");
        }
        finally
        {
            UiSmoke.PumpUntil(() => !vm.Busy, TimeSpan.FromSeconds(5));
            host.Close(); UiSmoke.Pump();
        }
    }
    internal static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    public static void PackageStartup() => ObservePackage(1);
    public static void PackageLongObservation() => ObservePackage(3);
    private static void ObservePackage(int samples)
    {
        string version = typeof(MainWindow).Assembly.GetName().Version!.ToString(3);
        string path = Path.GetFullPath($"artifacts/RobloxAfkManager-v{version}-win-x64/RobloxAfkManager.exe");
        Program.Check(File.Exists(path), "Publish the app first with build.ps1 -Publish");
        using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden })!;
        try
        {
            UiSmoke.PumpUntil(() => { process.Refresh(); return process.HasExited || process.MainWindowHandle != 0; }, TimeSpan.FromSeconds(10));
            Program.Check(!process.HasExited, "The packaged manager exited during startup");
            var warmup = Stopwatch.StartNew();
            UiSmoke.PumpUntil(() => warmup.ElapsedMilliseconds >= 2500, TimeSpan.FromSeconds(5));
            for (int sample = 1; sample <= samples; sample++)
            {
                process.Refresh(); var cpuStart = process.TotalProcessorTime; int handlesStart = process.HandleCount;
                var observation = Stopwatch.StartNew();
                UiSmoke.PumpUntil(() => observation.ElapsedMilliseconds >= 12000, TimeSpan.FromSeconds(15));
                process.Refresh(); Program.Check(!process.HasExited, "The packaged manager exited during the idle observation");
                Console.WriteLine($"Packaged manager sample {sample}: {process.WorkingSet64 / 1048576d:F1} MiB working set; {(process.TotalProcessorTime - cpuStart).TotalMilliseconds:F1} ms CPU over {observation.Elapsed.TotalSeconds:F1}s; handles {handlesStart} -> {process.HandleCount}.");
            }
            Program.Check(process.CloseMainWindow(), "Could not request normal package shutdown");
            Program.Check(process.WaitForExit(5000), "Manager did not close normally");
            Program.Check(process.ExitCode == 0, "Manager returned a failure exit code");
        }
        finally
        {
            if (!process.HasExited) { process.CloseMainWindow(); if (!process.WaitForExit(3000)) process.Kill(); }
        }
    }

    public static void Run()
    {
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string configuration = AppContext.BaseDirectory.Contains("Release", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        string fixture = Path.GetFullPath($"tests/RobloxAfkManager.TestClient/bin/{configuration}/net10.0-windows/RobloxPlayerBeta.exe");
        Program.Check(File.Exists(fixture), "Build the synthetic TestClient project first.");
        var log = new TestLog(); var windows = new Win32WindowSystem(log); var manager = new WindowManager(windows, log);
        var owned = new List<Process>();
        using var monitor = new WindowMonitor(windows, log, () => { });
        var unrelated = new Window { Title = "Unrelated test sentinel", Width = 640, Height = 480, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            // Only these test-owned process IDs are ever passed to mutation methods.
            foreach (var argument in new[] { "--memory-load", "--maximized" })
                owned.Add(Process.Start(new ProcessStartInfo(fixture, argument) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden })!);
            IReadOnlyList<ClientWindow> clients = [];
            UiSmoke.PumpUntil(() =>
            {
                clients = windows.Detect().Where(c => owned.Any(p => p.Id == c.Identity.ProcessId)).ToArray();
                return clients.Count == 2;
            }, TimeSpan.FromSeconds(15));
            var screen = windows.GetMonitors().First(m => m.IsPrimary);
            VerifyResidentCaps(clients);
            VerifyResourcePlans(clients);
            VerifyResourcePanelAndClose(windows, clients);
            VerifyWindowCapture(windows, clients[0]);
            VerifyRemotePanel();
            var originals = clients.ToDictionary(c => c.Identity, c => windows.Capture(c.Identity));
            Console.WriteLine($"Native monitor: {screen.Label}, {screen.Details}");
            foreach (var c in clients) Console.WriteLine($"Synthetic PID {c.Identity.ProcessId}, limits {windows.GetLimits(c.Identity, screen.Dpi)}");
            unrelated.Show();
            var sentinel = new WindowInteropHelper(unrelated).Handle;
            Program.Check(windows.Detect().All(c => c.Identity.Handle != sentinel), "An unrelated application was detected");
            Program.Check(!windows.IsCurrent(new(sentinel, Environment.ProcessId, Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks)), "Unrelated handle passed validation");
            var bad = manager.Arrange(clients, screen, new(32, 32));
            Program.Check(!bad.Success && manager.OriginalCount == 0, "Impossible layout was not rejected before mutation");
            var arranged = manager.Arrange(clients, screen, new(2, 1));
            Program.Check(arranged.Success, arranged.Message);
            Program.Check(clients.All(c => windows.IsNormal(c.Identity)), "A test window is minimized or maximized after arrangement");
            var plan = manager.LastGrid!;
            Program.Check(plan.Targets.All(t => windows.ReadBounds(t.Identity) == t.Bounds));
            var focus = manager.Focus(clients[0].Identity);
            Console.WriteLine("Foreground attempt: " + focus.Message); // OS may legitimately deny it.
            var restoreGrid = manager.RestoreGrid(); Program.Check(restoreGrid.Success, restoreGrid.Message);
            VerifyLivePreviews(windows, clients);
            var restored = manager.RestoreOriginals(); Program.Check(restored.Success, restored.Message);
            foreach (var (id, original) in originals)
            {
                var current = windows.Capture(id);
                Program.Check(current.NormalBounds == original.NormalBounds && current.ShowCommand == (original.ShowCommand == 3 ? 3u : 1u), "Original placement differs");
            }
            var closedId = clients[0].Identity;
            var closing = owned.First(p => p.Id == closedId.ProcessId); closing.CloseMainWindow();
            UiSmoke.PumpUntil(() => closing.HasExited, TimeSpan.FromSeconds(5));
            UiSmoke.Pump();
            Program.Check(!windows.IsCurrent(closedId), "Closed identity remains valid");
            Program.Check(!manager.Focus(closedId).Success, "Closed client accepted focus");
            Program.Check(windows.Detect().Count(c => owned.Any(p => p.Id == c.Identity.ProcessId)) == 1);
        }
        finally
        {
            unrelated.Close();
            foreach (var process in owned)
            {
                if (!process.HasExited) { process.CloseMainWindow(); if (!process.WaitForExit(3000)) process.Kill(); }
                process.Dispose();
            }
        }
    }

    private static void VerifyWindowCapture(Win32WindowSystem windows, ClientWindow client)
    {
        var before = windows.Capture(client.Identity);
        var source = new RobloxAfkManager.App.Remote.WindowScreenshot(windows.IsCurrent);
        var capture = Task.Run(() => source.Capture(client.Identity));
        UiSmoke.PumpUntil(() => capture.IsCompleted, TimeSpan.FromSeconds(10));
        var jpeg = capture.GetAwaiter().GetResult();
        using var stream = new MemoryStream(jpeg);
        var image = System.Windows.Media.Imaging.BitmapFrame.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        Program.Check(image.PixelWidth is > 0 and <= 640 && image.PixelHeight is > 0 and <= 360, "Unexpected capture dimensions");
        Program.Check(jpeg.Length > 1000, "Capture did not contain a meaningful image");
        Program.Check(windows.Capture(client.Identity) == before, "Capture changed the source placement");
        Directory.CreateDirectory("artifacts"); File.WriteAllBytes("artifacts/remote-synthetic-preview.jpg", jpeg);
        bool refused = false;
        try { source.Capture(client.Identity with { StartTimeUtcTicks = client.Identity.StartTimeUtcTicks + 1 }); }
        catch (InvalidOperationException) { refused = true; }
        Program.Check(refused, "Capture accepted a stale identity");
        Console.WriteLine($"Windows Graphics Capture: owned covered window, {image.PixelWidth}x{image.PixelHeight}, {jpeg.Length:N0} JPEG bytes; placement unchanged, stale identity refused.");
    }

    private static void VerifyRemotePanel()
    {
        using var vm = new MainViewModel(new FakeWindowSystem(0), new TestLog());
        var host = new MainWindow(vm) { ShowActivated = false, WindowState = WindowState.Normal };
        try
        {
            host.Show(); UiSmoke.PumpUntil(() => !vm.Busy, TimeSpan.FromSeconds(5));
            ((Button)host.FindName("MobileButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); UiSmoke.Pump();
            var popup = (System.Windows.Controls.Primitives.Popup)host.FindName("MobilePopup");
            Program.Check(popup.IsOpen);
            var start = Descendants(popup.Child).OfType<Button>().Single(b => Equals(b.Content, "Start monitoring"));
            Program.Check(start.Command?.CanExecute(null) == true); start.Command!.Execute(null);
            UiSmoke.PumpUntil(() => !vm.Remote.Busy, TimeSpan.FromSeconds(10));
            Program.Check(vm.Remote.Running && vm.Remote.AccessCode.Length == 48, vm.Remote.Status);
            var codeField = Descendants(popup.Child).OfType<TextBox>().Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Dashboard access code");
            Program.Check(codeField.Text == vm.Remote.AccessCode && codeField.IsReadOnly);
            popup.IsOpen = false;
            host.Close(); UiSmoke.PumpUntil(() => !host.IsVisible && !vm.Remote.Running, TimeSpan.FromSeconds(10));
            Console.WriteLine("Mobile panel: bound Start/code display and normal-close server shutdown passed; no viewer or game capture was started.");
        }
        finally { host.Close(); }
    }

    private static void VerifyResourcePlans(IReadOnlyList<ClientWindow> clients)
    {
        var system = new Win32ProcessResources();
        var manager = new ResourceProfileManager(system, new TestLog());
        var originals = new Dictionary<ProcessIdentity, ResourceState>();
        foreach (var client in clients)
        {
            var id = ProcessIdentity.From(client.Identity); using var process = system.Open(id); originals[id] = process.Read().Normalized;
        }
        try
        {
            foreach (var plan in Enum.GetValues<ResourcePlan>())
            {
                var applied = manager.Apply(plan, originals.Keys); Program.Check(applied.Success, applied.Message);
                foreach (var (id, original) in originals)
                {
                    using var process = system.Open(id);
                    Program.Check(process.Read().Normalized == original.ForPlan(plan).Normalized, $"Native {plan} policy differs");
                }
            }
            var first = originals.Keys.First(); Program.Check(manager.Restore(first).Success);
            using (var process = system.Open(first)) Program.Check(process.Read().Normalized == originals[first]);
            Program.Check(manager.Reset().Success);
            foreach (var (id, original) in originals)
            {
                using var process = system.Open(id); Program.Check(process.Read().Normalized == original);
            }
            bool refused = false;
            try { using var unrelated = system.Open(new(Environment.ProcessId, Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks)); }
            catch (InvalidOperationException) { refused = true; }
            Program.Check(refused, "Unrelated process accepted for resource changes");
            refused = false;
            try { using var stale = system.Open(first with { StartTimeUtcTicks = first.StartTimeUtcTicks + 1 }); }
            catch (ProcessGoneException) { refused = true; }
            Program.Check(refused, "Mismatched process creation time accepted");
            Console.WriteLine("Native resource plans: Low/Balanced/High readback, individual restore, full reset and identity refusal passed on owned fixture clients.");
        }
        finally { manager.Reset(); }
    }

    private static void VerifyResidentCaps(IReadOnlyList<ClientWindow> clients)
    {
        var system = new Win32ProcessResources(); var manager = new WorkingSetCapManager(system, new TestLog());
        var originals = new Dictionary<ProcessIdentity, WorkingSetLimits>();
        foreach (var client in clients)
        {
            var id = ProcessIdentity.From(client.Identity); using var handle = system.OpenWorkingSet(id); originals[id] = handle.ReadLimits().Normalized;
        }
        try
        {
            // Exercise actual paging at a smaller limit on one disposable fixture, not a real Roblox client.
            var first = originals.Keys.First(); using var process = Process.GetProcessById(first.ProcessId);
            process.Refresh(); long before = process.WorkingSet64;
            Program.Check(before > 34L * 1048576, "Fixture data already paged out; resident-reduction measurement is inconclusive");
            using (var handle = system.OpenWorkingSet(first))
            {
                try
                {
                    handle.WriteLimits(new(81920, 32UL * 1048576, 4));
                    UiSmoke.PumpUntil(() => { process.Refresh(); return process.WorkingSet64 <= 34L * 1048576; }, TimeSpan.FromSeconds(8));
                    Console.WriteLine($"Fixture resident working set: {before / 1048576d:F1} -> {process.WorkingSet64 / 1048576d:F1} MiB with a temporary 32 MiB test limit; process still running.");
                }
                finally { handle.WriteLimits(originals[first]); }
            }
            var applied = manager.Apply(500, originals.Keys); Program.Check(applied.Success, applied.Message);
            foreach (var id in originals.Keys)
            {
                using var handle = system.OpenWorkingSet(id); var limits = handle.ReadLimits();
                Program.Check((limits.Flags & 4) != 0 && limits.MaximumBytes == 500UL * 1048576, "500 MiB limit not enabled");
            }
            Program.Check(manager.Reset().Success);
            foreach (var (id, original) in originals)
            {
                using var handle = system.OpenWorkingSet(id); Program.Check(handle.ReadLimits().Normalized == original, "Original working-set limits differ after restoration");
            }
            Console.WriteLine("Native resident caps: 500 MiB readback and exact original enforcement restoration passed.");
        }
        finally { manager.Reset(); }
    }

    private static void VerifyResourcePanelAndClose(Win32WindowSystem windows, IReadOnlyList<ClientWindow> clients)
    {
        if (!Application.Current.Resources.Contains("CanvasBrush")) Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/RobloxAfkManager;component/Themes/Dark.xaml", UriKind.Relative) });
        var fixturesOnly = new FakeWindowSystem(0);
        var system = new Win32ProcessResources(); var originals = new Dictionary<ProcessIdentity, ResourceState>();
        foreach (var client in clients)
        {
            fixturesOnly.Bounds[client.Identity] = windows.ReadBounds(client.Identity);
            fixturesOnly.Placements[client.Identity] = windows.Capture(client.Identity);
            using var process = system.Open(ProcessIdentity.From(client.Identity)); originals[ProcessIdentity.From(client.Identity)] = process.Read().Normalized;
        }
        using var vm = new MainViewModel(fixturesOnly, new TestLog(), system, system);
        var host = new MainWindow(vm) { ShowActivated = false, WindowState = WindowState.Normal };
        try
        {
            host.Show(); UiSmoke.PumpUntil(() => !vm.Busy && vm.ClientCount == clients.Count, TimeSpan.FromSeconds(5));
            ((Button)host.FindName("ResourceButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); UiSmoke.Pump();
            var popup = (System.Windows.Controls.Primitives.Popup)host.FindName("ResourcePopup");
            Program.Check(popup.IsOpen, "Resource panel did not open");
            var selector = Descendants(popup.Child).OfType<ComboBox>().Single();
            Program.Check(selector.Items.Count == 3, "Resource choices not bound");
            selector.SelectedItem = ResourcePlan.Low; UiSmoke.Pump();
            Program.Check(vm.SelectedResourcePlan == ResourcePlan.Low);
            var apply = Descendants(popup.Child).OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.ApplyResourcePlanCommand));
            Program.Check(apply.Command!.CanExecute(null)); apply.Command.Execute(null);
            UiSmoke.PumpUntil(() => !vm.Busy, TimeSpan.FromSeconds(5)); Program.Check(!vm.Warning, vm.Message);
            var memoryOriginals = new Dictionary<ProcessIdentity, WorkingSetLimits>();
            foreach (var id in originals.Keys) { using var process = system.OpenWorkingSet(id); memoryOriginals[id] = process.ReadLimits().Normalized; }
            var applyCap = Descendants(popup.Child).OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.ApplyMemoryCapCommand));
            applyCap.Command!.Execute(null); UiSmoke.PumpUntil(() => !vm.Busy, TimeSpan.FromSeconds(5)); Program.Check(!vm.Warning, vm.Message);
            var panel = (FrameworkElement)popup.Child; panel.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth), (int)Math.Ceiling(panel.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(panel);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            Directory.CreateDirectory("artifacts"); using (var stream = File.Create("artifacts/resource-plans.png")) encoder.Save(stream);
            popup.IsOpen = false;
            var first = vm.Clients[0]; first.IsIgnored = true; UiSmoke.PumpUntil(() => !vm.Busy, TimeSpan.FromSeconds(5));
            using (var process = system.Open(ProcessIdentity.From(first.Identity))) Program.Check(process.Read().Normalized == originals[ProcessIdentity.From(first.Identity)]);
            using (var process = system.OpenWorkingSet(ProcessIdentity.From(first.Identity))) Program.Check(process.ReadLimits().Normalized == memoryOriginals[ProcessIdentity.From(first.Identity)]);
            Program.Check(vm.VisibleClients.Count == clients.Count - 1);
            host.Close(); UiSmoke.PumpUntil(() => !host.IsVisible, TimeSpan.FromSeconds(5));
            foreach (var (id, original) in originals)
            {
                using var process = system.Open(id); Program.Check(process.Read().Normalized == original, "Normal close failed to restore resource settings");
                using var memory = system.OpenWorkingSet(id); Program.Check(memory.ReadLimits().Normalized == memoryOriginals[id], "Normal close failed to restore resident limits");
            }
            Console.WriteLine("Resource panel: Low and 500 MiB apply; ignore and normal close restore original policies and resident limits.");
        }
        finally { vm.Dispose(); host.Close(); UiSmoke.Pump(); }
    }

    private static void VerifyFilledSurface(LiveWindowPreview preview)
    {
        var dpi = VisualTreeHelper.GetDpi(preview);
        var rect = preview.DisplayedBounds!.Value;
        Program.Check(preview.FillPreview && Math.Abs(rect.Width - preview.ActualWidth * dpi.DpiScaleX) <= 2 && Math.Abs(rect.Height - preview.ActualHeight * dpi.DpiScaleY) <= 2,
            "Native Fill preview left unused width or height");
    }

    private static void VerifyLivePreviews(Win32WindowSystem windows, IReadOnlyList<ClientWindow> clients)
    {
        var panel = new Grid(); panel.ColumnDefinitions.Add(new()); panel.ColumnDefinitions.Add(new());
        var previews = clients.Select(client => new LiveWindowPreview
        {
            Identity = client.Identity, SourceBounds = windows.ReadBounds(client.Identity), IdentityValidator = windows.IsCurrent, Margin = new Thickness(12)
        }).ToArray();
        for (int i = 0; i < previews.Length; i++) { Grid.SetColumn(previews[i], i); panel.Children.Add(previews[i]); }
        var host = new Window { Title = "Synthetic live-preview test", Content = panel, Width = 800, Height = 500, ShowActivated = false, Background = Brushes.Black };
        try
        {
            host.Show();
            UiSmoke.PumpUntil(() => previews.All(p => p.HasLiveThumbnail && p.DisplayedBounds is { Width: > 0, Height: > 0 }), TimeSpan.FromSeconds(5));
            var first = previews[0].DisplayedBounds!.Value; var second = previews[1].DisplayedBounds!.Value;
            Program.Check(!first.Intersects(second), "Native previews overlap");
            foreach (var preview in previews) VerifyFilledSurface(preview);
            foreach (var preview in previews) preview.FillPreview = false;
            UiSmoke.Pump();
            Program.Check(previews.All(p => p.DisplayedSourceBounds is { X: 0, Y: 0 }), "Fit did not restore source edges");
            foreach (var preview in previews) preview.FillPreview = true;
            UiSmoke.Pump();
            host.Width = 620; host.Height = 350;
            UiSmoke.PumpUntil(() => previews[0].DisplayedBounds is { } changed && changed != first, TimeSpan.FromSeconds(5));
            host.WindowState = WindowState.Minimized;
            UiSmoke.PumpUntil(() => previews.All(p => p.DisplayedBounds is null), TimeSpan.FromSeconds(5));
            host.WindowState = WindowState.Normal;
            UiSmoke.PumpUntil(() => previews.All(p => p.DisplayedBounds is not null), TimeSpan.FromSeconds(5));
            host.Close(); UiSmoke.Pump();
            Program.Check(previews.All(p => !p.HasLiveThumbnail), "Native preview handles leaked after closing the host");
            Program.Check(clients.All(c => windows.IsNormal(c.Identity)), "Preview lifecycle changed a source window's normal state");
            Console.WriteLine("DWM previews: registered, positioned, resized, hidden/restored with manager, and unregistered; source clients stayed normal.");
        }
        finally { host.Close(); }
    }
}

