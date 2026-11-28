using System.Collections.ObjectModel;
using RobloxAfkManager.Core;

namespace RobloxAfkManager.App.ViewModels;

public sealed partial class MainViewModel
{
    private WorkspaceService work = null!;
    private Dictionary<long, AccountProfile> profiles => work.Profiles;
    private readonly SessionHealthTracker health = new();
    private readonly HashSet<ProcessIdentity> restoredProfiles = [];
    private bool notificationsEnabled { get => work.NotificationsEnabled; set => work.NotificationsEnabled = value; }
    private int unreadAlerts;
    private bool allowUnidentifiedClients;
    private bool restoringWorkspace, manualOrderChanged;
    private readonly HashSet<ProcessIdentity> editedNames = [], editedIgnores = [];
    public ObservableCollection<ActivityEntry> Activity { get; } = [];
    public event Action<ActivityEntry>? AlertRaised;
    public bool NotificationsEnabled { get => notificationsEnabled; set { if (notificationsEnabled == value) return; notificationsEnabled = value; Raise(); SaveWorkspace(); } }
    public string ActivityLabel => unreadAlerts == 0 ? "Activity" : $"Activity ({unreadAlerts})";
    public bool HasActivity => Activity.Count > 0;
    public int UnreadAlerts => unreadAlerts;
    public bool HasUnreadAlerts => unreadAlerts > 0;
    public int SavedProfileCount => profiles.Count;
    public bool HasSavedPlans => profiles.Values.Any(p => p.PreferredPlan is not null);
    public string ProfileSummary => $"{profiles.Count} saved account profiles";
    public bool AllowUnidentifiedClients { get => allowUnidentifiedClients; set { if (Set(ref allowUnidentifiedClients, value)) RaiseSelection(); } }
    public int PendingProfileCount => RequiresProfileIdentification ? Clients.Count(c => c.AccountId is null && !c.IsIgnored) : 0;
    public bool HasPendingProfiles => PendingProfileCount > 0;
    public string PendingProfileHint => $"Identifying {PendingProfileCount} clients before restoring saved ignore choices. Unidentified clients stay hidden and unmanaged.";
    private bool RequiresProfileIdentification => accountNames is IAccountDetailsLookup && profiles.Values.Any(p => p.Ignored);
    private bool CanManageProfile(ClientTileViewModel tile) => !tile.IsIgnored && (!RequiresProfileIdentification || tile.AccountId is not null || AllowUnidentifiedClients);

