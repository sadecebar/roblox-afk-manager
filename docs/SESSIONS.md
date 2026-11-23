# Account profiles and session monitoring

V0.7 adds remembered account preferences, richer cards and an Activity panel. All features use existing window observations and bounded reads of Roblox's local logs. No game input, restart, injection or GPU-driver changes are involved.

## Profiles

Once the log is matched to its exact owning process, its local account user ID keys a profile. The app remembers custom name, card order, ignored status and preferred Low/Balanced/High priority plan. Names and order persist even if the process ID changes. A typed alias is never used to identify a different process.

Saved ignore choices are restored before saved plans. When any saved ignored account exists, clients awaiting identification are withheld from the dashboard, remote feed and bulk actions. If logs are unavailable, open **Ignored → Show unidentified clients this session** to release those clients explicitly; their prior ignore choice cannot be guaranteed until identification succeeds.

Applying a plan remembers it for identified included accounts. Normal exit restores their original Windows settings and retains the preference. **Reset all** restores settings and clears saved plan preferences, including offline profiles. RAM caps are not saved or re-enabled automatically. Ignoring an account restores its active settings; its preferred plan remains available if it is later included again.

The file `%LOCALAPPDATA%\RobloxAfkManager\workspace.json` contains at most 128 profiles (including last username, last game and last-seen time) and 100 history entries, plus the desktop-notification preference. Writes replace the file atomically. Invalid files fall back to empty settings. The legacy `client-names.json` still supplies process-local aliases for unidentified clients. These files are personal data, excluded from Git and release packaging. Do not run two managers simultaneously against the same settings.

## Offline accounts

Each identified profile also records the account's last known username, last game and the time it was last seen running. **Offline** lists accounts that are not running now, are not ignored, and were seen within the last 24 hours, ordered like the dashboard. It answers "which account closed?" so you know which login to open again.

- Last-seen times update while an account runs and are saved when the set of running accounts changes, at least every five minutes, and on normal exit.
- For the first two minutes after the manager starts (or until every running client is identified), the header count waits, so clients still being identified are not reported as offline. The panel notes how many running clients are still unidentified.
- **Copy name** puts the account name on the clipboard. **Forget** stops listing an account until it runs again.
- Avatars appear for accounts seen during the current manager session; otherwise an initial is shown.
- The manager cannot open or log in to an account. That would require login cookies, which it never reads.

## Cards

- Username/custom name remains the main label. The avatar is a public 48 × 48 headshot; missing or failed images show an initial.
- Game name comes from the universe ID in the latest parsed join report and Roblox's public games API. If unavailable, the card shows the place ID or “Game not identified.”
- Duration is elapsed time since the last reported join. It resets on a new join and disappears when a join attempt or disconnect is observed. It does not measure productive play time.
- “Joined (log),” “Joining (log)” and “Disconnected (log)” describe log evidence. A three-minute-old log check becomes “Session unknown.” Window responsiveness remains available independently in the tooltip.
- High-density cards hide secondary details and avatars to preserve preview size and controls. Full information remains in the tooltip. Mobile cards also show game/session information; public avatars remain desktop-only.

Public usernames and successful game/avatar responses are cached in memory for up to one hour. Failed artwork requests back off for two minutes. A pass downloads at most six small avatars with three concurrent downloads. No avatars or game captures are archived to disk. PNG response size, dimensions, HTTPS hosts and redirects are restricted before decoding.

## Activity and alerts

**Activity** shows the latest 100 events and a count of unread warnings. Opening it clears the badge; **Clear** deletes the saved history. **Desktop notifications** controls silent Windows notification-area balloons. Windows focus/notification settings may suppress them, but history remains available. Notifications are limited to one per ten seconds; multiple simultaneous problems still receive individual history entries.

| Event | Evidence and timing |
| --- | --- |
| Window not responding | Observed unresponsive for at least 20 seconds; one warning until recovery. |
| Window closed / no longer detected | Missing for at least 15 seconds; this does not identify a crash or its cause. |
| Disconnect | An explicit `FLog::Network` disconnect/lost-connection marker in the matched log. |
| Possibly stuck joining | An explicit join attempt without a later join report for over two minutes. This is a heuristic, not proof of a loading failure. |
| Recovery | A previously unresponsive window responds, or a later join report follows an observed disconnect. |

Window checks use the existing ten-second/event-driven refresh; log checks run at most every 30 seconds and rotate through bounded recent files. Alerts therefore have polling and lookup latency. They do not continuously inspect game frames. Only exact engine log tags are parsed; text printed by a game under `FLog::Output` is excluded. Some loading failures may emit no supported marker and will not be detected. Ignored accounts receive no new alerts. These notifications do not restart or reconnect clients, and phone push notifications are not implemented.

## Two-account acceptance test

1. Identify two accounts, assign an alias, swap cards and ignore one. Close the manager normally and reopen it. Check the alias, positions and ignore choice.
2. Restart one Roblox account yourself. Wait for identification and verify that its profile returns; a different account must not inherit its profile from a reused PID.
3. Apply Low to an included account. Reopen the manager and check that Low returns only after identification; ignored clients remain untouched. Use Reset all and reopen again to confirm it stays cleared.
4. Confirm avatars and game names match. Compare the duration with the last join; watch it reset on a normal rejoin.
5. Close an included client yourself and wait for the missing-window alert. Repeated refreshes should not repeat it. Review Activity, toggle notifications and clear history.
6. Watch a real disconnect/rejoin and compare the log-based badge with the game. A static frame or an open window alone is not evidence of a healthy session.

References: [Roblox public games API](https://prod.docsiteassets.roblox.com/assets/en-us/cloud/legacy/games/v1.json), [public thumbnails API](https://prod.docsiteassets.roblox.com/assets/en-us/cloud/legacy/thumbnails/v1.json), [Windows notification-area API](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shell_notifyiconw).
