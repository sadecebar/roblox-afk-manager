using RobloxAfkManager.Core;

namespace RobloxAfkManager.Engine;

// Remembers where each account's window belongs and puts new windows there, so launched accounts don't all pile up in the
// bottom-right corner.
public sealed class PlacementService(IEngineHost host, WorkspaceService work, WindowManager manager, LaunchService launch)
{
    // Roblox positions and sizes its own window while it loads; moving it earlier gets undone.
    private static readonly TimeSpan PlacementDelay = TimeSpan.FromSeconds(8);
    // Roblox moves and resizes its own window while it loads, and a busy client can refuse a move. So a new window
    // is checked on every refresh for a while and put back whenever it isn't in its saved spot.
    private static readonly TimeSpan PlacementWatch = TimeSpan.FromSeconds(120), PlacementRetry = TimeSpan.FromSeconds(3);
    private const int PlacementMaxMoves = 15;
    private sealed class PlacementState { public int Moves; public DateTimeOffset Next; public bool MaximizedDone; public string? Failure; }
    private readonly Dictionary<ProcessIdentity, PlacementState> placement = [];
    private readonly HashSet<ProcessIdentity> placed = [];
    private bool placing;

    // Saves where every identified, visible window is now. Returns false when nothing could be saved.
    public async Task SaveLayoutAsync()
    {
        var clients = host.Clients;
        var tiles = clients.Where(c => c.AccountId is not null && !c.IsIgnored).ToArray();
        if (tiles.Length == 0)
        {
            host.SetStatus(clients.Count == 0 ? "No Roblox windows detected. Open your accounts first." : "Accounts aren't identified yet. Wait a minute for their names to appear, then try again.", true);
            return;
        }
        int skipped = clients.Count(c => !c.IsIgnored) - tiles.Length, saved = 0;
        var results = await Task.Run(() => tiles.Select(t => (t.AccountId!.Value, manager.CapturePlacement(t.Identity))).ToArray());
        foreach (var (id, place) in results)
        {
            if (place is null) { skipped++; continue; }
            if (!work.SetPlacement(id, place)) continue;
            saved++;
            placed.Add(ProcessIdentity.From(tiles.First(t => t.AccountId == id).Identity)); // already where it belongs
        }
        work.Save();
        host.SetStatus(saved == 0 ? "No positions saved. Windows must be open, identified and not minimized."
            : $"Saved {saved} positions. Each account now opens in its own spot." + (skipped > 0 ? $" {skipped} skipped (minimized or not identified yet)." : ""), saved == 0);
    }

    // Runs on every refresh.
    public async Task ApplySavedAsync()
    {
        if (placing || host.Busy || host.Disposed || work.SavedPlacementCount == 0) return;
        var now = DateTimeOffset.UtcNow;
        var clients = host.Clients;
        var live = clients.Select(c => ProcessIdentity.From(c.Identity)).ToHashSet();
        foreach (var gone in placement.Keys.Where(k => !live.Contains(k)).ToArray()) placement.Remove(gone);
        var due = new List<(IClientView Tile, SavedPlacement Place, PlacementState State)>();
        foreach (var tile in clients)
        {
            var process = ProcessIdentity.From(tile.Identity);
            if (tile.IsIgnored || placed.Contains(process)) continue;
            long? id = tile.AccountId ?? launch.LaunchedAccount(tile.ProcessId);
            if (id is null || work.PlacementFor(id.Value) is not { } place) continue;
            var age = now - new DateTimeOffset(new DateTime(tile.Identity.StartTimeUtcTicks, DateTimeKind.Utc));
            if (age < PlacementDelay) continue;
            if (age > PlacementWatch || (placement.TryGetValue(process, out var done) && done.Moves >= PlacementMaxMoves))
            {
                placed.Add(process);
                if (placement.TryGetValue(process, out var failed) && failed.Failure is { } why && !(failed.Moves < PlacementMaxMoves && Fits(tile, place, failed)))
                    host.SetStatus(why, true);
                continue;
            }
            if (!placement.TryGetValue(process, out var state)) placement[process] = state = new();
            if (now < state.Next || Fits(tile, place, state)) continue;
            due.Add((tile, place, state));
        }
        if (due.Count == 0) return;
        placing = true;
        try
        {
            var results = await Task.Run(() => due.Select(d => manager.ApplyPlacement(d.Tile.Identity, d.Place)).ToArray());
            for (int i = 0; i < due.Count; i++)
            {
                var state = due[i].State; state.Moves++; state.Next = DateTimeOffset.UtcNow + PlacementRetry;
                if (results[i].Success) { state.Failure = null; if (due[i].Place.Maximized) state.MaximizedDone = true; }
                else state.Failure = results[i].Message;
            }
            if (results.All(r => r.Success)) host.SetStatus($"Moved {due.Count} window{(due.Count == 1 ? "" : "s")} to their saved positions.", false);
        }
        finally { placing = false; }
    }

    // A maximized target can't be compared by its rectangle, so it counts as in place after one successful move.
    private static bool Fits(IClientView tile, SavedPlacement place, PlacementState state) =>
        place.Maximized ? state.MaximizedDone : tile.WindowBounds == place.Bounds;
}
