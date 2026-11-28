using RobloxAfkManager.Core;

namespace RobloxAfkManager.App.ViewModels;

// Remembers where each account's window belongs and puts new windows there, so launched
// accounts don't all pile up in the bottom-right corner.
public sealed partial class MainViewModel
{
    // Roblox positions and sizes its own window while it loads; moving it earlier gets undone.
    private static readonly TimeSpan PlacementDelay = TimeSpan.FromSeconds(8);
    private readonly HashSet<ProcessIdentity> placed = [];
    private bool placing;
    public AsyncCommand SaveLayoutCommand { get; private set; } = null!;
    public AsyncCommand ClearLayoutCommand { get; private set; } = null!;
    public int SavedPlacementCount => profiles.Values.Count(p => p.Window is not null);
    public string LayoutSummary2 => SavedPlacementCount == 0 ? "No positions saved yet." : $"{SavedPlacementCount} account positions saved. New windows open there.";

    private void InitializePlacements()
    {
        SaveLayoutCommand = new(SaveLayoutAsync, () => !Busy, HandleError);
        ClearLayoutCommand = new(() =>
        {
            foreach (var id in profiles.Keys.ToArray()) if (profiles[id].Window is not null) profiles[id] = profiles[id] with { Window = null };
            SaveWorkspace(); RaisePlacements(); Message = "Saved positions cleared."; Warning = false;
            return Task.CompletedTask;
        }, () => SavedPlacementCount > 0, HandleError);
    }

    private void RaisePlacements() { Raise(nameof(SavedPlacementCount)); Raise(nameof(LayoutSummary2)); ClearLayoutCommand?.Notify(); SaveLayoutCommand?.Notify(); }

    private async Task SaveLayoutAsync()
    {
        var tiles = Clients.Where(c => c.AccountId is not null && !c.IsIgnored).ToArray();
        if (tiles.Length == 0)
        {
            Warning = true;
            Message = Clients.Count == 0 ? "No Roblox windows detected. Open your accounts first." : "Accounts aren't identified yet. Wait a minute for their names to appear, then try again.";
            return;
        }
        int skipped = Clients.Count(c => !c.IsIgnored) - tiles.Length, saved = 0;
        var results = await Task.Run(() => tiles.Select(t => (t.AccountId!.Value, manager.CapturePlacement(t.Identity))).ToArray());
        foreach (var (id, place) in results)
        {
            if (place is null) { skipped++; continue; }
            if (!profiles.TryGetValue(id, out var profile)) continue;
            profiles[id] = profile with { Window = place }; saved++;
            placed.Add(ProcessIdentity.From(tiles.First(t => t.AccountId == id).Identity)); // already where it belongs
        }
        SaveWorkspace(); RaisePlacements();
        Warning = saved == 0;
        Message = saved == 0 ? "No positions saved. Windows must be open, identified and not minimized."
            : $"Saved {saved} positions. Each account now opens in its own spot." + (skipped > 0 ? $" {skipped} skipped (minimized or not identified yet)." : "");
    }

    // Roblox moves and resizes its own window while it loads, and a busy client can refuse a move. So a new window
    // is checked on every refresh for a while and put back whenever it isn't in its saved spot.
    private static readonly TimeSpan PlacementWatch = TimeSpan.FromSeconds(120), PlacementRetry = TimeSpan.FromSeconds(3);
    private const int PlacementMaxMoves = 15;
    private sealed class PlacementState { public int Moves; public DateTimeOffset Next; public bool MaximizedDone; public string? Failure; }
    private readonly Dictionary<ProcessIdentity, PlacementState> placement = [];

    private async Task ApplySavedPlacementsAsync()
    {
        SaveLayoutCommand?.Notify(); ClearLayoutCommand?.Notify();
        if (placing || Busy || disposed || SavedPlacementCount == 0) return;
        var now = DateTimeOffset.UtcNow;
        var live = Clients.Select(c => ProcessIdentity.From(c.Identity)).ToHashSet();
        foreach (var gone in placement.Keys.Where(k => !live.Contains(k)).ToArray()) placement.Remove(gone);
        var due = new List<(ClientTileViewModel Tile, SavedPlacement Place, PlacementState State)>();
        foreach (var tile in Clients)
        {
            var process = ProcessIdentity.From(tile.Identity);
            if (tile.IsIgnored || placed.Contains(process)) continue;
            long? id = tile.AccountId ?? launch.LaunchedAccount(tile.ProcessId);
            if (id is null || !profiles.TryGetValue(id.Value, out var profile) || profile.Window is not { } place) continue;
            var age = now - new DateTimeOffset(new DateTime(tile.Identity.StartTimeUtcTicks, DateTimeKind.Utc));
            if (age < PlacementDelay) continue;
            if (age > PlacementWatch || (placement.TryGetValue(process, out var done) && done.Moves >= PlacementMaxMoves))
            {
                placed.Add(process);
                if (placement.TryGetValue(process, out var failed) && failed.Failure is { } why && !(failed.Moves < PlacementMaxMoves && Fits(tile, place, failed)))
                { Warning = true; Message = why; }
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
            if (results.All(r => r.Success)) { Warning = false; Message = $"Moved {due.Count} window{(due.Count == 1 ? "" : "s")} to their saved positions."; }
        }
        finally { placing = false; }
    }

    // A maximized target can't be compared by its rectangle, so it counts as in place after one successful move.
    private static bool Fits(ClientTileViewModel tile, SavedPlacement place, PlacementState state) =>
        place.Maximized ? state.MaximizedDone : tile.WindowBounds == place.Bounds;
}
