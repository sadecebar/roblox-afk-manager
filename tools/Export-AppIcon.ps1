$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$projectRoot = Split-Path -Parent $PSScriptRoot
$sourcePath = Join-Path $projectRoot 'assets/roblox-afk-manager-icon-source.png'
$iconPath = Join-Path $projectRoot 'src/RobloxAfkManager.App/Assets/RobloxAfkManager.ico'
$webPath = Join-Path $projectRoot 'src/RobloxAfkManager.App/Remote/Web'
$source = [System.Drawing.Bitmap]::FromFile($sourcePath)
try {
    if ($source.Width -ne $source.Height -or $source.GetPixel(0, 0).A -ne 0) { throw 'Expected square artwork with transparent corners.' }
    function Export-IconFrame([int]$Size) {
        $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $stream = [System.IO.MemoryStream]::new()
        try {
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.DrawImage($source, [System.Drawing.Rectangle]::new(0, 0, $Size, $Size))
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            return ,$stream.ToArray()
        } finally { $graphics.Dispose(); $bitmap.Dispose(); $stream.Dispose() }
    }
    $sizes = @(16, 24, 32, 48, 64, 128, 256)
    $frames = @($sizes | ForEach-Object { ,(Export-IconFrame $_) })
    $file = [System.IO.File]::Create($iconPath)
    $writer = [System.IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
            $offset += $frames[$i].Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    } finally { $writer.Dispose(); $file.Dispose() }
    [System.IO.File]::WriteAllBytes((Join-Path $webPath 'app-icon.png'), (Export-IconFrame 192))
    [System.IO.File]::WriteAllBytes((Join-Path $webPath 'apple-touch-icon.png'), (Export-IconFrame 180))
    Write-Output "Created ICO sizes $($sizes -join ', ') and mobile PNGs from $($source.Width)x$($source.Height) transparent artwork."
} finally { $source.Dispose() }
