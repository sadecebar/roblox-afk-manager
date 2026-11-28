namespace RobloxAfkManager.App.ViewModels;

// The dashboard side of saved window positions. Saving them and putting new windows back in place is the engine's PlacementService.
public sealed partial class MainViewModel
{
    private PlacementService placements = null!;
    public AsyncCommand SaveLayoutCommand { get; private set; } = null!;
    public AsyncCommand ClearLayoutCommand { get; private set; } = null!;
    public int SavedPlacementCount => work.SavedPlacementCount;
    public string LayoutSummary2 => SavedPlacementCount == 0 ? "No positions saved yet." : $"{SavedPlacementCount} account positions saved. New windows open there.";

    private void InitializePlacements()
    {
        placements = new PlacementService(this, work, manager, launch);
        SaveLayoutCommand = new(async () => { await placements.SaveLayoutAsync(); RaisePlacements(); }, () => !Busy, HandleError);
        ClearLayoutCommand = new(() =>
        {
            work.ClearPlacements();
            SaveWorkspace(); RaisePlacements(); Message = "Saved positions cleared."; Warning = false;
            return Task.CompletedTask;
        }, () => SavedPlacementCount > 0, HandleError);
    }

    private void RaisePlacements() { Raise(nameof(SavedPlacementCount)); Raise(nameof(LayoutSummary2)); ClearLayoutCommand?.Notify(); SaveLayoutCommand?.Notify(); }

    private Task ApplySavedPlacementsAsync()
    {
        SaveLayoutCommand?.Notify(); ClearLayoutCommand?.Notify();
        return placements.ApplySavedAsync();
    }
}
