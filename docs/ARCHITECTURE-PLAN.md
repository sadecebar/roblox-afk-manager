# Splitting the engine from the UI

Goal: the WPF window can be redesigned freely without touching what the manager does (launching, private servers, restarts, alerts, logs).

## Target shape

```
Core               pure rules and models: no WPF, no Windows calls
Engine             all behaviour; exposes state, commands and events; no WPF
Platform.Windows   Win32: windows, processes, log-file owners, idle time, Roblox singleton
App (WPF)          XAML, themes and thin view-models that only translate engine state for display
```

Dependency rule: App → Engine → Core, and Platform.Windows → Engine/Core. Engine never references WPF or Platform.Windows; it reaches Windows only through interfaces it owns. A test fails the build if Engine references WPF.

## Milestones

Every milestone ends with all tests passing, a published test build and no change in behaviour.

| # | Milestone | Done when |
|---|---|---|
| M0 | Baseline: v0.13.0 released and tagged; UI screenshots kept as a reference. | Tag `v0.13.0`, screenshots in `artifacts/ui-baseline-v0.13.0`. |
| M1 | Engine project and dependency rule. Move the WPF-free services (login vault, Discord, settings stores, logs, public Roblox lookups). | Engine builds without WPF; the rule test passes. |
| M2 | Platform layer. Move Win32 code behind interfaces owned by Engine; split the launcher's web logic from starting processes. | Engine builds and tests with fakes only. |
| M3 | Break `MainViewModel` into engine services, one capability at a time: launch, servers and fallback; health, reopen and recovery; alerts and activity; workspace and layout. A thin `AfkEngine` composes them. `MainViewModel` keeps its public API meanwhile. | `MainViewModel` holds no behaviour. |
| M4 | The contract: WPF-free state snapshots (`TileStatus` instead of brushes), commands that return results, prompts through an interface instead of `Func` hooks. Documented in `docs/ENGINE.md`. | The UI can be built from the contract alone. |
| M5 | Thin view-models and code-behind; colours come from the theme. | No business logic, Win32 or brushes outside views and themes. |
| M6 | Proof: a test that the UI project has no logic, plus a throwaway console front-end on the same engine. | The second front-end works without Engine changes. |
| M7 | UI/UX work starts. | |

## Known issues at the baseline

- The UI smoke test (`--ui`) fails at 640×360 with 16 clients: the card footer is too narrow for Show, Restart and Ignore, so Show drops below a 20 px hit target. It predates v0.12.0 and belongs to the UI/UX phase.

## Progress

- [x] M0: baseline
- [x] M1: `RobloxAfkManager.Engine` (plain `net10.0`, so WPF cannot be referenced) holds the login vault, Discord webhook, settings stores, file log, public Roblox lookups and the launcher/idle contracts. Secrets go through `ISecretProtector`; its DPAPI implementation still lives in Engine and moves in M2. Rule test: "Architecture: the engine references no user interface".
- [x] M2: `RobloxAfkManager.Platform.Windows` (no WPF) holds window and process handling, log-file owners, the Roblox single-instance handle, idle time and DPAPI. The launcher is split: the launch sequence and Roblox web calls are in Engine (`AccountLauncher`), the processes behind `IRobloxClients` (`WindowsRobloxClients`). `WindowsPlatform.Install()` plugs DPAPI in at startup. The launch sequence is now tested on fake processes. Win32 that is part of drawing the window stays in the app: live previews (DWM), the window-change hook (runs on the UI thread), tray notifications and phone screenshots.
- [x] M3: behaviour moved into engine services, each through its own step with all tests passing:
  - M3a `LaunchService`: game link, launch gap, private servers and the fixed split, staged fallback, Rejoin all, Reset servers, "never start an account twice". `ReplaceAsync` is the one path for Restart and every automatic fix.
  - M3b `HealthService`: Auto-reopen with its caps, login and quiet-log restarts, closing a disconnected second window, the Restart button.
  - M3c `AlertService`: activity entries and the incident log, Discord (ping, no ping, recovery, escalation), away detection and Sleep mode, the status board, crash reporting.
  - M3d `WorkspaceService` and `PlacementService`: saved profiles, card order, activity history, roster (last seen), backup and restore, saved window positions.
  `MainViewModel` went from 1,912 to about 1,000 lines. The services still borrow the client list and a few callbacks from the dashboard through `IEngineHost`. Still in the dashboard, because they act on the card objects themselves: detecting windows, matching them to accounts and restoring each account's saved name, ignore choice and plan, and the resource-plan, memory-limit and arrange actions. These move in M4 together with the engine's own client list, which replaces the cards as the source of truth.
- [x] M4: `AfkEngine` is the one thing a window talks to. It owns the client list (`EngineClient`), window detection, account matching and restoring saved choices, ignoring, names and card order, and the window and resource actions, and composes the five services. Status is plain data (`ClientStatus`: session, window state, tone); the card turns it into words and colours. Reset servers asks through `IUserPrompts`. `IEngineHost` is internal to the engine; `MainViewModel` builds the engine and mirrors its state (cards wrap `EngineClient`). The contract is `docs/ENGINE.md`. New test: the engine runs with no window. The dashboard renders pixel-identical to v0.13.0 at all nine baseline sizes (apart from the clock).
- [ ] M5
- [ ] M6
