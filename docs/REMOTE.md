# Remote mobile monitoring

The optional mobile dashboard is read-only. Desktop previews, ignoring, resource plans, RAM limits and window controls keep their existing behavior. Monitoring starts **off** each time the manager opens. No NVIDIA setting is changed.

## Run the updated app

Close the old manager normally so it restores any policies/limits it applied. Run `Run.cmd` inside the extracted release folder (or the `artifacts/RobloxAfkManager-v<version>-win-x64` package if you built it yourself). If an optional repository-local `.dotnet` SDK exists, the launcher can use its runtimes. For a package copied elsewhere, install both **.NET 10 Desktop Runtime x64** and **ASP.NET Core Runtime 10 x64** from [Microsoft's .NET 10 downloads](https://dotnet.microsoft.com/en-us/download/dotnet/10.0), then run `RobloxAfkManager.exe`. The .NET 10 SDK for Windows also supplies both for development.

In the manager, choose **Mobile → Start monitoring**. Copy the displayed access code. On the same PC, open `http://127.0.0.1:8765` and paste it. Check that two included clients produce real images before setting up remote access. The random 48-character code changes after Stop/Start or a manager restart. Anyone who has both network access and this code can view the included clients. It is never put in URLs or logs.

## Private access from Windows to iPhone

1. [Install Tailscale](https://tailscale.com/docs/install) on the Windows PC and the iPhone. Sign both into your private tailnet, normally using the same account, and connect both devices. No router port forwarding, public web hosting or exit node is needed.
2. Enable MagicDNS and HTTPS certificates for your tailnet if not already enabled. The Serve command may display a setup link. Follow Tailscale's [HTTPS instructions](https://tailscale.com/docs/how-to/set-up-https-certificates). Certificate names appear in public certificate-transparency logs; choose a PC hostname that does not reveal personal information.
3. With monitoring started, open PowerShell on the PC and run:

   ```powershell
   tailscale serve --bg http://127.0.0.1:8765
   tailscale serve status
   ```

   If `tailscale` is not on PATH, use `& 'C:\Program Files\Tailscale\tailscale.exe'` in its place. If Serve is already hosting something on this PC's HTTPS port, inspect `tailscale serve status` before changing it. Use a separate HTTPS port if necessary, for example `tailscale serve --bg --https=8443 http://127.0.0.1:8765`, and include `:8443` in Safari's URL.

4. Tailscale prints a private address such as `https://your-pc.your-tailnet.ts.net`. Open **that exact HTTPS address** in Safari on the connected iPhone. The loopback address `127.0.0.1` is only for testing on the PC and will not reach the PC from your phone. Enter the access code from the manager.
5. Turn off iPhone Wi-Fi and repeat on mobile data with Tailscale connected. Tap any client to enlarge its preview. Close Safari or switch apps: captures cease when the viewer lease expires (within about 20 seconds plus an in-flight capture). The game sessions continue independently.

[Tailscale Serve](https://tailscale.com/docs/features/tailscale-serve) is private to your tailnet. **Do not use Tailscale Funnel**, router forwarding, or a public reverse proxy for this dashboard. The manager never configures those services automatically. It binds only IPv4 loopback and does not create firewall rules. Tailscale must run on both devices.

If your tailnet includes other people, restrict access to this PC's TCP 443 (or your selected Serve port) using your tailnet's [access controls](https://tailscale.com/docs/features/access-control). Review device membership and remove devices you do not trust. The dashboard's additional code is required even over Tailscale. Sharing the code alone does not provide a network path.

To stop monitoring, press **Mobile → Stop** or close the manager normally. To disable only this Serve endpoint, run `tailscale serve --bg --https=443 off` (use the selected port and the same flags used to enable it). `tailscale serve reset` clears all Serve configuration on the device, including unrelated services, so do not use it for a shared setup. Serve can remain configured while the manager is off; it cannot serve game data without the local server. See the [Serve command reference](https://tailscale.com/docs/reference/tailscale-cli/serve).

## What the phone shows

- A responsive grid of included clients, their detected username or custom account label (with Client number as the fallback), PID, working-set RAM and window responsiveness. Name changes appear on the next metadata poll. Ignored clients stay hidden and are never captured by this feature.
- Real individual-window JPEGs, bounded to **640 × 360**, quality 65. The capture target is one update per client about every **8 seconds**, with one capture in progress at a time. Safari polls lightweight metadata every four seconds and downloads only changed images, in batches of three.
- Tap a client for an enlarged image, exact last successful screenshot time and current status. This first version keeps the same low update rate in the enlarged view to reduce load.
- Failed or old previews are marked, and a disconnected phone retains the last image with an explicit stale-data message. Closed/not-detected clients remain for about one minute before disappearing. Custom account labels are user-entered and Client numbers are session labels; neither is proof of account identity. PID and process start time prevent reuse of another process's image.

A detected process, a responding window, or even an updating image **does not prove the account is farming or that a rejoin succeeded**. V0.7 also shows game names, time since a reported join and log-based session states. The desktop Activity panel can warn about disconnects and possibly stalled joins; it does not send phone push notifications. See [SESSIONS.md](SESSIONS.md).

## Resource use and capture limits

The desktop keeps using DWM thumbnails. Only an authenticated request for remote metadata activates screenshots. There is no recording or disk screenshot archive. The server stores one latest JPEG per client, with an **8 MiB total image-cache limit**, a 512 KiB per-image limit and a 128-record bound. It closes each capture session/frame pool and releases D3D textures after a single capture attempt. Metadata reuses the existing event-driven/10-second discovery. With no viewer, the lightweight worker checks once per second and clears cached images after the lease expires; it does not capture or hold game textures.

Windows Graphics Capture requires **Windows 10 1903 or newer**, a supported graphics device and an interactive desktop. It targets one window, so a covered window can be captured without capturing unrelated windows in front of it. Windows may briefly display a capture border. Minimized/cloaked/unresponsive windows are skipped. Protected content, exclusive rendering, a locked desktop, driver/device loss or Roblox stopping presentation may produce unavailable, blank or frozen images. Capturing a frame is not a guarantee that the game rendered a new scene. The manager does not move, minimize, restore, resize, focus, suspend or restart a game for capture, and does not bypass capture restrictions. There is no desktop-screenshot or PrintWindow fallback.

When system memory is nearly full, bounded capture textures and the web host can still add pressure. Test two clients, then eight, observing responsiveness and a full rejoin cycle. No claim of zero overhead is made. The capture bridge uses one source-size frame buffer and a temporary CPU-readable texture per attempt; large source windows cost more transient memory than the final JPEG.

## Troubleshooting and acceptance checks

1. **App will not start:** check both required .NET runtimes, or use the workspace `Run.cmd`. Keep the capture DLL and all release files beside the EXE.
2. **Start fails:** another manager/service may already use port 8765. Close the duplicate normally; the app does not terminate it or bind to a public port as a fallback.
3. **Phone cannot connect:** first verify the local URL on the PC, then `tailscale status` and `tailscale serve status`, the exact HTTPS URL, both devices' connection, HTTPS setup and tailnet access policy. PC sleep/shutdown or internet loss stops availability. No automatic power-plan changes are made.
4. **Unauthorized:** copy the current access code from Mobile. Old codes stop working after restarting monitoring. Safari stores the code only for this tab's session; Disconnect clears it. A page reload can reuse that tab session.
5. **No image:** inspect the status and image age. Test a normal, non-minimized game window. Keep it covered and verify its image changes without its placement changing. Do not assume a black or identical image proves a stuck game.
6. **Ignore test:** ignore one client on the PC; it must disappear from the phone on the next metadata poll, and its old image URL must return 404. Show it again and wait for a new preview.
7. **Rejoin test:** observe two clients through 40+ minutes, then try eight. Closed/recreated windows receive fresh capture identities; saved ignore choices return only after the account ID is matched. While saved ignored accounts exist, unidentified clients are withheld unless explicitly released from the Ignored menu.
8. **Disconnect test:** close Safari for 30 seconds, confirm capture load settles, reconnect, and confirm fresh timestamps. Disconnect Tailscale and verify the remote URL fails. No game should close or change placement.

Tailscale login, tailnet policy, iPhone Safari over mobile data and long-running Roblox rejoins require your real devices.

