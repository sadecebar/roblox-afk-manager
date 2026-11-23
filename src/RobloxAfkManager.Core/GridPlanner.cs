namespace RobloxAfkManager.Core;

public static class GridPlanner
{
    public static void Validate(LayoutSpec layout, int count)
    {
        if (layout.Columns is < 1 or > 32 || layout.Rows is < 1 or > 32 || layout.Gap is < 0 or > 100)
            throw new InvalidOperationException("Use 1–32 columns and rows, with a gap of 0–100 pixels.");
        if (count == 0) throw new InvalidOperationException("No included Roblox windows. Detect clients or uncheck Ignore this client on a tile.");
        if (count > layout.Columns * layout.Rows)
            throw new InvalidOperationException($"This layout holds {layout.Columns * layout.Rows} clients; {count} are included. Ignore your active client, choose a larger layout, or use the live dashboard without arranging.");
    }

    public static IReadOnlyList<PixelRect> Plan(PixelRect area, LayoutSpec layout, IReadOnlyList<WindowLimits> limits)
    {
        Validate(layout, limits.Count);
        int width = area.Width - (layout.Columns - 1) * layout.Gap;
        int height = area.Height - (layout.Rows - 1) * layout.Gap;
        if (width < layout.Columns || height < layout.Rows)
            throw new InvalidOperationException("The working area is too small for this layout.");
        var result = new List<PixelRect>();
        for (int i = 0; i < limits.Count; i++)
        {
            int col = i % layout.Columns, row = i / layout.Columns;
            int x = col * width / layout.Columns, y = row * height / layout.Rows;
            int w = (col + 1) * width / layout.Columns - x;
            int h = (row + 1) * height / layout.Rows - y;
            var limit = limits[i];
            if (w < limit.MinWidth || h < limit.MinHeight || w > limit.MaxWidth || h > limit.MaxHeight)
                throw new InvalidOperationException($"Layout cannot fit included client {i + 1}: cell {w} × {h} px; minimum outer size {limit.MinWidth} × {limit.MinHeight} px (maximum {limit.MaxWidth} × {limit.MaxHeight}). Use the live dashboard without arranging, or choose fewer rows/columns on a larger monitor. No windows moved.");
            result.Add(new(area.X + x + col * layout.Gap, area.Y + y + row * layout.Gap, w, h));
        }
        return result;
    }
}
