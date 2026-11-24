using RobloxAfkManager.Core;

namespace RobloxAfkManager.Tests;

internal sealed class FakeWindowSystem : IWindowSystem
{
    public Dictionary<WindowIdentity, Placement> Placements { get; } = [];
    public Dictionary<WindowIdentity, PixelRect> Bounds { get; } = [];
    public WindowLimits Limits { get; set; } = new(200, 150);
    public MonitorInfo Screen { get; set; } = new("TEST", new(-1920, 40, 1920, 1040), 96, true);
    public HashSet<WindowIdentity> Closed { get; } = [];
    public List<WindowIdentity> Moved { get; } = [];
    public List<WindowIdentity> Restored { get; } = [];
    public List<WindowIdentity> Prepared { get; } = [];
    public List<WindowIdentity> Focused { get; } = [];
    public WindowIdentity? FailCapture { get; set; }
    public WindowIdentity? FailMove { get; set; }
    public WindowIdentity? RejectSize { get; set; }
    public WindowIdentity? FailRestore { get; set; }
    public bool FocusAllowed { get; set; } = true;
    public bool MonitorPresent { get; set; } = true;
    public FakeWindowSystem(int count = 2, long? startTicks = null)
    {
        for (int i = 1; i <= count; i++)
        {
            var id = new WindowIdentity(i, i + 100, startTicks ?? i * 1000);
            var rect = new PixelRect(100 + i * 10, 100 + i * 10, 800, 600);
            Bounds[id] = rect; Placements[id] = new(0, 1, 0, 0, 0, 0, rect);
        }
    }
    public IReadOnlyList<ClientWindow> Detect() => Placements.Keys.Where(IsCurrent).Select(id => new ClientWindow(id, "Test client", Bounds[id], 100_000_000, "Open")).ToArray();
    public IReadOnlyList<MonitorInfo> GetMonitors() => MonitorPresent ? [Screen] : [];
    public bool IsCurrent(WindowIdentity id) => Placements.ContainsKey(id) && !Closed.Contains(id);
    public WindowLimits GetLimits(WindowIdentity id, uint dpi) { Check(id); return Limits; }
    public Placement Capture(WindowIdentity id) { Check(id); if (id == FailCapture) throw new InvalidOperationException("Capture failed"); return Placements[id]; }
    public void MoveNormal(WindowIdentity id, PixelRect rect)
    {
        Check(id); Moved.Add(id); Bounds[id] = id == RejectSize ? rect with { Width = rect.Width + 100 } : rect;
        Placements[id] = Placements[id] with { NormalBounds = Bounds[id], ShowCommand = 1 };
        if (id == FailMove) throw new InvalidOperationException("Move failed after partial mutation");
    }
    public List<WindowIdentity> Closing { get; } = [];
    public void Close(WindowIdentity id) { Check(id); Closing.Add(id); Closed.Add(id); }
    public void Maximize(WindowIdentity id, PixelRect area)
    {
        Check(id); Moved.Add(id); Bounds[id] = area;
        Placements[id] = Placements[id] with { ShowCommand = 3 };
    }
    public PixelRect ReadBounds(WindowIdentity id) { Check(id); return Bounds[id]; }
    public bool IsNormal(WindowIdentity id) => IsCurrent(id) && Placements[id].ShowCommand is 1 or 4;
    public void Restore(WindowIdentity id, Placement p)
    {
        Check(id); if (id == FailRestore) throw new InvalidOperationException("Restore failed");
        Restored.Add(id); Placements[id] = p with { ShowCommand = p.ShowCommand == 3 ? 3u : 4u }; Bounds[id] = p.NormalBounds;
    }
    public bool Focus(WindowIdentity id) { Check(id); Focused.Add(id); if (Placements[id].ShowCommand == 2) Placements[id] = Placements[id] with { ShowCommand = 4 }; return FocusAllowed; }
    public void RestoreForPreview(WindowIdentity id) { Check(id); Prepared.Add(id); if (Placements[id].ShowCommand == 2) Placements[id] = Placements[id] with { ShowCommand = 4 }; }
    private void Check(WindowIdentity id) { if (!IsCurrent(id)) throw new InvalidOperationException("Stale identity"); }
}
internal sealed class TestLog : IAppLog
{
    public List<string> Entries { get; } = [];
    public void Write(string message, Exception? exception = null) => Entries.Add(message + exception?.Message);
}
