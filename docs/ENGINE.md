# The engine contract

`RobloxAfkManager.Engine` does everything the manager does: it finds the Roblox windows, works out which account is in each, starts and restarts accounts, keeps them on the right private server, sends alerts and remembers settings. A window (the WPF app today, anything else tomorrow) only shows what the engine reports and passes clicks back.

This page is everything a window may use. If a redesign needs something that is not here, add it to the engine first; do not put behaviour in the window.

## Rules

- The engine has no user interface. It references neither WPF nor Windows Forms, and reaches Windows only through the interfaces in `EngineOptions` (a test enforces this: "Architecture: the engine references no user interface").
- A window reads engine state and calls engine methods. It never changes an `EngineClient` itself and never writes the settings files.
- Words, colours, icons, layout and animation belong to the window. The engine reports facts (`ClientStatus`, counts, flags); its status and activity messages are plain sentences a window may show as they are.
- Use the engine from one thread (the UI thread). Its events are raised on that thread, and its `await`s resume there.

## Starting it

```csharp
var engine = new AfkEngine(new EngineOptions
{
    Windows = new Win32WindowSystem(log),            // Platform.Windows, or a fake in tests
    Log = log,
    Resources = processResources, WorkingSet = processResources,
    AccountLookup = new LogAccountNameLookup(owners: LogFileOwners.Read),
    ClientNames = new ClientNameStore(), Workspace = new WorkspaceStore(),
    Launcher = new AccountLauncher(new WindowsRobloxClients()),
    Vault = new LoginVault(), SettingsFolder = AppFolders.Data,
    Webhook = new DiscordWebhook(log: log), Idle = new Win32UserIdle(),
    Prompts = myWindow,                               // IUserPrompts; without it the engine proceeds
});
engine.StateChanged += ...;                           // subscribe first
engine.Start();                                       // crash marker and "manager started" alert
await engine.RefreshAsync();                          // then on a timer and whenever windows change
```

Call `WindowsPlatform.Install()` once at startup (it plugs Windows DPAPI in for saved logins and webhooks). `Services/EngineFactory` builds the engine for the WPF app (Windows implementations, or the fakes a test passes in) and `MainViewModel` mirrors its state. `samples/RobloxAfkManager.ConsoleView` is a second, much smaller example: a text dashboard that uses nothing but this contract.

## State

### The clients: `engine.Clients`

Every detected Roblox client, in dashboard order. Each `EngineClient` has:

| Member | Meaning |
|---|---|
| `Window` | Title, bounds, memory, CPU and Windows status (`ClientWindow`) |
| `Identity`, `ProcessId` | Which window this is |
| `Slot` | Card number, shown as "Client 01" until the account is known |
| `AccountId`, `Account`, `Session` | The Roblox account and what its log says (null until known) |
| `Label` | Custom name, else Roblox username, else "Client 01" |
| `CustomName`, `DetectedName`, `KnownGameName` | The parts of the label and the game name |
| `IsIgnored` | Hidden from the dashboard and left out of bulk actions |
| `ResourceStatus` | The resource plan and memory limit applied, in words |
| `StatusAt(now)` | Its status as data (below) |
| `Changed` event | Anything about this client changed |

`engine.CanManage(client)` says whether a client is shown and included in bulk actions (not ignored, and identified when saved ignore choices still have to be restored).

### Status as data: `ClientStatus`

| Field | Values |
|---|---|
| `Session` | `Unknown`, `Joining`, `StuckJoining` (over 2 minutes), `Joined`, `Disconnected`, `NotResponding` |
| `Window` | `Normal`, `Minimized`, `OtherDesktop`, `NotResponding` |
| `Tone` | `Good` (joined), `Pending` (joining), `Warn` (window out of view), `Alert` (needs a look), `Idle` (unknown) |
| `NeedsAttention`, `SessionNeedsAttention` | A problem the person should look at |
| `CanPreview` | The window is on screen, so a live preview can be shown |

The log is trusted for 3 minutes after it was last read; after that the session is `Unknown`. A window chooses its own words and colours for each value.

### Everything else

