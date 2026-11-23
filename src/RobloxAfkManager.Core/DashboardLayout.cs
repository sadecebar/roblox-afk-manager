namespace RobloxAfkManager.Core;

// Dashboard geometry is in WPF logical units, completely separate from game-window geometry.
public readonly record struct DashboardLayout(int Columns, int Rows, double TileWidth, double TileHeight, double Gap)
{
    public const double HorizontalChrome = 10;
    public const double VerticalChrome = 138;
    public const double DenseHorizontalChrome = 10;
    public const double DenseVerticalChrome = 54;

    public bool IsDense => TileWidth < 240 || TileHeight < 240;

    // Expand a partially occupied last row too; no empty strip remains on its right.
    public (double X, double Y, double Width, double Height) Cell(int index, int count, double width)
    {
        int row = index / Columns;
        int rowCount = Math.Min(Columns, count - row * Columns);
        double cellWidth = Math.Max(0, (width - (rowCount - 1) * Gap) / rowCount);
        return ((index % Columns) * (cellWidth + Gap), row * (TileHeight + Gap), cellWidth, TileHeight);
    }

    public static DashboardLayout Fit(int count, double width, double height, double aspectRatio)
    {
        if (count <= 0 || !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            return new(1, 1, 0, 0, 0);
        double aspect = double.IsFinite(aspectRatio) ? Math.Clamp(aspectRatio, 0.5, 3) : 1.5;
        var best = new DashboardLayout(1, count, 0, 0, 0);
        double bestPreview = -1;
        for (int columns = 1; columns <= count; columns++)
        {
            int rows = (count + columns - 1) / columns;
            double gap = Math.Min(10, Math.Min(width / (columns * 2), height / (rows * 2)));
            double cellWidth = (width - (columns - 1) * gap) / columns;
            double cellHeight = (height - (rows - 1) * gap) / rows;
            // Preserve the compact controls whenever a wider layout already has a usable preview.
            if (cellWidth < 90 && bestPreview > 0) continue;
            bool dense = cellWidth < 240 || cellHeight < 240;
            double chromeWidth = dense ? DenseHorizontalChrome : HorizontalChrome;
            double chromeHeight = dense ? DenseVerticalChrome : VerticalChrome;
            double previewWidth = Math.Max(0,
                Math.Min(cellWidth - chromeWidth, (cellHeight - chromeHeight) * aspect));
            // Avoid narrow columns that cannot hold the compact Show/Ignore controls.
            previewWidth *= Math.Pow(Math.Min(1, cellWidth / 120), 2);
            // Prefer wider cards when image sizes are within eight percent; dense
            // chrome is a fallback for small viewports, not a reward for crowding.
            double tolerance = Math.Max(0.01, bestPreview * 0.08);
            bool tied = Math.Abs(previewWidth - bestPreview) <= tolerance;
            if (previewWidth > bestPreview + tolerance || tied && columns < best.Columns)
            {
                bestPreview = previewWidth;
                // Aspect ratio selects the grid; it must not shrink the grid's occupied area.
                best = new(columns, rows, cellWidth, cellHeight, gap);
            }
        }
        return best;
    }
}
