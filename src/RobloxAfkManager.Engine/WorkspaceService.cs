using System.IO;
using System.Text.Json;
using RobloxAfkManager.Core;

namespace RobloxAfkManager.Engine;

// What the manager remembers about each account between runs (custom name, card order, ignored, preferred plan, last seen,
// saved window position), the activity history and the notifications switch. Never logins or cookies.
public sealed class WorkspaceService
{
    public const int MaxProfiles = 128, MaxHistory = 100;
    private static readonly TimeSpan RosterSaveInterval = TimeSpan.FromMinutes(5);
    private readonly IWorkspaceStore store;
    private readonly IAppLog log;
    private DateTimeOffset lastRosterSave = DateTimeOffset.MinValue;

    public WorkspaceService(IWorkspaceStore store, IAppLog log)
    {
        this.store = store; this.log = log;
        Load(store.Read());
    }

    // Keyed by Roblox user id. Still written directly by the dashboard's identification step until M4.
    public Dictionary<long, AccountProfile> Profiles { get; } = [];
    // Newest first.
    public List<ActivityEntry> History { get; } = [];
    public bool NotificationsEnabled { get; set; } = true;
    private HashSet<long> running = [];
    // Accounts that have a running, identified client, as of the last TrackRunning.
    public IReadOnlySet<long> RunningAccounts => running;

    private void Load(WorkspaceSettings settings)
    {
        Profiles.Clear(); foreach (var profile in settings.Profiles) Profiles.TryAdd(profile.UserId, profile);
        History.Clear(); History.AddRange(settings.History.Take(MaxHistory));
        NotificationsEnabled = settings.Notifications;
    }

    // False when the settings file could not be written (the reason is in the manager log).
    public bool Save()
    {
        try { store.Write(new(Profiles.Values.OrderBy(p => p.Order).Take(MaxProfiles).ToArray(), History.Take(MaxHistory).ToArray(), NotificationsEnabled)); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { log.Write("Workspace settings could not be saved."); return false; }
    }

    public void AddHistory(ActivityEntry entry) { History.Insert(0, entry); while (History.Count > MaxHistory) History.RemoveAt(History.Count - 1); }
    public void ClearHistory() => History.Clear();

    public bool HasSavedPlans => Profiles.Values.Any(p => p.PreferredPlan is not null);
    public int NextOrder => Profiles.Count == 0 ? 0 : Profiles.Values.Max(p => p.Order) + 1;

    // Remembers a client's name, ignore choice and (optionally) plan under its account.
    public void SaveProfile(long id, string? customName, bool ignored, ResourcePlan? preferredPlan = null, bool updatePlan = false)
    {
        if (!Profiles.TryGetValue(id, out var previous) && Profiles.Count >= MaxProfiles) return;
        Profiles[id] = new(id, customName, previous?.Order ?? NextOrder, ignored, updatePlan ? preferredPlan : previous?.PreferredPlan,
            previous?.Username, previous?.LastGame, previous?.LastSeen, previous?.Window);
    }

    // The accounts in their new on-screen order. The existing slots are reused so offline accounts keep their relative position.
    public void SaveOrder(IReadOnlyList<long> ordered)
    {
        var slots = ordered.Where(Profiles.ContainsKey).Select(id => Profiles[id].Order).Order().ToArray();
        for (int i = 0; i < ordered.Count && i < slots.Length; i++) Profiles[ordered[i]] = Profiles[ordered[i]] with { Order = slots[i] };
    }

    public void ForgetPreferredPlans() { foreach (long id in Profiles.Keys.ToArray()) Profiles[id] = Profiles[id] with { PreferredPlan = null }; }

    // ---- roster: which accounts are running, and when each was last seen -----------------------------------------------------------
    // Returns true when the settings were saved.
    public bool TrackRunning(IEnumerable<IClientView> clients, DateTimeOffset now, bool forceSave = false)
    {
        var current = clients.Where(c => c.AccountId is not null).GroupBy(c => c.AccountId!.Value).ToDictionary(g => g.Key, g => g.First());
        bool changed = !running.SetEquals(current.Keys);
        foreach (var (id, tile) in current)
        {
            if (!Profiles.TryGetValue(id, out var profile)) continue;
            Profiles[id] = profile with { Username = tile.DetectedName ?? profile.Username, LastGame = tile.KnownGameName ?? profile.LastGame, LastSeen = now };
        }
        running = current.Keys.ToHashSet();
        if (current.Count > 0 && (changed || forceSave || now - lastRosterSave > RosterSaveInterval)) { lastRosterSave = now; return true; }
        return false;
    }
    public IReadOnlyList<OfflineAccount> Offline(DateTimeOffset now) => AccountRoster.Offline(Profiles.Values, running, now);
    // Not listed as offline again until it runs.
    public bool ForgetOffline(long id)
    {
        if (!Profiles.TryGetValue(id, out var profile)) return false;
        Profiles[id] = profile with { LastSeen = null };
        return true;
    }

    // ---- saved window positions ---------------------------------------------------------------------------------------------------
    public int SavedPlacementCount => Profiles.Values.Count(p => p.Window is not null);
    public SavedPlacement? PlacementFor(long id) => Profiles.TryGetValue(id, out var p) ? p.Window : null;
    public bool SetPlacement(long id, SavedPlacement place)
    {
        if (!Profiles.TryGetValue(id, out var profile)) return false;
        Profiles[id] = profile with { Window = place };
        return true;
    }
    public void ClearPlacements() { foreach (var id in Profiles.Keys.ToArray()) if (Profiles[id].Window is not null) Profiles[id] = Profiles[id] with { Window = null }; }

    // ---- backup ---------------------------------------------------------------------------------------------------------------------
    // Preferences only (names, order, ignored accounts, saved window positions, activity). Never logins or cookies.
    public void Backup(string path) { Save(); File.WriteAllText(path, JsonSerializer.Serialize(store.Read())); }
    // Throws when the file is not a backup this manager can read. Returns how many account profiles were restored.
    public int Restore(string path)
    {
        if (new FileInfo(path).Length > 1_000_000) throw new InvalidDataException("That file is too large to be a backup.");
        var restored = JsonSerializer.Deserialize<WorkspaceSettings>(File.ReadAllText(path)) ?? throw new InvalidDataException("That file is empty.");
        store.Write(restored);          // validates and trims the data
        Load(store.Read());
        Save();
        return Profiles.Count;
    }
}
