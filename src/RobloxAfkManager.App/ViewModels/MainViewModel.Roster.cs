using System.Collections.ObjectModel;
using System.Windows.Media;
using RobloxAfkManager.Core;

namespace RobloxAfkManager.App.ViewModels;

public sealed record OfflineAccountView(long UserId, string Name, string Details, ImageSource? Avatar)
{
    public string AvatarFallback => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
}

// Remembers which identified accounts were recently running so a closed client can be named.
public sealed partial class MainViewModel
{
    private static readonly TimeSpan RosterGrace = TimeSpan.FromMinutes(2);
    private readonly DateTimeOffset rosterStarted = DateTimeOffset.UtcNow;
    private IReadOnlySet<long> runningAccounts => work.RunningAccounts;
    private readonly Dictionary<long, ImageSource> avatarCache = [];
    public ObservableCollection<OfflineAccountView> OfflineAccounts { get; } = [];
    public AsyncCommand ForgetOfflineCommand { get; private set; } = null!;
    // Right after start, accounts are still being identified, so the count waits instead of flagging everyone.
    public bool RosterSettled => Clients.All(c => c.AccountId is not null) || DateTimeOffset.UtcNow - rosterStarted > RosterGrace;
    public int OfflineCount => RosterSettled ? OfflineAccounts.Count : 0;
    public bool HasOffline => OfflineCount > 0;
    public int UnidentifiedCount => Clients.Count(c => c.AccountId is null);
    public bool HasUnidentified => UnidentifiedCount > 0;
    public string UnidentifiedHint => UnidentifiedCount == 1 ? "1 running client isn't identified yet, so it may be listed here."
        : $"{UnidentifiedCount} running clients aren't identified yet, so they may be listed here.";

    private void InitializeRoster() => ForgetOfflineCommand = new(parameter =>
    {
        if (parameter is OfflineAccountView view && work.ForgetOffline(view.UserId))
        {
            SaveWorkspace(); RebuildOffline(DateTimeOffset.UtcNow);
            Message = $"{view.Name} won't be listed as offline until it runs again."; Warning = false;
        }
        return Task.CompletedTask;
    }, _ => true, HandleError);

    public void ReportCopied(string name) { Warning = false; Message = $"Copied \"{name}\". Log in to that account to bring it back."; }

    private void TrackRoster()
    {
        // The engine tracks which accounts run; the window keeps their last avatar to show them while offline.
        foreach (var tile in Clients.Where(c => c.AccountId is not null).GroupBy(c => c.AccountId!.Value).Select(g => g.First())) if (tile.Avatar is { } avatar) avatarCache[tile.AccountId!.Value] = avatar;
        RebuildOffline(DateTimeOffset.UtcNow);
    }

    private void RebuildOffline(DateTimeOffset now)
    {
        var offline = work.Offline(now);
        OfflineAccounts.Clear();
        foreach (var account in offline)
        {
            string details = (account.Game is { } game ? game + " · " : "") + "last seen " + AccountRoster.Ago(account.LastSeen, now);
            OfflineAccounts.Add(new(account.UserId, account.Name, details, avatarCache.GetValueOrDefault(account.UserId)));
        }
        if (OpenClosedCommand is not null && !launch.Launching) RefreshLogins();
        Raise(nameof(RosterSettled)); Raise(nameof(OfflineCount)); Raise(nameof(HasOffline));
        Raise(nameof(UnidentifiedCount)); Raise(nameof(HasUnidentified)); Raise(nameof(UnidentifiedHint));
        Raise(nameof(HealthSummary)); Raise(nameof(HealthTone));
    }
}
