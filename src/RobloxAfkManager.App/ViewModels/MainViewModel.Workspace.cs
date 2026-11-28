using System.Collections.ObjectModel;
using RobloxAfkManager.Core;

namespace RobloxAfkManager.App.ViewModels;

// Saved profiles and the activity list as the window shows them. The engine's WorkspaceService keeps and saves them.
public sealed partial class MainViewModel
{
    private WorkspaceService work = null!;
    private int unreadAlerts;
    public ObservableCollection<ActivityEntry> Activity { get; } = [];
    public event Action<ActivityEntry>? AlertRaised;
    private bool notificationsEnabled => work.NotificationsEnabled;
    public bool NotificationsEnabled { get => work.NotificationsEnabled; set { if (work.NotificationsEnabled == value) return; work.NotificationsEnabled = value; Raise(); SaveWorkspace(); } }
    public string ActivityLabel => unreadAlerts == 0 ? "Activity" : $"Activity ({unreadAlerts})";
    public bool HasActivity => Activity.Count > 0;
    public int UnreadAlerts => unreadAlerts;
    public bool HasUnreadAlerts => unreadAlerts > 0;
    public int SavedProfileCount => work.Profiles.Count;
    public bool HasSavedPlans => work.HasSavedPlans;
    public string ProfileSummary => $"{work.Profiles.Count} saved account profiles";
    public bool AllowUnidentifiedClients { get => engine.AllowUnidentifiedClients; set { if (engine.AllowUnidentifiedClients == value) return; engine.AllowUnidentifiedClients = value; Raise(); } }
    public int PendingProfileCount => engine.RequiresIdentification ? Clients.Count(c => c.AccountId is null && !c.IsIgnored) : 0;
    public bool HasPendingProfiles => PendingProfileCount > 0;
    public string PendingProfileHint => $"Identifying {PendingProfileCount} clients before restoring saved ignore choices. Unidentified clients stay hidden and unmanaged.";

    private void InitializeWorkspace()
    {
        work = engine.Workspace;
        foreach (var entry in work.History) Activity.Add(entry);
    }
    private void SaveWorkspace()
    {
        if (!work.Save()) { Warning = true; Message = "Could not save workspace preferences. Check the app's local settings folder."; }
        Raise(nameof(SavedProfileCount)); Raise(nameof(ProfileSummary));
        Raise(nameof(HasSavedPlans)); ResetResourcePlanCommand?.Notify();
    }
    public void MarkActivityRead() { unreadAlerts = 0; RaiseUnread(); }
    private void RaiseUnread() { Raise(nameof(ActivityLabel)); Raise(nameof(UnreadAlerts)); Raise(nameof(HasUnreadAlerts)); }
    public void ClearActivity() { Activity.Clear(); work.ClearHistory(); MarkActivityRead(); Raise(nameof(HasActivity)); SaveWorkspace(); }
}
