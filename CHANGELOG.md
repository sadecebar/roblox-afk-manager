# Changelog

## Unreleased

### Incident log
- **`incidents.log` records what happened overnight.** One line per event: time, account, what went wrong (including Roblox's own disconnect reason, such as "authentication failure") and what the manager did about it (closed and reopened from the saved login, or that the fix failed). It is written even when you're at the PC and rotates at 1 MB. It sits in the manager's settings folder.

### Fixes
- **Disconnects are now detected.** The log reader missed the lines Roblox writes when the server connection ends ("Client has been disconnected with reason…", "Lost connection with reason…"), so a client left on an error screen (such as error 257) stayed "Joined" with no alert and no rejoin. Both are recognized now.
- **Auto-rejoin waits 5 minutes** (was 60 seconds) before closing and restarting a disconnected client, so one that reconnects by itself is left alone.

## v0.11.0: Discord alerts, live status board, Settings page

Released 2026-11-25. Everything new is optional and off until you set it up. Nothing stores or asks for your password.

### Discord alerts (Settings → Alerts)
- **Alerts to your phone.** Add a channel webhook and the manager messages Discord when an account disconnects or is kicked, its game or window closes, it freezes, or Roblox ends its login. Add your Discord user ID to get a real ping.
- **Only when you're away.** A header chip shows **At PC**, **Away** or **Sleep**. You count as away after 2 minutes without keyboard or mouse (changeable). Closing a client yourself at the PC never pings you. **Sleep mode** counts you as away even while you use the PC.
- **No spam.** Events that arrive together are one message, and when an account comes back the bot **edits the original alert** (green line, "back at 03:41") instead of posting again. Capped at 30 messages an hour.
- **Live status board.** One message lists every account (joined, joining, closed, disconnected, not running) and is edited in place when something changes. It can have **its own channel**, apart from the alerts and log channel. When the manager closes, the board is marked "manager closed".
- The webhook address is stored encrypted for your Windows user, sent only to Discord, never logged. Messages can only mention you.

### Automatic rejoin (off by default)
- **Reopen crashed or kicked accounts while you're away.** A closed window, or a client kicked and left on the error screen, is started again from its saved login. It waits 25 seconds, handles one account at a time, and reopens each account at most 3 times an hour (24 in total). It never acts while you're at the PC.
- **Open selected** accounts, and **Seconds between accounts** (5 to 120) for launches.
- A clear message when Roblox refuses a second client, instead of a silent failure.

### Interface
- **Settings page** with five tabs (Accounts, Alerts, Layout, Plans, Phone). Arrange, Plans and Mobile moved into it. Activity, Offline and Ignored stay in the header.
- **Alerts** and **Rejoin** chips in the header show **on**, **standby** or **off** with a colored dot.
- Buttons and tabs have matching icons. Buttons squish when pressed, switches slide, panels fade and slide in. All animation is skipped when Windows animations are off.
- **Back up / Restore settings** (names, order, ignored accounts, window positions; never logins).
- Dark password boxes, refreshed screenshots.

### Security and privacy
- The in-app login window now only allows Roblox's own https pages; links, redirects and pop-ups to anything else are blocked.
- Discord webhook addresses must be real `discord.com` webhook addresses; redirects are not followed.
- Automated tests can no longer touch a real settings folder, saved logins or webhook (they use a temporary folder).
- No known vulnerable packages. See [SECURITY.md](SECURITY.md) to report a problem.

### Good to know
- A saved login is as powerful as your password. Don't use this on a shared PC.
- Running several Roblox clients at once still needs Roblox's single-instance limit lifted by a tool you choose. The manager only starts the clients.
- If the PC loses power, no alert can be sent, so silence is not proof that everything is fine.

## v0.10.0: Open accounts, arrange windows

Optional, and off until you save a login. Nothing here stores a password.

### New
- **Open closed.** One button (plus a per-account **Open**) starts every saved account that isn't running, 10 seconds apart, in the game you set.
- **Log in to add an account.** Roblox's own login page in an embedded browser. You type your own password; only the resulting session is saved, encrypted for your Windows user. You can also paste a cookie.
- **Game to open.** Paste a game link or place id in Settings → Accounts.
- **Arrange.** Grid on the monitor you choose, with an optional maximized main client.
- **Save positions.** Each account's window goes back to its saved spot when reopened.
- **Restart an account when Roblox ends its login** (off by default). Detects a burst of 401 errors in the client's log, then restarts only that account. At most 4 per hour.

### Details
- Accounts are started by running `RobloxPlayerBeta.exe` directly. The `roblox-player:` link is never used, because its installer closes every running client.
- Adds one dependency, Microsoft.Web.WebView2, for the login window.
- Web requests identify themselves honestly as this tool.
- Removed the launcher script `Run.cmd`. Run `RobloxAfkManager.exe` directly.

## v0.9.0: Offline accounts

One of your accounts closed and you can't tell which? The manager now remembers.

### New
- **Offline panel.** A new **Offline** button in the header lists every account that ran in the last 24 hours but isn't running now. Each entry shows the account's avatar, name, last game and when it was last seen ("last seen 14 min ago").
- **Copy name.** One click copies the account name, so you know exactly which login to open.
- **Forget.** Hide an account you closed on purpose. It comes back to the list automatically the next time it runs and closes again.
- **Header health chip** now reads **"N offline"** in amber when everything running is fine but expected accounts are missing. Red still means a running client needs attention.

### Details
- Saved profiles now also store each account's last known username, last game and last-seen time, locally in `%LOCALAPPDATA%\RobloxAfkManager\workspace.json`. Older settings files load without changes.
- Last-seen times are saved when the set of running accounts changes, at least every five minutes, and when the manager closes normally.
- For the first two minutes after start (or until every running client is identified), the offline count waits, so clients still being identified aren't reported as offline.
- Ignored accounts never appear in the Offline list.

### Good to know
- Tracking starts once you run v0.9.0. Accounts that closed before you upgraded can't be listed.

## v0.8.0: First public release

- New "night shift" look: near-black canvas, violet accent, moon mark.
- Color-coded session status on every card, with red cards for clients that need attention.
- Header health summary: all clear / needs attention, joined count, combined RAM.
- Unread badge on Activity, count badge on Ignored, live dot on Mobile.
- Redesigned Plans, Mobile, Ignored and Activity panels.
- Matching redesign of the phone dashboard.
- MIT license, security policy and issue templates.
