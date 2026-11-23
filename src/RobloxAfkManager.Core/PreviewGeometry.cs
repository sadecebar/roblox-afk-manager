namespace RobloxAfkManager.Core;

public readonly record struct PreviewPlacement(PixelRect Destination, PixelRect Source)
{
    public bool IsVisible => Destination.Width > 0 && Destination.Height > 0;
}

// Only thumbnail coordinates. Never changes the source window's size or placement.
public static class PreviewGeometry
{
    public static PreviewPlacement Map(PixelRect viewport, int sourceWidth, int sourceHeight, bool fill, PixelRect visibleClip)
    {
        if (viewport.Width <= 0 || viewport.Height <= 0 || sourceWidth <= 0 || sourceHeight <= 0) return default;
        double sx = (double)viewport.Width / sourceWidth, sy = (double)viewport.Height / sourceHeight;
        double scale = fill ? Math.Max(sx, sy) : Math.Min(sx, sy);
        // Round out for Fill so fractional scaling cannot leave a one-pixel bar.
        int width = Math.Max(1, (int)(fill ? Math.Ceiling(sourceWidth * scale) : Math.Floor(sourceWidth * scale)));
        int height = Math.Max(1, (int)(fill ? Math.Ceiling(sourceHeight * scale) : Math.Floor(sourceHeight * scale)));
        var full = new PixelRect(viewport.X + (viewport.Width - width) / 2, viewport.Y + (viewport.Height - height) / 2, width, height);
        // DWM does not inherit WPF clipping. Limit to the preview itself, not just
        // the outer card, so a zoomed image cannot cover headers or ignore buttons.
        var destination = Intersect(Intersect(full, viewport), visibleClip);
        if (destination.Width <= 0 || destination.Height <= 0) return default;
        int left = Math.Clamp((int)Math.Floor((destination.X - full.X) * (double)sourceWidth / full.Width), 0, sourceWidth - 1);
        int top = Math.Clamp((int)Math.Floor((destination.Y - full.Y) * (double)sourceHeight / full.Height), 0, sourceHeight - 1);
        int right = Math.Clamp((int)Math.Ceiling((destination.Right - full.X) * (double)sourceWidth / full.Width), left + 1, sourceWidth);
        int bottom = Math.Clamp((int)Math.Ceiling((destination.Bottom - full.Y) * (double)sourceHeight / full.Height), top + 1, sourceHeight);
        return new(destination, new(left, top, right - left, bottom - top));
    }

    public static PixelRect Intersect(PixelRect a, PixelRect b)
    {
        int x = Math.Max(a.X, b.X), y = Math.Max(a.Y, b.Y);
        return new(x, y, Math.Max(0, Math.Min(a.Right, b.Right) - x), Math.Max(0, Math.Min(a.Bottom, b.Bottom) - y));
    }
}
