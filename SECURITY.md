# Security policy

## Reporting a vulnerability

Please **do not open a public issue** for security problems. Use GitHub's private reporting instead: open the repository's **Security** tab and choose **Report a vulnerability**.

Include what you found, how to reproduce it and which version you used. Leave out real access codes, Tailscale addresses, account IDs and usernames.

## What counts

The areas that matter most:

- The mobile dashboard server: authentication, access codes, the loopback-only binding and the read-only routes.
- Process identity checks before window or resource changes (for example, acting on a reused process ID).
- Handling of data from Roblox log files and public Roblox APIs.
- Anything that could leak local settings, logs, screenshots or the encrypted saved logins (`logins.dat`).

## Supported versions

Only the latest release receives fixes.