| Where | What |
|---|---|
| `engine` | `Monitors`, `DetectedAt`, `Busy`, `ResolvingNames`, `SavedWindowCount`, `CanRestoreGrid`, `LastGrid`, resource plan and memory limit state (`ActivePlan`, `PlanAppliedCount`, `MemoryCapMiB`, `HasResourceChanges`…), `RequiresIdentification`, `AllowUnidentifiedClients` |
| `engine.Launch` | Game link (`GamePlaceId`, `SetGame`), private servers (`PrivateServer1Link`, `SetServerLink`, `AccountsOnServer1`, `ServerSummary`, `ServerShownFor`), `LaunchSpacingSeconds`, `HasLaunchTarget`, `Launching`, `IsActive`, `IsStarting`, `ResetDelay`, `PrivateJoinPatience`, saved logins (`Vault.Logins`) |
| `engine.Health` | `AutoReopen`, `AutoRecover`, `AutoRestartQuiet`, `ReopenAfter`, `IsPendingReopen`, `CanRestart(client)` |
| `engine.Alerts` | Away: `IsAway`, `Mode` (`Auto`, `Away` = Sleep, `AtPc`), `AwayMinutes`. Discord: `Webhook` (configured, board, last result), `IncidentLogPath` |
| `engine.Workspace` | `Profiles`, `History` (newest first), `NotificationsEnabled`, `RunningAccounts`, `Offline(now)`, `SavedPlacementCount`, `HasSavedPlans` |

## Events

| Event | When |
|---|---|
| `engine.StateChanged` | The client list, its order, names, ignore choices, monitors or resource state changed. Redraw lists and counts. |
| `engine.StatusChanged(message, warning)` | A status-bar message. `message` null: keep the text, change only the warning flag. |
| `engine.BusyChanged` | `Busy` changed; actions that need an idle engine can be enabled or disabled. |
| `engine.LaunchStateChanged` | Saved logins, servers, the game link or launch progress changed. |
| `engine.Alerts.ActivityAdded(entry, alert)` | A new activity entry. `alert`: a warning worth a desktop notification and an unread count. |
| `engine.Alerts.AwayChanged` | Away / at the PC (or its settings) changed. |
| `EngineClient.Changed` | One client changed. |

## Actions

Methods that report through `StatusChanged` return a `Task`; the others return their result.

| Area | Methods |
|---|---|
| Detection | `RefreshAsync(manual)`, `RefreshAccountNamesAsync()`, `SettleAccountsAsync()` |
| Clients | `SetClientName(client, name)` → `OperationResult`, `SwapClients(a, b)` → `bool`, `SetIgnoredAsync(client, ignored)` |
| Windows | `FocusAsync(client)`, `ArrangeAsync(layout, screen, main, mainScreen)`, `PrepareDashboardAsync()`, `RestoreGridAsync()`, `RestoreOriginalsAsync()` |
| Resources | `ApplyResourcePlanAsync(plan)`, `StopResourcePlansAsync(forget)` → `bool`, `ApplyMemoryCapAsync(MiB)`, `ResetMemoryCapAsync()` |
| Accounts (`Launch`) | `OpenClosedAsync()` (Rejoin all), `LaunchAsync(logins)`, `ResetServersAsync()`, `Resplit()`, `SetGame`, `SetServerLink`; `Vault.Save` / `Vault.Remove` for logins, `Launcher.IdentifyAsync` to check a pasted login |
| Health | `RestartAsync(client)` (the Restart button); the automatic fixes run by themselves after each refresh |
| Alerts | `SaveDiscord`, `ClearDiscord`, `SaveBoardChannel`, `ClearBoardChannel`, `SetBoard(on)` → `bool`, `TestAsync()`, `Announce` |
| Workspace | `Backup(path)`, `Restore(path)` → profile count, `ClearHistory()`, `ForgetOffline(id)`, `ClearPlacements()`, `Save()` → `bool` |
| Placements | `SaveLayoutAsync()` |

## Questions for the person: `IUserPrompts`

| Method | Asked |
|---|---|
| `ConfirmResetServers(count)` | Before Reset servers closes `count` running accounts. Return false to cancel. |

Without prompts (tests, headless use) the engine proceeds.

## What stays in a window

Text and colours for every status and count, layout, the Activity and Offline lists, avatar images (`AvatarImages.Decode` turns the engine's PNG bytes into a picture), live window previews (DWM), the window-change hook that triggers a refresh, desktop and tray notifications, the phone page, and the dialogs that answer `IUserPrompts`.
