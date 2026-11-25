# Changelog

## v0.11.0: Open selected, spacing, backups

### New
- **Settings page.** One **Settings** button with five tabs (Accounts, Alerts, Layout, Plans, Phone). The Arrange, Plans and Mobile header buttons moved into it; Activity, Offline and Ignored stay in the header because they show live badges.
- **Discord alerts.** Optional webhook that messages a channel when an account disconnects or is kicked, its game or window closes, it freezes, or it loses its login. Batched, pings only you, stored encrypted. See the README.
- **Away mode.** Alerts are sent only while you're away from the PC (automatic after 5 minutes without keyboard or mouse, or forced either way). A header chip shows At PC / Away. Closing a client yourself at the PC never pings you.
- Password boxes now follow the dark theme.
- **Open selected.** Tick accounts in the saved logins list and start just those.
- **Seconds between accounts.** The gap between launches is now a setting (5 to 120 seconds, default 10).
- **Clear message when Roblox refuses a second client.** Instead of a silent failure or "didn't start", the manager explains the single-instance limit.
- **Back up / Restore settings.** Names, order, ignored accounts and saved window positions. Logins are never in a backup.
- A reminder next to the cookie box that a saved login gives full access to the account.

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
