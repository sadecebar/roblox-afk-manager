namespace RobloxAfkManager.Core;

// Call operations serially. The UI runs them on a worker so an unresponsive client
// cannot block WPF. Originals survive failed arrangements until an explicit restore.
public sealed class WindowManager(IWindowSystem windows, IAppLog log)
{
    private readonly Dictionary<WindowIdentity, Placement> originals = [];
    private readonly HashSet<(int ProcessId, long Start)> ignored = [];
    public GridPlan? LastGrid { get; private set; }
    public int OriginalCount => originals.Keys.Count(id => !IsIgnored(id));
    public bool CanRestoreGrid => LastGrid?.Targets.Any(t => !IsIgnored(t.Identity)) == true;
    public bool IsIgnored(WindowIdentity id) => ignored.Contains((id.ProcessId, id.StartTimeUtcTicks));
    public void SetIgnored(WindowIdentity id, bool value)
    {
        if (value) ignored.Add((id.ProcessId, id.StartTimeUtcTicks));
        else ignored.Remove((id.ProcessId, id.StartTimeUtcTicks));
    }

    public OperationResult PrepareDashboard(IReadOnlyList<ClientWindow> clients)
    {
        int ready = 0, failed = 0;
        var included = clients.Where(c => !IsIgnored(c.Identity)).ToArray();
        if (included.Length == 0) return new(false, "All clients are ignored. Uncheck Ignore this client on at least one tile.");
        foreach (var client in included)
        {
            try
            {
                var placement = windows.Capture(client.Identity);
                originals.TryAdd(client.Identity, placement);
                windows.RestoreForPreview(client.Identity);
                ready++;
            }
            catch (Exception ex) { failed++; log.Write($"Prepare preview failed for PID {client.Identity.ProcessId}.", ex); }
        }
        return new(failed == 0, $"{ready} clients ready for previews; {clients.Count - included.Length} ignored; {failed} unavailable. Click a preview to focus a client. No grid resizing is required.");
    }

    public void PruneClosed()
    {
        foreach (var id in originals.Keys.Where(id => !windows.IsCurrent(id)).ToArray()) originals.Remove(id);
        if (LastGrid is { } grid)
        {
            var surviving = grid.Targets.Where(t => windows.IsCurrent(t.Identity)).ToArray();
            LastGrid = surviving.Length == 0 ? null : grid with { Targets = surviving };
        }
    }

    public OperationResult Arrange(IReadOnlyList<ClientWindow> clients, MonitorInfo monitor, LayoutSpec layout)
    {
        try
        {
            clients = clients.Where(c => !IsIgnored(c.Identity)).ToArray();
            GridPlanner.Validate(layout, clients.Count);
            // Refresh monitor geometry immediately before planning (taskbar/DPI/hotplug).
            monitor = windows.GetMonitors().SingleOrDefault(m => m.DeviceName == monitor.DeviceName)
                ?? throw new InvalidOperationException("The selected monitor is no longer connected. Select a monitor again.");
            var limits = clients.Select(c => windows.GetLimits(c.Identity, monitor.Dpi)).ToArray();
            var cells = GridPlanner.Plan(monitor.WorkArea, layout, limits);
            var targets = clients.Select((c, i) => new WindowTarget(c.Identity, cells[i])).ToArray();
            var plan = new GridPlan(monitor.DeviceName, monitor.WorkArea, monitor.Dpi, layout, targets);
            var result = Apply(plan);
            if (result.Success) LastGrid = plan;
            return result;
        }
        catch (Exception ex) { return Failure("Arrange grid", ex); }
    }

    public OperationResult RestoreGrid()
    {
        try
        {
            var grid = LastGrid ?? throw new InvalidOperationException("Arrange a grid first.");
            var monitor = windows.GetMonitors().SingleOrDefault(m => m.DeviceName == grid.MonitorDevice);
            if (monitor is null || monitor.WorkArea != grid.WorkArea || monitor.Dpi != grid.Dpi)
                throw new InvalidOperationException("The grid monitor or its working area/scaling has changed. Arrange a new grid.");
            var targets = grid.Targets.Where(t => !IsIgnored(t.Identity) && windows.IsCurrent(t.Identity)).ToArray();
            if (targets.Length == 0) throw new InvalidOperationException("No included saved grid windows remain. Unignore a client or arrange a new grid.");
            foreach (var target in targets)
            {
                var limit = windows.GetLimits(target.Identity, monitor.Dpi);
                if (target.Bounds.Width < limit.MinWidth || target.Bounds.Height < limit.MinHeight ||
                    target.Bounds.Width > limit.MaxWidth || target.Bounds.Height > limit.MaxHeight)
                    throw new InvalidOperationException("A client's size constraints changed. Arrange a new grid.");
            }
            var result = Apply(grid with { Targets = targets });
            return result.Success ? new(true, $"Restored {targets.Length} saved grid positions. New clients need Arrange Grid.") : result;
        }
        catch (Exception ex) { return Failure("Restore grid", ex); }
    }

