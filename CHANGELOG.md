# Changelog

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
- The manager still can't open or log in to accounts for you. That would need your login cookies, which it never touches.

## v0.8.0: First public release

- New "night shift" look: near-black canvas, violet accent, moon mark.
- Color-coded session status on every card, with red cards for clients that need attention.
- Header health summary: all clear / needs attention, joined count, combined RAM.
- Unread badge on Activity, count badge on Ignored, live dot on Mobile.
- Redesigned Plans, Mobile, Ignored and Activity panels.
- Matching redesign of the phone dashboard.
- MIT license, security policy and issue templates.
