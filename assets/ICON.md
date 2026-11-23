# Application icon

`roblox-afk-manager-icon-source.png` is the original AI-generated transparent artwork. The app uses ICO frames at 16, 24, 32, 48, 64, 128 and 256 pixels. The mobile dashboard uses 192-pixel and 180-pixel PNG exports.

Regenerate the exports on Windows:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/Export-AppIcon.ps1
```

The conversion preserves transparency. The ICO is embedded in the executable and WPF resources; the browser PNGs are embedded web assets. The artwork is an independent client-window grid symbol, not an official Roblox logo.
