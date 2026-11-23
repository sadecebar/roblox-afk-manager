using RobloxAfkManager.Core;
using RobloxAfkManager.App.ViewModels;

namespace RobloxAfkManager.Tests;

internal static class Program
{
    private static int passed, failed;
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--remote-demo")) { RemoteBrowserFixture.RunAsync().GetAwaiter().GetResult(); return 0; }
        Test("Preset layouts use the full working area without overlap", () =>
        {
            foreach (int rows in new[] { 2, 3, 4 })
            {
                var area = new PixelRect(-1920, 40, 1920, 1040);
                var cells = GridPlanner.Plan(area, new(4, rows), Enumerable.Repeat(new WindowLimits(200, 150), 4 * rows).ToArray());
                Check(cells.All(area.Contains)); Check(cells[^1].Right == area.Right && cells[^1].Bottom == area.Bottom);
                for (int i = 0; i < cells.Count; i++) for (int j = i + 1; j < cells.Count; j++) Check(!cells[i].Intersects(cells[j]));
            }
        });
        Test("Odd dimensions distribute remainders and preserve gaps", () =>
        {
            var cells = GridPlanner.Plan(new(15, -1080, 1919, 1039), new(4, 3, 7), Enumerable.Repeat(new WindowLimits(1, 1), 12).ToArray());
            Check(cells[1].X - cells[0].Right == 7); Check(cells[4].Y - cells[0].Bottom == 7); Check(cells[^1].Right == 1934); Check(cells[^1].Bottom == -41);
        });
        Test("Custom layout supports more than sixteen clients", () => Check(GridPlanner.Plan(new(0, 0, 3840, 2160), new(5, 5), Enumerable.Repeat(new WindowLimits(200, 150), 25).ToArray()).Count == 25));
        Test("Reject zero, excessive and malformed grid dimensions", () =>
        {
            foreach (var spec in new[] { new LayoutSpec(0, 2), new(33, 2), new(2, -1), new(2, 2, -1) })
                Throws(() => GridPlanner.Plan(new(0, 0, 1920, 1040), spec, [new(1, 1)]));
            Throws(() => GridPlanner.Plan(new(0, 0, 1920, 1040), new(2, 1), []));
        });
        Test("Insufficient capacity rejects every move", () =>
        {
            var f = new FakeWindowSystem(3); var m = Manager(f);
            Check(!m.Arrange(f.Detect(), f.Screen, new(2, 1)).Success); Check(f.Moved.Count == 0); Check(m.OriginalCount == 0);
        });
        Test("Minimum outer size rejects before capture and mutation", () =>
        {
            var f = new FakeWindowSystem(8) { Limits = new(600, 400) }; var m = Manager(f);
            Check(!m.Arrange(f.Detect(), f.Screen, new(4, 2)).Success); Check(f.Moved.Count == 0); Check(m.OriginalCount == 0);
        });
        Test("Maximum size constraint is enforced", () => Throws(() => GridPlanner.Plan(new(0, 0, 1920, 1040), new(2, 1), [new(200, 150, 900, 900)])));
        Test("Capture failure causes no partial movement", () =>
        {
            var f = new FakeWindowSystem(); f.FailCapture = f.Placements.Keys.Last(); var m = Manager(f);
            Check(!m.Arrange(f.Detect(), f.Screen, new(2, 1)).Success); Check(f.Moved.Count == 0); Check(m.OriginalCount == 0);
        });
        Test("First originals survive multiple successful arrangements", () =>
        {
            var f = new FakeWindowSystem(); var before = f.Bounds.ToDictionary(); var m = Manager(f);
            Check(m.Arrange(f.Detect(), f.Screen, new(2, 1)).Success); Check(m.Arrange(f.Detect(), f.Screen, new(1, 2)).Success);
            Check(m.RestoreOriginals().Success); Check(before.All(p => f.Bounds[p.Key] == p.Value)); Check(m.OriginalCount == 0); Check(m.LastGrid is null);
        });
        Test("Partial move failure rolls back every attempted window", () =>
        {
            var f = new FakeWindowSystem(); var before = f.Bounds.ToDictionary(); f.FailMove = f.Placements.Keys.Last(); var m = Manager(f);
            Check(!m.Arrange(f.Detect(), f.Screen, new(2, 1)).Success); Check(f.Restored.Count == 2); Check(before.All(p => f.Bounds[p.Key] == p.Value)); Check(m.OriginalCount == 2);
        });
        Test("A silently rejected size is verified and rolled back", () =>
        {
            var f = new FakeWindowSystem(); f.RejectSize = f.Placements.Keys.First(); var before = f.Bounds.ToDictionary(); var m = Manager(f);
            Check(!m.Arrange(f.Detect(), f.Screen, new(2, 1)).Success); Check(before.All(p => f.Bounds[p.Key] == p.Value));
        });
        Test("Failed rollback is reported and originals remain recoverable", () =>
        {
            var f = new FakeWindowSystem(); f.FailMove = f.Placements.Keys.Last(); f.FailRestore = f.Placements.Keys.First(); var m = Manager(f);
            var result = m.Arrange(f.Detect(), f.Screen, new(2, 1)); Check(!result.Success && result.Message.Contains("could not be rolled back")); Check(m.OriginalCount == 2);
        });
        Test("Restore Grid preserves slots when one client closes", () =>
        {
            var f = new FakeWindowSystem(); var m = Manager(f); Check(m.Arrange(f.Detect(), f.Screen, new(2, 1)).Success);
            var grid = m.LastGrid!; f.Closed.Add(grid.Targets[0].Identity); f.Bounds[grid.Targets[1].Identity] = new(1, 1, 800, 600);
            Check(m.RestoreGrid().Success); Check(f.Bounds[grid.Targets[1].Identity] == grid.Targets[1].Bounds);
        });
        Test("Display working area or DPI changes invalidate saved grid", () =>
        {
            var f = new FakeWindowSystem(); var m = Manager(f); Check(m.Arrange(f.Detect(), f.Screen, new(2, 1)).Success);
            int moves = f.Moved.Count; f.Screen = f.Screen with { Dpi = 144 }; Check(!m.RestoreGrid().Success); Check(f.Moved.Count == moves);
            f.Screen = f.Screen with { Dpi = 96, WorkArea = new(0, 0, 1280, 720) }; Check(!m.RestoreGrid().Success);
        });
        Test("Disconnected monitor rejects a new arrangement", () =>
        {
            var f = new FakeWindowSystem { MonitorPresent = false }; Check(!Manager(f).Arrange(f.Detect(), f.Screen, new(2, 1)).Success); Check(f.Moved.Count == 0);
        });
        Test("Closed/restarted process identity cannot receive originals", () =>
        {
            var f = new FakeWindowSystem(); var m = Manager(f); Check(m.Arrange(f.Detect(), f.Screen, new(2, 1)).Success);
            var old = f.Placements.Keys.First(); f.Closed.Add(old); var replacement = old with { StartTimeUtcTicks = old.StartTimeUtcTicks + 1 };
            f.Placements[replacement] = f.Placements[old]; f.Bounds[replacement] = f.Bounds[old];
            Check(m.RestoreOriginals().Success); Check(!f.Restored.Contains(old) && !f.Restored.Contains(replacement));
        });
        Test("Reused HWND in the same process has a separate lifetime", () =>
        {
            var f = new FakeWindowSystem(); var m = Manager(f); Check(m.Arrange(f.Detect(), f.Screen, new(2, 1)).Success);
            var old = f.Placements.Keys.First(); f.Closed.Add(old); var replacement = old with { Generation = old.Generation + 1 };
            f.Placements[replacement] = f.Placements[old]; f.Bounds[replacement] = f.Bounds[old];
            Check(m.RestoreOriginals().Success); Check(!f.Restored.Contains(replacement));
        });
        Test("Originally minimized placements restore normally", () =>
        {
            var f = new FakeWindowSystem(); var id = f.Placements.Keys.First(); f.Placements[id] = f.Placements[id] with { ShowCommand = 2 };
            var m = Manager(f); Check(m.Arrange(f.Detect(), f.Screen, new(2, 1)).Success); Check(m.RestoreOriginals().Success); Check(f.IsNormal(id));
        });
        Test("Maximized originals preserve maximized show state", () =>
        {
            var f = new FakeWindowSystem(); var id = f.Placements.Keys.First(); f.Placements[id] = f.Placements[id] with { ShowCommand = 3 };
            var m = Manager(f); Check(m.Arrange(f.Detect(), f.Screen, new(2, 1)).Success); Check(m.RestoreOriginals().Success); Check(f.Placements[id].ShowCommand == 3);
        });
        Test("Focus restrictions are surfaced and original is saved", () =>
        {
            var f = new FakeWindowSystem { FocusAllowed = false }; var m = Manager(f); Check(!m.Focus(f.Placements.Keys.First()).Success); Check(m.OriginalCount == 1); Check(f.Moved.Count == 0);
        });
        Test("Failed original restores are retained for retry", () =>
        {
            var f = new FakeWindowSystem(); var m = Manager(f); Check(m.Arrange(f.Detect(), f.Screen, new(2, 1)).Success); f.FailRestore = f.Placements.Keys.First();
            Check(!m.RestoreOriginals().Success); Check(m.OriginalCount == 1); f.FailRestore = null; Check(m.RestoreOriginals().Success); Check(m.OriginalCount == 0);
        });
        Test("Pruning retains surviving slots and clears an empty saved grid", () =>
        {
            var f = new FakeWindowSystem(); var m = Manager(f); Check(m.Arrange(f.Detect(), f.Screen, new(2, 1)).Success);
            var targets = m.LastGrid!.Targets.ToArray(); f.Closed.Add(targets[0].Identity); m.PruneClosed();
            Check(m.OriginalCount == 1 && m.LastGrid!.Targets.Count == 1 && m.LastGrid.Targets[0] == targets[1]);
            f.Closed.Add(targets[1].Identity); m.PruneClosed(); Check(m.OriginalCount == 0 && m.LastGrid is null);
        });
        Test("Nine clients with one ignored arrange only the remaining eight", () =>
        {
            var f = new FakeWindowSystem(9); var m = Manager(f); var active = f.Placements.Keys.First(); var before = f.Bounds[active];
            m.SetIgnored(active, true); Check(m.Arrange(f.Detect(), f.Screen, new(4, 2)).Success);
            Check(f.Moved.Count == 8 && !f.Moved.Contains(active) && f.Bounds[active] == before && m.OriginalCount == 8);
        });
        Test("Ignoring a previously arranged client protects it from both restores", () =>
        {
            var f = new FakeWindowSystem(); var m = Manager(f); Check(m.Arrange(f.Detect(), f.Screen, new(2, 1)).Success);
            var active = f.Placements.Keys.First(); m.SetIgnored(active, true); f.Bounds[active] = new(50, 70, 1100, 800);
            var before = f.Bounds[active]; f.Moved.Clear();
            Check(m.RestoreGrid().Success && !f.Moved.Contains(active));
            Check(m.RestoreOriginals().Success && !f.Restored.Contains(active) && f.Bounds[active] == before);
            m.SetIgnored(active, false); Check(m.OriginalCount == 1); Check(m.RestoreOriginals().Success && f.Restored.Contains(active));
        });
        Test("Ignoring every client prevents all bulk changes", () =>
        {
            var f = new FakeWindowSystem(); var m = Manager(f); foreach (var id in f.Placements.Keys) m.SetIgnored(id, true);
            Check(!m.Arrange(f.Detect(), f.Screen, new(2, 1)).Success); Check(!m.PrepareDashboard(f.Detect()).Success);
            Check(f.Moved.Count == 0 && f.Prepared.Count == 0 && m.OriginalCount == 0);
        });
        Test("Ignore follows the process window lifetime but not a reused process ID", () =>
        {
            var f = new FakeWindowSystem(); var m = Manager(f); var id = f.Placements.Keys.First(); m.SetIgnored(id, true);
            Check(m.IsIgnored(id with { Handle = 12345, Generation = 7 }));
            Check(!m.IsIgnored(id with { StartTimeUtcTicks = id.StartTimeUtcTicks + 1 }));
        });
        Test("Dashboard restores minimized included clients without grid sizing or focus", () =>
        {
            var f = new FakeWindowSystem(9) { Limits = new(816, 638) }; var m = Manager(f); var active = f.Placements.Keys.First();
            foreach (var id in f.Placements.Keys.ToArray()) f.Placements[id] = f.Placements[id] with { ShowCommand = 2 };
            m.SetIgnored(active, true); var before = f.Bounds.ToDictionary();
            Check(m.PrepareDashboard(f.Detect()).Success); Check(f.Prepared.Count == 8 && !f.Prepared.Contains(active));
            Check(f.Placements[active].ShowCommand == 2); Check(f.Prepared.All(f.IsNormal));
            Check(f.Moved.Count == 0 && f.Focused.Count == 0 && before.All(p => f.Bounds[p.Key] == p.Value));
        });
        Test("Ignore survives refresh and the dashboard counts only included clients", () =>
        {
            var f = new FakeWindowSystem(9); using var vm = new MainViewModel(f, new TestLog());
            vm.RefreshAsync().GetAwaiter().GetResult(); var active = vm.Clients[0]; active.IsIgnored = true;
            vm.RefreshAsync().GetAwaiter().GetResult(); Check(vm.Clients[0].IsIgnored && vm.IncludedCount == 8 && vm.IgnoredCount == 1);
            Check(vm.VisibleClients.Count == 8 && vm.IgnoredClients.Single() == active && f.Focused.Count == 0);
            Check(!vm.VisibleClients.Contains(active) && vm.UnignoreClientCommand.CanExecute(active));
            foreach (var client in vm.Clients) client.IsIgnored = true;
            Check(!vm.PrepareDashboardCommand.CanExecute(null) && !vm.ArrangeCommand.CanExecute(null));
            Check(vm.IsEmpty && vm.VisibleClients.Count == 0 && vm.IgnoredClients.Count == 9 && vm.EmptyHeading.Contains("ignored"));
        });
        Test("Showing an ignored client restores its tile without focusing or moving the game", () =>
        {
            var f = new FakeWindowSystem(9); using var vm = new MainViewModel(f, new TestLog()); vm.RefreshAsync().GetAwaiter().GetResult();
            var client = vm.Clients[4]; vm.SelectedClient = client; client.IsIgnored = true;
            Check(vm.SelectedClient is null && !vm.FocusClientCommand.CanExecute(client));
            vm.FocusClientCommand.Execute(client); Check(f.Focused.Count == 0);
            vm.UnignoreClientCommand.Execute(client);
            Check(vm.VisibleClients.SequenceEqual(vm.Clients) && vm.IgnoredClients.Count == 0 && !client.IsIgnored);
            Check(f.Focused.Count == 0 && vm.SelectedClient is null);
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.Busy) && !vm.Busy) finished.TrySetResult(); };
            vm.FocusClientCommand.Execute(client);
            finished.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Check(f.Focused.SequenceEqual([client.Identity]) && vm.SelectedClient?.Identity == client.Identity && !client.IsIgnored);
            Check(f.Moved.Count == 0 && f.Prepared.Count == 0);
        });
        Test("Hidden clients follow a recreated window and drop out when their process closes", () =>
        {
            var f = new FakeWindowSystem(3); using var vm = new MainViewModel(f, new TestLog()); vm.RefreshAsync().GetAwaiter().GetResult();
            var old = vm.Clients[1].Identity; vm.Clients[1].IsIgnored = true;
            var replacement = old with { Handle = 999, Generation = 2 };
            f.Closed.Add(old); f.Bounds[replacement] = f.Bounds[old]; f.Placements[replacement] = f.Placements[old];
            vm.RefreshAsync().GetAwaiter().GetResult();
            Check(vm.IgnoredClients.Single().Identity == replacement && vm.VisibleClients.Count == 2);
            f.Closed.Add(replacement); var restarted = old with { StartTimeUtcTicks = old.StartTimeUtcTicks + 1 };
            f.Bounds[restarted] = f.Bounds[old]; f.Placements[restarted] = f.Placements[old];
            vm.RefreshAsync().GetAwaiter().GetResult();
            Check(vm.IgnoredClients.Count == 0 && vm.VisibleClients.Count == 3 && vm.VisibleClients.Any(c => c.Identity == restarted));
        });
        Test("Compact dashboard fits 1–64 tiles inside the viewport without overlap", () =>
        {
            foreach (var (width, height) in new[] { (624d, 260d), (904d, 500d), (1264d, 620d), (1904d, 940d) })
            foreach (double aspect in new[] { 0.75, 1.5, 16d / 9 })
            for (int count = 1; count <= 64; count++)
            {
                var layout = DashboardLayout.Fit(count, width, height, aspect);
                Check(layout.TileWidth > 0 && layout.TileHeight > 0);
                Check(layout.Columns * layout.Rows >= count);
                Check(layout.Columns * layout.TileWidth + (layout.Columns - 1) * layout.Gap <= width + 0.01);
                Check(layout.Rows * layout.TileHeight + (layout.Rows - 1) * layout.Gap <= height + 0.01);
                Check(Math.Abs(layout.Columns * layout.TileWidth + (layout.Columns - 1) * layout.Gap - width) < 0.01);
                Check(Math.Abs(layout.Rows * layout.TileHeight + (layout.Rows - 1) * layout.Gap - height) < 0.01);
                for (int row = 0; row < layout.Rows; row++)
                {
                    int first = row * layout.Columns, last = Math.Min(count, first + layout.Columns) - 1;
                    var left = layout.Cell(first, count, width); var right = layout.Cell(last, count, width);
                    Check(Math.Abs(left.X) < 0.01 && Math.Abs(right.X + right.Width - width) < 0.01, "Every row must fill the workspace width");
                    for (int i = first; i < last; i++)
                    {
                        var a = layout.Cell(i, count, width); var b = layout.Cell(i + 1, count, width);
                        Check(Math.Abs(b.X - (a.X + a.Width) - layout.Gap) < 0.01, "Adjacent cells must retain their gap");
                    }
                }
            }
        });
        Test("Twelve cards fill wide and laptop workspaces without an empty strip", () =>
        {
            foreach (var (width, height) in new[] { (1896d, 860d), (1256d, 580d) })
            {
                var layout = DashboardLayout.Fit(12, width, height, 974d / 638);
                Check(layout.Columns * layout.Rows == 12, $"Unexpected twelve-client grid: {layout.Columns}x{layout.Rows}");
                var last = layout.Cell(11, 12, width);
                Check(Math.Abs(last.X + last.Width - width) < 0.01 && Math.Abs(last.Y + last.Height - height) < 0.01);
            }
            Check(DashboardLayout.Fit(0, 100, 100, 1.5).TileWidth == 0);
            Check(DashboardLayout.Fit(12, double.NaN, 100, 1.5).TileWidth == 0);
        });
        Test("Fill previews occupy their entire surface and crop rather than stretch", () =>
        {
            foreach (var (sw, sh) in new[] { (974, 638), (1920, 480), (480, 1920), (101, 73) })
            foreach (var viewport in new[] { new PixelRect(15, 30, 460, 235), new(-400, 50, 137, 221), new(0, 0, 501, 311) })
            {
                var fill = PreviewGeometry.Map(viewport, sw, sh, true, viewport);
                Check(fill.Destination == viewport, "Fill left a bar or escaped the preview surface");
                Check(new PixelRect(0, 0, sw, sh).Contains(fill.Source));
                // Integer source/destination rounding can differ by at most a few source pixels.
                Check(Math.Abs(fill.Source.Width - fill.Source.Height * (double)viewport.Width / viewport.Height) < 5);
                Check(Math.Abs(fill.Source.X * 2 + fill.Source.Width - sw) <= Math.Ceiling((double)sw / viewport.Width) + 2);
                Check(Math.Abs(fill.Source.Y * 2 + fill.Source.Height - sh) <= Math.Ceiling((double)sh / viewport.Height) + 2);
            }
        });
        Test("Fit preserves the whole source and clipping never covers adjacent controls", () =>
        {
            var viewport = new PixelRect(20, 40, 460, 235);
            var fit = PreviewGeometry.Map(viewport, 974, 638, false, viewport);
            Check(fit.Source == new PixelRect(0, 0, 974, 638));
            Check(viewport.Contains(fit.Destination) && fit.Destination.Width < viewport.Width);
            var visible = new PixelRect(40, 60, 220, 150);
            foreach (bool fill in new[] { false, true })
            {
                var clipped = PreviewGeometry.Map(viewport, 974, 638, fill, visible);
                Check(clipped.IsVisible && viewport.Contains(clipped.Destination) && visible.Contains(clipped.Destination));
                Check(new PixelRect(0, 0, 974, 638).Contains(clipped.Source));
            }
            Check(!PreviewGeometry.Map(viewport, 0, 100, true, viewport).IsVisible);
            Check(!PreviewGeometry.Map(viewport, 100, 100, true, new(5000, 0, 100, 100)).IsVisible);
        });
        DashboardInteractionTests.Register(Test);
        AccountNameTests.Register(Test);
        SessionWorkspaceTests.Register(Test);
        ResourceProfileTests.Register(Test);
        WorkingSetCapTests.Register(Test);
        MonitoringTests.Register(Test);
        if (args.Contains("--remote-http")) Test("Private HTTP dashboard authentication, image delivery, read-only routes and restart", MonitoringTests.Http);
        if (args.Contains("--ui")) Test("WPF resources, bindings and dashboard rendering", UiSmoke.Run);
        if (args.Contains("--drag")) Test("Mouse drag-to-swap and preview clicks on synthetic clients", DashboardDragSmoke.Run);
        if (args.Contains("--name-native")) Test("Native log ownership and account matching on an owned fixture", AccountNameTests.NativeOwnership);
        if (args.Contains("--name-network")) Test("Public Roblox ID-to-username endpoint", AccountNameTests.PublicLookup);
        if (args.Contains("--name-network")) Test("Public avatar/game metadata and bounded avatar decoding", SessionWorkspaceTests.PublicArtwork);
        if (args.Contains("--account-details-live")) Test("Read-only account and session metadata for running Roblox clients", SessionWorkspaceTests.ReadOnlyLiveDetails);
        if (args.Contains("--native")) Test("Win32 detection, sizing, arrangement and restoration on two synthetic clients", NativeSmoke.Run);
        if (args.Contains("--package")) Test("Packaged application startup and idle observation", NativeSmoke.PackageStartup);
        if (args.Contains("--package-long")) Test("Packaged application over three metadata intervals", NativeSmoke.PackageLongObservation);
        if (args.Contains("--real-previews")) Test("Read-only preview registration for current Roblox clients", NativeSmoke.RealPreviews);
        Console.WriteLine($"\n{passed} passed; {failed} failed.");
        return failed == 0 ? 0 : 1;
    }
    private static WindowManager Manager(FakeWindowSystem f) => new(f, new TestLog());
    internal static void Check(bool value, string message = "Assertion failed") { if (!value) throw new InvalidOperationException(message); }
    private static void Throws(Action action) { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Expected rejection"); }
    private static void Test(string name, Action test)
    {
        try { test(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + "\n" + ex); }
    }
}
