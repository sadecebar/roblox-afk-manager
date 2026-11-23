# Win32 and DWM implementation notes

## Account names

`LogFileOwners` uses `RmStartSession`, `RmRegisterResources`, `RmGetList` and `RmEndSession` only to query which process currently holds a player log. `RM_UNIQUE_PROCESS` supplies PID plus process creation FILETIME; both must match a detected Roblox process. No Restart Manager shutdown/restart functions are imported or called. Filename timestamps and launch order are never identity evidence. [RmGetList](https://learn.microsoft.com/en-us/windows/win32/api/restartmanager/nf-restartmanager-rmgetlist), [RM_UNIQUE_PROCESS](https://learn.microsoft.com/en-us/windows/win32/api/restartmanager/ns-restartmanager-rm_unique_process).

`LogAccountNameLookup` considers at most 64 recent desktop player logs and checks at most 16 per pass, rotating least-recently-checked files. It reads at most 128 KiB from the beginning and 128 KiB from the end of a matched log. Only `[FLog::GameJoinLoadTime] Report game_join_loadtime` records supply a user ID. Multiple matching processes or conflicting IDs are rejected. Roblox's log format is not a stable public contract, so this is best-effort identification and manual labels remain available. Raw logs are neither saved nor sent. [Roblox log location and naming](https://en.help.roblox.com/hc/en-us/articles/360016022492-How-to-Retrieve-Roblox-Studio-Logs).

Public username resolution uses `POST https://users.roblox.com/v1/users` with up to 100 matched user IDs. It uses no cookies, credentials or redirects, bounds response data to 64 KiB, times out network requests, validates returned IDs, and caches successful names in memory for up to an hour. The existing metadata refresh starts at most one lookup task per 30 seconds; manual Refresh can request another pass. No additional polling timer is created. [Roblox Users API schema](https://prod.docsiteassets.roblox.com/assets/en-us/cloud/legacy/users/v1.json).

The view model accepts results only for the same PID and process start time still present in its collection. User overrides take precedence over automatic names and are stored in a bounded local JSON file, outside the checkout. Clearing an override returns to automatic naming. This changes manager labels only; it never changes the Roblox window title or account settings.

## V0.3.1 resident RAM limits

`Core/WorkingSetCaps.cs` independently tracks each process's initial working-set bounds and hard-enforcement state. The native adapter opens a separately validated handle with `PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_QUOTA`, reads `GetProcessWorkingSetSizeEx`, and uses `SetProcessWorkingSetSizeEx` with `QUOTA_LIMITS_HARDWS_MAX_ENABLE` plus `QUOTA_LIMITS_HARDWS_MIN_DISABLE`. Values are MiB converted to pointer-sized byte counts. [SetProcessWorkingSetSizeEx](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-setprocessworkingsetsizeex).

Readback must match the requested bounds and enabled bits. Restoration explicitly sends disable flags where the original enforcement bit was absent; sending zero flags could otherwise leave enforcement unchanged. Process identity and the same opened handle cover each write/readback/rollback. These limits regulate resident pages, not committed allocations. No job object, EmptyWorkingSet, -1/-1 request or repeated trim timer is used. [GetProcessWorkingSetSizeEx](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-getprocessworkingsetsizeex).

RAM-limit application, removal and restoration run off the UI thread. Failed rollback retains originals for retry. Ignoring restores both the resource policy and resident limit before hiding the tile. New included processes receive the active cap once; metadata refreshes do not continually rewrite it. Bounds restoration can page data out even when returning to an unenforced original maximum.

## Resource priority controls

`Core/ResourceProfiles.cs` stores the first CPU, memory-priority and power-policy snapshot for each PID plus process creation time. `Services/Win32ProcessResources.cs` opens a handle with `PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_INFORMATION`, validates creation time with `GetProcessTimes` and verifies the executable filename with `QueryFullProcessImageName`. All reads, writes, verification and rollback for an operation use that same handle, preventing PID reuse from redirecting a write.

`GetPriorityClass`/`SetPriorityClass` control CPU scheduling priority. Plans use only Below Normal or Normal. `GetProcessInformation`/`SetProcessInformation` read and write `ProcessMemoryPriority` and `ProcessPowerThrottling`. Low enables the execution-speed EcoQoS bit, Balanced returns it to Windows management, and High disables it explicitly. Other power-policy bits are preserved. Readback compares explicitly controlled bits because Windows owns uncontrolled policy state. [Priority classes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setpriorityclass), [process information](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocessinformation), [power policies](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessinformation).

Plan application is independent per client. Failed writes attempt a verified rollback; failed restoration retains the original snapshot for Reset. Ignoring a managed process restores its original settings before the tile can disappear. Normal window close runs an asynchronous reset and stays open if it fails. A crash/forced termination cannot restore session-only snapshots. Closed processes are pruned; recreated HWNDs keep process policies, while a new process receives a new snapshot. Repeated metadata refreshes do not repeatedly reapply a policy or retry denied writes.

Priority plans are scheduler and paging hints, separate from the working-set limit above. There is no frame-rate limit, job-object allocation/CPU cap, process suspension, driver setting change, configuration editing, injection or process-memory read/write access. Memory priority influences page reclamation under pressure; effects are workload dependent. [Memory priority](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/ns-processthreadsapi-memory_priority_information).

## Compact live dashboard

The UI displays every included client in a responsive preview grid **inside the manager**. It does not call the desktop grid planner. The V0.1 arrangement backend and its safeguards remain in the core library for compatibility and regression coverage.

`Core/DashboardLayout.cs` evaluates row/column combinations using the viewport width and height, fixed tile chrome and median source-window proportions. `Controls/CompactDashboardPanel.cs` measures and arranges all visible tiles to those bounds in WPF logical units, with no scroll extent or virtualized offscreen clients. Secondary labels collapse at high density; full details remain in the preview tooltip. This calculation never resizes the real game windows.

`Controls/LiveWindowPreview.cs` uses `DwmRegisterThumbnail` to associate a validated Roblox HWND with the manager's own top-level HWND. `DwmQueryThumbnailSourceSize` supplies the aspect ratio; `DwmUpdateThumbnailProperties` positions and crops the live compositor image into the preview region. The destination and source must be top-level windows, and the destination belongs to this process. [DwmRegisterThumbnail](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmregisterthumbnail), [DwmUpdateThumbnailProperties](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmupdatethumbnailproperties).

WPF element bounds are converted with `PointToScreen` and `ScreenToClient`, so the destination rectangle is expressed in physical client-area pixels. Fill mode preserves proportions with a centered source crop and covers the preview area; Fit mode preserves the full source with letterboxing. Native thumbnails do not inherit WPF clipping: the control explicitly intersects destination bounds with the preview's own rectangle, the manager client rectangle and clipping ancestors, adjusting the matching source rectangle. Clipping to the preview itself prevents Fill from painting over the client header/footer. These operations affect only DWM thumbnail coordinates. [DWM_THUMBNAIL_PROPERTIES](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ns-dwmapi-dwm_thumbnail_properties).

No timer captures or copies frames. DWM supplies image updates; the application updates geometry on layout/source-bound changes and retries failed registrations on metadata refresh. Source identity is checked before registration. Minimized/cloaked sources get an explanatory placeholder. Preview registrations are hidden when the manager minimizes and released with `DwmUnregisterThumbnail` on source changes, unloading or manager close. DWM registration success does not guarantee that Roblox continues rendering while occluded. [DWM thumbnail overview](https://learn.microsoft.com/en-us/windows/win32/dwm/thumbnail-ovw).

Clicking a preview invokes the guarded Focus operation for that exact identity. The ignore checkbox is outside the focus button, so toggling it never focuses the game. The view model maintains separate visible and ignored collections. Removing a tile unloads its preview and releases its DWM handle; the Ignored menu restores the tile without a game-window operation. Ignore is keyed by PID plus creation time: it survives metadata refresh or HWND recreation within a process, but does not carry over to a restarted process. The core filters ignored clients from every bulk operation, including original restoration. Hidden clients are not eligible for the dashboard's focus command.

Restore minimized clients captures original placement first, then calls `ShowWindowAsync(SW_SHOWNOACTIVATE)` only if the included client is minimized. Open normal/maximized clients retain their current geometry. It never requests grid sizing or foreground focus. The following sections document shared APIs and the retained V0.1 layout backend.

Window/DWM imports are in `src/RobloxAfkManager.App/Native/NativeMethods.cs`, with resource-policy imports in `Services/Win32ProcessResources.cs` and the optional manager title-bar theme call in the view. Native operations are isolated behind `IWindowSystem` and `IProcessResourceSystem`; deterministic tests use in-memory implementations.

## Detection and identity

| API | Use |
| --- | --- |
| `EnumWindows` | Enumerates top-level HWNDs. |
| `GetWindowThreadProcessId` | Associates each window with a detected process; checked again before mutation. |
| `GetAncestor(GA_ROOT)`, `GetWindow(GW_OWNER)`, `GetWindowLongPtrW` | Rejects child, owned and tool windows; checks the resizable-frame style before arranging. No styles are modified. |
| `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)`, `QueryFullProcessImageName` | Verifies the executable filename; closes the safe process handle after each query. No memory write rights are requested. |
| `GetWindowText` | Reads the top-level window title without accessing game internals. |
| `IsWindow`, `IsWindowVisible`, `IsIconic`, `IsZoomed`, `IsHungAppWindow` | Validates existence and reports visible/minimized/maximized/unresponsive states. |
| `DwmGetWindowAttribute(DWMWA_CLOAKED)` | Reports windows on other virtual desktops and rejects mutations of cloaked windows. |
| `GetForegroundWindow` | Reports foreground status during metadata refresh. |

`System.Diagnostics.Process` supplies the process creation time and working-set byte count. HWND + PID + start time avoids sending old placements to a restarted process. A generation counter and observed destroy events distinguish HWND reuse within one process lifetime. A Win32 check followed by a Win32 mutation is not atomic; the implementation narrows this inherent race with immediate revalidation and post-checks.

## Geometry and DPI

`EnumDisplayMonitors` and `GetMonitorInfoW` return the physical working rectangle, including negative desktop origins. The manifest declares PerMonitorV2. A hidden manager-owned `STATIC` window is created on each monitor, queried with `GetDpiForWindow`, and destroyed on the same thread. This avoids relying on the legacy `GetDpiForMonitor` API from a PMv2 thread. It does not capture pixels or create a game preview.

Grid cells contain the entire rectangle returned by `GetWindowRect`. This includes invisible resize borders, making the layout conservative. No client-area width is mistaken for an outer-window width. WPF device-independent UI units are never passed to window-positioning calls. [Microsoft: GetWindowRect](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowrect), [GetDpiForWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getdpiforwindow), [GetDpiForMonitor limitations](https://learn.microsoft.com/en-us/windows/win32/api/shellscalingapi/nf-shellscalingapi-getdpiformonitor).

`GetSystemMetricsForDpi` initializes minimum tracking dimensions. `SendMessageTimeoutW(WM_GETMINMAXINFO, SMTO_ABORTIFHUNG | SMTO_ERRORONEXIT, 250ms)` asks the client for its minimum and maximum outer tracking dimensions. A timeout or denial aborts preflight. This is a documented, OS-marshalled system message; there is no custom remote buffer, injected routine or game-memory access. [WM_GETMINMAXINFO](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-getminmaxinfo), [SendMessageTimeoutW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendmessagetimeoutw).

Before a cross-DPI move, minimum sizes scale up conservatively when necessary. They never automatically scale down. This can reject a layout that a particular client would allow; conservative rejection is preferable for V0.1. Roblox may apply additional sizing rules during movement, so preflight is supplemented by exact physical-bound checks.

## Movement, focus and restore

1. Preflight every target and capture every `WINDOWPLACEMENT` before the first change.
2. If minimized or maximized, `ShowWindowAsync(SW_SHOWNOACTIVATE)` requests normal state without taking focus.
3. `SetWindowPos` uses `SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_ASYNCWINDOWPOS`. It changes geometry without changing window styles, parentage or intentional stacking order.
4. Bounded, operation-only checks wait up to 1.8 seconds per stage for actual geometry/state. A final pass checks containment, exact bounds and pairwise overlap for the entire grid. These checks run on the worker, not the WPF dispatcher.
5. A failure rolls back attempted moves in reverse order. Asynchronous window queues cannot provide an atomic multi-window transaction; an unresponsive client can delay requests and defeat rollback. Such failures are visible and retained for manual retry, never reported as successful arrangement.

`SetForegroundWindow` is called only for explicit Focus actions. Windows can deny foreground permission. There is no `AttachThreadInput`, fake keyboard input or focus-stealing workaround. [SetWindowPos flags](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos), [SetForegroundWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow).

`GetWindowPlacement` and `SetWindowPlacement` use their own placement/workspace coordinate convention. Those coordinates are kept together and are never passed directly to `SetWindowPos`, which uses screen coordinates for these top-level windows. `WINDOWPLACEMENT.length` is initialized correctly. The saved normal bounds and minimized/maximized positions are preserved; restoration intentionally replaces flags with `WPF_ASYNCWINDOWPLACEMENT` and allows only maximized or normal-without-activation show commands. A minimized original is **never** re-minimized. Restores are checked against saved normal bounds and expected non-minimized show state. [SetWindowPlacement](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowplacement), [WINDOWPLACEMENT coordinates](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-windowplacement).

## Remote individual-window capture (V0.4)

The mobile feature uses a separate native C++/WinRT bridge. `IGraphicsCaptureItemInterop::CreateForWindow` targets a validated Roblox HWND. The bridge also checks PID, process creation time, executable basename, minimized/cloaked state and responsiveness. `D3D11CreateDevice` creates a BGRA-capable hardware device; `CreateDirect3D11DeviceFromDXGIDevice` provides its WinRT projection. `Direct3D11CaptureFramePool::CreateFreeThreaded` uses one buffer without a UI dispatcher dependency. A session waits at most 800 ms for a frame; device creation/copy/driver work is outside that frame wait budget. `IDirect3DDxgiInterfaceAccess` exposes the captured D3D texture. A temporary staging texture is mapped for CPU downsampling to at most 640 × 360, then WPF's WIC-backed JPEG encoder compresses at quality 65. The frame, session, pool and textures are released after each attempt. Windows may draw a capture border; the app does not bypass that indicator.

Capture is serialized on a worker while an authenticated viewer lease is active. It never uses `PrintWindow`, desktop `BitBlt`, input simulation, injection, window movement or foreground changes. Covered windows can work; minimized/protected/locked-desktop sources may be unavailable or stale. HRESULTs are logged without repeated identical failures every refresh. Identity is revalidated after capture, and ignored/closed entries discard in-flight results. Desktop DWM thumbnail behavior is unchanged.

References: [window capture interop](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createforwindow), [free-threaded frame pools](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframepool.createfreethreaded).

## Monitoring and cleanup

`SetWinEventHook` watches create/destroy/show/hide and minimize-start/end notifications using `WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS`. A rooted managed delegate lives as long as the hook, with a WPF dispatcher supplying the required message loop. Destroy notifications invalidate known identities immediately. Relevant events schedule one refresh per 750ms burst. `UnhookWinEvent` runs on disposal. A ten-second dispatcher timer refreshes process metadata and recovers from missed notifications; no images are captured. [SetWinEventHook](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook).

The manager listens to `WM_DISPLAYCHANGE`, `WM_SETTINGCHANGE` and `WM_DPICHANGED` to request fresh monitor metadata. Restore Grid rejects changed monitor geometry or scaling. The app applies `DwmSetWindowAttribute` only to its own title bar for the dark theme.

## Account profiles and alerts

V0.7 reuses the read-only Restart Manager file-owner match for account IDs and session metadata. Only timestamped engine join/disconnect markers are parsed; game output is excluded. Profile replay checks PID/start time again and restores ignored status before resource plans. Public game/avatar endpoints and cache bounds are documented in [SESSIONS.md](SESSIONS.md).

`Shell_NotifyIconW` adds a manager-owned notification-area icon on the first desktop alert, modifies it to display a silent balloon, and removes it on normal close. Alerts neither activate nor move game windows. Windows decides whether to display the balloon. All events remain available in the bounded Activity history. [Microsoft: Shell_NotifyIconW](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shell_notifyiconw).
