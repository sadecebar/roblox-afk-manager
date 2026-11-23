# Testing

Start with two clients before testing larger groups. Build tests exercise only synthetic process/window data unless an interactive desktop option is selected.

## Automated checks

```powershell
.\build.ps1
.\tests\run-desktop-tests.ps1 -Configuration Release
```

The build runs deterministic checks for identity validation, restoration, ignored clients, resource-policy rollback, cache limits, preview cropping and grid layouts with 1–64 clients. CPU sampling tests cover processor normalization, rapid refreshes, unavailable readings and process reuse. Swap tests cover stable labels/order, ignored clients, new arrivals and recreated windows without invoking game-window actions.

The desktop suite requires an interactive Windows desktop. It checks WPF bindings and tile/control bounds, renders synthetic layouts, and opens two disposable fixture windows to test DWM previews, capture, window controls and resource restoration. Mutations are restricted to the fixture PIDs. Do not manually manage the labelled fixture windows from another manager during these tests. The Mobile panel test requires port 8765 to be available.

To check a generated package:

```powershell
.\build.ps1 -Publish
.\tests\run-desktop-tests.ps1 -Configuration Release -Package
```

The optional `-RealPreviews` switch opens previews of running Roblox clients and checks Fill/Fit and Ignore/Show behavior. It verifies that placements, show states, resource policies and resident limits remain unchanged. It does not apply resource settings to real clients. Omit it for fixture-only checks.

The optional `-Pointer` switch briefly moves the mouse over a temporary manager window containing synthetic clients. It checks title/preview drag-to-swap, suppression of focus during a drag, and ordinary preview clicks afterward. Leave the pointer alone during this check. It restores the original cursor position and closes its host afterward; real game windows are not controlled. It requires input access to an unlocked interactive desktop.

Add `-AccountNames` to test Windows log ownership against a synthetic file held by the test process, plus public username/avatar lookup for Roblox user ID 1 and game metadata for public universe 13058. These require internet access and send no personal account IDs. Deterministic tests also cover engine-only session parsing, timestamp validation, alert deduplication/grace periods, profile restoration across process restarts, saved-ignore protection, preferences/history bounds and manual edits during lookup. `-Pointer` also exercises the pencil editor before swapping cards.

The test executable's optional `--account-details-live` flag reads current clients' matched log metadata and queries public names/game details/avatars. It does not instantiate the dashboard, write settings or mutate game windows; it reports counts only. This requires an interactive desktop and internet access. Package startup testing runs the real application, including saved preferred plans: use a clean local workspace settings file or inspect those preferences before enabling `-Package`.

## Browser checks

After the native desktop test creates a synthetic capture fixture, use Node.js 22 or newer and Microsoft Edge:

```powershell
node tests/remote-browser.mjs --configuration=Release
```

The script uses a temporary browser profile and local server with invented client data. It checks mobile/desktop layout, authentication, preview enlargement, offline/reconnect behavior and disconnect cleanup. Set `EDGE_PATH` to an Edge executable if it is installed elsewhere. `DOTNET_EXE` can select a .NET host; otherwise the local SDK or PATH is used.

All test screenshots, browser profiles and temporary access-code files belong under ignored `artifacts/`. They must not be committed. Temporary profiles and codes are removed on normal browser-test completion; forced termination can leave them behind.

## Two-client acceptance check

1. Open two Roblox clients and start the manager. Both tiles should appear without moving their game windows. On a fresh installation resource plans start off; saved plans return only after identification. RAM limits and mobile monitoring always start off.
2. Keep games non-minimized and cover them with the manager. Confirm the previews show the expected windows and continue updating when the game renders.
3. Click each preview. The matching game should come forward without closing or minimizing other games.
4. Ignore a client. Its tile must disappear without moving/focusing its window. Show it from **Ignored** and confirm its tile returns.
5. Manually minimize an included client, then use **Restore windows**. An ignored minimized client must remain untouched.
6. Resize/maximize/minimize the manager. Previews must remain within their tiles and return when the manager is restored.
7. Switch **Fill / Fit**. Fill should cover the image area without drawing over metadata or Ignore controls; Fit should show the complete source. Neither should stretch the image or resize games.
8. Observe at least two normal rejoin cycles. The manager does not initiate rejoining or prove that farming continues.

Drag the title or preview of one card onto the other. Their dashboard positions should swap while their labels remain unchanged. Press **Refresh**, then Ignore/Show one client: the chosen order should remain. Clicking a preview or **Show** should still focus the correct game. Select a card and use **Ctrl+Left/Right** to swap using the keyboard. Identified accounts now retain their order after restarting the manager.

Check CPU readings after two metadata updates. They should show measured usage; they are not a CPU throttle. The first reading is a dash. In a dense layout, hover over a preview to see CPU, RAM and full status.

Wait for the game to finish joining, then allow several 30-second name checks. When available, each title should show the correct username. If a title remains `Client NN`, use its pencil button or F2 to enter the name. Verify Save/Enter, Escape cancellation, and **Use automatic name**. A manual name must follow the process through swaps, refresh and Ignore/Show, and reach the mobile dashboard on its next poll. Reopening the manager should retain a manual name for an already-running process; restarting that Roblox process must not inherit a name using only its reused PID. Real automatic naming depends on Roblox's current log format and file ownership reporting.

## Resource controls

With one client ignored, apply each preset to the included client. Verify Low/Balanced use Below Normal CPU priority and High uses Normal. Ignore/Show, Reset all and normal manager exit must restore or reapply settings as documented. Access failures should be reported without elevation or a repeated retry loop.

Test the resident RAM limit separately. Watch physical working set, committed memory, disk activity and responsiveness. A smaller working set does not mean assets or allocations were removed. Turn the limit off if paging stalls the game. Verify restoration on Off, Ignore, Reset all and normal exit. Forced termination cannot restore settings.

## Larger layouts and displays

After two-client behavior is satisfactory, test eight, twelve, sixteen or more clients within the PC's capacity. Cards adapt their columns and metadata density to the available size; twelve clients may use 6 × 2 on a wide desktop or 4 × 3 on a smaller window. Partial rows should fill their width too. Adding/removing clients must not create a dashboard scrollbar or overlap tiles.

Check mixed-DPI displays, negative monitor coordinates, source resizing, virtual desktops and fullscreen transitions. Test keyboard focus and Escape dismissal in settings panels, plus Windows reduced-animation settings. Monitor CPU/GPU usage and handle counts over a longer session.

DWM renders outside WPF's bitmap pipeline, so automated layout images show placeholders. Native checks verify thumbnail registration and geometry; actual game animation still needs visual observation. Black pixels drawn by a game, protected content and stopped background rendering cannot be fixed by Fill mode.

For remote authentication, private networking and capture checks, see [REMOTE.md](REMOTE.md).

Follow [SESSIONS.md](SESSIONS.md) for profile persistence, avatar/game/duration checks, Activity notifications and real disconnect/rejoin acceptance. Unknown identities must not inherit another account's preferences. Ignored clients must stay excluded from alerts and mobile viewing.
