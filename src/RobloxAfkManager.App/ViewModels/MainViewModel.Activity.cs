using RobloxAfkManager.Core;

namespace RobloxAfkManager.App.ViewModels;

// The Activity list in the window mirrors what the engine's AlertService reports. The incident log and Discord are handled there.
public sealed partial class MainViewModel
{
    // Where the morning-after record of problems and fixes is kept (next to the other settings).
    public string IncidentLogPath => alerts.IncidentLogPath;
    private void AddActivity(string label, string message, bool warning, bool announce = true, bool info = false, bool quiet = false) =>
        alerts.Add(label, message, warning, announce, info, quiet);

    private void OnActivityAdded(ActivityEntry entry, bool alert)
    {
        work.AddHistory(entry);
        Activity.Insert(0, entry); while (Activity.Count > WorkspaceService.MaxHistory) Activity.RemoveAt(Activity.Count - 1);
        if (alert) { unreadAlerts++; if (notificationsEnabled) AlertRaised?.Invoke(entry); }
        RaiseUnread(); Raise(nameof(HasActivity)); SaveWorkspace();
    }
}