    private void InitializeWorkspace(IWorkspaceStore store)
    {
        work = new WorkspaceService(store, log);
        foreach (var entry in work.History) Activity.Add(entry);
    }
    private void SaveWorkspace()
    {
        if (!work.Save()) { Warning = true; Message = "Could not save workspace preferences. Check the app's local settings folder."; }
        Raise(nameof(SavedProfileCount)); Raise(nameof(ProfileSummary));
        Raise(nameof(HasSavedPlans)); ResetResourcePlanCommand.Notify();
    }
    private void SaveProfile(ClientTileViewModel tile, ResourcePlan? preferredPlan = null, bool updatePlan = false)
    {
        if (tile.AccountId is not { } id) return;
        work.SaveProfile(id, tile.CustomName, tile.IsIgnored, preferredPlan, updatePlan);
        SaveWorkspace();
    }
    private void SaveOrder()
    {
        // Existing slots are reused so accounts that are currently offline keep their relative position.
        work.SaveOrder(Clients.Where(c => c.AccountId is not null).Select(c => c.AccountId!.Value).Distinct().ToArray());
        SaveWorkspace();
    }
    private async Task ApplyAccountDetailsAsync(IReadOnlyDictionary<ProcessIdentity, AccountDetails> details, ProcessIdentity[] requested)
    {
        bool changed = false;
        var live = Clients.Select(c => ProcessIdentity.From(c.Identity)).ToHashSet();
        restoredProfiles.RemoveWhere(p => !live.Contains(p));
        editedNames.RemoveWhere(p => !live.Contains(p)); editedIgnores.RemoveWhere(p => !live.Contains(p));
        foreach (var tile in Clients.ToArray())
        {
            var process = ProcessIdentity.From(tile.Identity);
            if (!requested.Contains(process) || !details.TryGetValue(process, out var info)) continue;
            long? previousId = tile.AccountId;
            if (previousId is not null && info.Session.UserId is not null && previousId != info.Session.UserId)
            {
                var reset = await Task.Run(() =>
                {
                    var a = resources.Restore(process); var b = memoryCaps.Restore(process);
                    return new OperationResult(a.Success && b.Success, a.Message + " " + b.Message);
                });
                if (!reset.Success) { Warning = true; Message = reset.Message; continue; }
                editedNames.Remove(process); editedIgnores.Remove(process);
                tile.SetCustomName(null);
                changingIgnore = true;
                try { manager.SetIgnored(tile.Identity, false); tile.IsIgnored = false; }
                finally { changingIgnore = false; }
            }
            tile.SetAccountDetails(info);
            if (tile.AccountId is not { } id) continue;
            if (previousId != id) restoredProfiles.Remove(process);
            if (restoredProfiles.Contains(process)) continue;
            if (profiles.TryGetValue(id, out var profile))
            {
                if (editedNames.Contains(process)) profile = profile with { CustomName = tile.CustomName };
                if (editedIgnores.Contains(process)) profile = profile with { Ignored = tile.IsIgnored };
                profiles[id] = profile;
                // Restore ignored status before any resource policy, even if a global plan is active.
                if (profile.Ignored)
                {
                    var reset = await Task.Run(() =>
                    {
                        var a = resources.Restore(process); var b = memoryCaps.Restore(process);
                        return new OperationResult(a.Success && b.Success, a.Message + " " + b.Message);
                    });
                    if (!reset.Success) { Warning = true; Message = reset.Message; continue; }
                }
                changingIgnore = true;
                try { manager.SetIgnored(tile.Identity, profile.Ignored); tile.IsIgnored = profile.Ignored; }
                finally { changingIgnore = false; }
                tile.SetCustomName(profile.CustomName);
                if (!tile.IsIgnored && (resources.ActivePlan ?? profile.PreferredPlan) is { } plan)
                {
                    var applied = await Task.Run(() => resources.ApplySaved(plan, process));
                    if (!applied.Success) { Warning = true; Message = applied.Message; }
                    else if (resources.ActivePlan is not null) profiles[id] = profile with { PreferredPlan = plan };
                }
            }
            else if (profiles.Count < 128)
            {
                profiles[id] = new(id, tile.CustomName, profiles.Count == 0 ? 0 : profiles.Values.Max(p => p.Order) + 1,
                    tile.IsIgnored, resources.AppliedPlan(process));
            }
            restoredProfiles.Add(process); changed = true;
        }
        if (changed)
        {
            if (manualOrderChanged) SaveOrder();
            else
            {
                var ordered = Clients.OrderBy(c => c.AccountId is { } id && profiles.TryGetValue(id, out var p) ? p.Order : int.MaxValue).ToArray();
                Reconcile(Clients, ordered); SaveWorkspace();
            }
            if (Clients.All(c => c.AccountId is not null)) manualOrderChanged = false;
        }
        RaiseResources(); RaiseSelection(); ObserveHealth(); TrackRoster();
    }
    private void ObserveHealth()
    {
        alerts.UpdateAway();
        var events = health.Observe(Clients.Select(c => new ClientHealth(ProcessIdentity.From(c.Identity), c.Label,
            c.IsIgnored || (RequiresProfileIdentification && c.AccountId is null), c.Status, c.Session)), DateTimeOffset.UtcNow);
        alerts.Report(events);
        alerts.AnnounceReturns();
        keeper.Track(IsAway); alerts.EscalateStuck();
        alerts.UpdateBoard();
    }
    public void MarkActivityRead() { unreadAlerts = 0; RaiseUnread(); }
    private void RaiseUnread() { Raise(nameof(ActivityLabel)); Raise(nameof(UnreadAlerts)); Raise(nameof(HasUnreadAlerts)); }
    public void ClearActivity() { Activity.Clear(); work.ClearHistory(); MarkActivityRead(); Raise(nameof(HasActivity)); SaveWorkspace(); }
}
