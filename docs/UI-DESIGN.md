# Desktop UI conventions

The desktop is a preview workspace. Game content occupies most of the window; settings open on demand rather than taking a permanent sidebar. The dashboard never scrolls.

## Visual system

- "Night shift" palette (defined in `src/RobloxAfkManager.App/Themes/Dark.xaml`): canvas `#07070A`, cards `#101016`, borders `#22222C`, text `#EEEEF4`, muted `#9A9AAC`. One violet accent `#9D8CFF` for brand, primary actions, selection and focus.
- Status tones (good, pending, warn, alert) drive the card status pill, the avatar status dot and the card border. A card that needs attention gets a red-tinted border and background, so problems can be spotted across the room. Text always accompanies the color.
- The header shows a fleet health summary (all clear / N need attention, joined count, combined RAM). On narrow windows (<980 px), the summary and toolbar labels collapse to icons with tooltips, and total RAM moves to the status bar.
- Segoe UI Variable Text/Display, with Cascadia Mono for slot numbers and codes. Tabular numerals for aligned data.
- Ten-pixel grid gaps, sixteen-pixel workspace edges and a four-pixel preview inset. Cards place the live preview above the identity row (avatar with status dot, name, game, duration, slot), then the status pill and plan chip, CPU/RAM, and the Show/Ignore actions.
- Dense tiles keep only a status dot, the name and the actions; full details stay available in tooltips.
- Short, user-triggered hover/popup transitions respect Windows animation settings. Do not add idle animation or animate native thumbnail transforms.

## Layout and interaction

`DashboardLayout` chooses rows/columns from the available viewport and source proportions, accounting for both full and compact card metadata. Cells fill both dimensions, including a partial final row. Near-equivalent preview sizes favor fewer, wider columns for readable controls; very narrow columns are penalized. Twelve cards may use six columns on a wide desktop and four on a smaller window.

Fill uses an aspect-preserving center crop to cover a preview. Fit shows the full source with letterboxing. Clip native thumbnails to the preview rectangle so they cannot paint over client names or controls. Never move or resize a game to change dashboard layout.

Keep Ignore separate from the focus target. A normal preview click or Show action focuses the client. Dragging a preview or title swaps its position with the highlighted target and suppresses the click. Ctrl+Left/Right provides a keyboard alternative. Reordering changes only the dashboard; labels stay with their processes, and the session's ordering survives refresh and window recreation. Hidden clients stay excluded from bulk operations.

Preserve visible keyboard focus, explicit errors, useful empty states and text alongside status colors. Settings popups use their own native window so DWM images do not cover them. CPU readings share the existing discovery interval, with no additional timer or frame capture.

The card title shows a custom account label, then a detected username, then its stable Client number as the fallback. The pencil button and F2 open a small name editor; Enter saves, Escape cancels, and automatic naming can be restored. Long names are ellipsized with the complete name in a tooltip. Editing is separate from the title/preview drag target and never focuses the game. The mobile dashboard and ignored list use the same label.

The application icon source and export instructions are in [assets/ICON.md](../assets/ICON.md). The mobile interface has its own stylesheet under `src/RobloxAfkManager.App/Remote/Web/`.

V0.7 uses a compact two-line identity area: a 30-pixel public avatar, account title, game name and a separate duration column that survives long-name truncation. Dense cards hide this secondary line and avatar, retaining all details in their tooltip. The existing status row distinguishes game-log evidence from window state. Activity opens from the secondary toolbar into a bounded history popup; it does not consume permanent preview space. Identified account order is saved across restarts. Pending identity matches are withheld when needed to protect saved ignore choices.
