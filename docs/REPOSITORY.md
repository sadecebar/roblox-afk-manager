# Keeping the repository clean

The repository holds application code, tests, original icon artwork, documentation and build scripts. Generated packages, SDKs, caches, screenshots, logs, browser profiles, access-code files and local editor settings are excluded by `.gitignore`.

## Before every commit

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\Check-Repository.ps1
git status --short
git diff --check
```

The repository check inspects Git's tracked and publishable untracked files. It rejects generated outputs, private-key and token patterns, email addresses, personal home paths, your Windows user and computer names, and real Tailscale addresses. Matches are reported by filename and rule, never by value. Pass extra values to catch, such as a Roblox username, with `-ExtraSensitiveValues 'name1','name2'`.

It is a safety net, not a guarantee. Review your changes before pushing.

## Files that must never be committed

| File | Why |
| --- | --- |
| `%LOCALAPPDATA%\RobloxAfkManager\client-names.json` | Your custom account labels. |
| `%LOCALAPPDATA%\RobloxAfkManager\workspace.json` | Account IDs, labels, preferences and the last 100 Activity events. |
| `%LOCALAPPDATA%\RobloxAfkManager\Logs\` | Process and window IDs, local error details. |
| `artifacts/` | Test screenshots, packages and temporary browser-test files. |

Both settings filenames are also excluded by `.gitignore` and rejected by the check, in case one is copied into the checkout.

When attaching logs or screenshots to an issue, remove usernames, account IDs, Tailscale addresses and access codes first.

## Build portability

No SDK, native compiler output or generated binary is committed. The capture DLL is built from source. Build scripts use a repository-local `.dotnet` SDK when present, otherwise `dotnet` on PATH.

Managed build paths are mapped to `/_/` so debug information never embeds the checkout's home path. Native capture compilation uses a relative source filename for the same reason. Release packages omit debug symbols, fixtures and screenshots.

## Git identity

Commits record the author's name and email publicly. To keep a personal address out of the history, set a GitHub noreply address for this repository only:

```powershell
git config user.name "<username>"
git config user.email "<id>+<username>@users.noreply.github.com"
```

Find the exact address under GitHub **Settings → Emails**, and enable **Keep my email addresses private**.
