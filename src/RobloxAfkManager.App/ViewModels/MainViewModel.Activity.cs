using RobloxAfkManager.Core;

namespace RobloxAfkManager.App.ViewModels;

// The activity list and the incident log. Moves to the engine in M3c.
public sealed partial class MainViewModel
{
    private IncidentJournal? incidents;
    // Where the morning-after record of problems and fixes is kept (next to the other settings).
    public string IncidentLogPath => System.IO.Path.Combine(settingsFolder, "incidents.log");
    private void Journal(ActivityEntry entry) => (incidents ??= new(IncidentLogPath)).Write(entry);

    private void AddActivity(string label, string message, bool warning, bool announce = true, bool info = false, bool quiet = false)
    {
        var entry = new ActivityEntry(DateTimeOffset.UtcNow, label, message, warning);
        Journal(entry);
        Activity.Insert(0, entry); while (Activity.Count > 100) Activity.RemoveAt(Activity.Count - 1);
        if (warning) { unreadAlerts++; if (notificationsEnabled) AlertRaised?.Invoke(entry); }
        if (announce) Announce(entry, warning, info, quiet);
        RaiseUnread(); Raise(nameof(HasActivity)); SaveWorkspace();
    }
}