    private OperationResult Apply(GridPlan plan)
    {
        // Capture every window before the first mutation. If any read fails, nothing moves.
        var before = plan.Targets.ToDictionary(t => t.Identity, t => windows.Capture(t.Identity));
        foreach (var item in before) originals.TryAdd(item.Key, item.Value);
        var attempted = new List<WindowIdentity>();
        try
        {
            foreach (var target in plan.Targets)
            {
                attempted.Add(target.Identity); // Include partial failures in rollback.
                windows.MoveNormal(target.Identity, target.Bounds);
            }
            var actual = plan.Targets.Select(t => windows.ReadBounds(t.Identity)).ToArray();
            for (int i = 0; i < actual.Length; i++)
            {
                if (!windows.IsNormal(plan.Targets[i].Identity) || actual[i] != plan.Targets[i].Bounds || !plan.WorkArea.Contains(actual[i]))
                    throw new InvalidOperationException($"Client {plan.Targets[i].Identity.ProcessId} did not accept its grid cell (minimum size, display scaling, or a busy window).");
                for (int j = 0; j < i; j++)
                    if (actual[i].Intersects(actual[j])) throw new InvalidOperationException("Window bounds overlap after resizing.");
            }
            log.Write($"Arranged {actual.Length} clients on {plan.MonitorDevice}, {plan.Layout}.");
            return new(true, $"Arranged {actual.Length} clients in a {plan.Layout} grid. All verified non-minimized.");
        }
        catch (Exception ex)
        {
            int failed = 0;
            foreach (var id in attempted.AsEnumerable().Reverse())
            {
                if (!windows.IsCurrent(id)) continue;
                try { windows.Restore(id, before[id]); }
                catch (Exception rollback) { failed++; log.Write($"Rollback failed for PID {id.ProcessId}.", rollback); }
            }
            log.Write("Grid failed; rollback attempted.", ex);
            return new(false, $"{ex.Message} " + (failed == 0 ? "Surviving windows returned to their previous placements." : $"{failed} windows could not be rolled back. Use Restore Original Window Positions and check the log."));
        }
    }

    public OperationResult Focus(WindowIdentity id)
    {
        try
        {
            // A minimized window can be restored by focus; capture before that state change.
            var placement = windows.Capture(id);
            originals.TryAdd(id, placement);
            bool focused = windows.Focus(id);
            if (!focused) log.Write($"Foreground focus was denied for PID {id.ProcessId}.");
            return focused
                ? new(true, $"Focused client PID {id.ProcessId}.")
                : new(false, "Windows did not grant foreground focus. Click the client's taskbar button; no input is simulated.");
        }
        catch (Exception ex) { return Failure("Focus client", ex); }
    }

    public OperationResult RestoreOriginals()
    {
        int restored = 0, closed = 0, failed = 0, skipped = 0;
        foreach (var (id, placement) in originals.ToArray())
        {
            if (IsIgnored(id)) { skipped++; continue; }
            if (!windows.IsCurrent(id)) { originals.Remove(id); closed++; continue; }
            try { windows.Restore(id, placement); originals.Remove(id); restored++; }
            catch (Exception ex) { failed++; log.Write($"Restore failed for PID {id.ProcessId}.", ex); }
        }
        if (failed == 0 && skipped == 0) LastGrid = null;
        return new(failed == 0, $"Restored {restored} original placements; {skipped} ignored; {closed} closed; {failed} failed. Originally minimized clients are restored normally.");
    }

    private OperationResult Failure(string action, Exception ex)
    {
        log.Write(action, ex);
        return new(false, $"{action}: {ex.Message}");
    }
}
