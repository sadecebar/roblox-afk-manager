namespace RobloxAfkManager.Core;

// All geometry is in physical screen pixels, never WPF device-independent units.
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => checked(X + Width);
    public int Bottom => checked(Y + Height);
    public bool Contains(PixelRect other) => other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;
    public bool Intersects(PixelRect other) => X < other.Right && Right > other.X && Y < other.Bottom && Bottom > other.Y;
    public override string ToString() => $"{X}, {Y}  /  {Width} × {Height} px";
}
public readonly record struct WindowIdentity(nint Handle, int ProcessId, long StartTimeUtcTicks, long Generation = 0);
public sealed record ClientWindow(WindowIdentity Identity, string Title, PixelRect Bounds, long? MemoryBytes, string Status)
{
    public double? CpuUsagePercent { get; init; }
}
public sealed record MonitorInfo(string DeviceName, PixelRect WorkArea, uint Dpi, bool IsPrimary)
{
    public string Label => $"{DeviceName.Replace(@"\\.\", "")} {(IsPrimary ? "(primary)" : "")}  ·  {WorkArea.Width} × {WorkArea.Height}";
    public string Details => $"{Dpi * 100 / 96}% scaling · work area {WorkArea.X}, {WorkArea.Y}";
}
public readonly record struct WindowLimits(int MinWidth, int MinHeight, int MaxWidth = int.MaxValue, int MaxHeight = int.MaxValue);
public sealed record Placement(uint Flags, uint ShowCommand, int MinX, int MinY, int MaxX, int MaxY, PixelRect NormalBounds);
public sealed record LayoutSpec(int Columns, int Rows, int Gap = 6)
{
    public override string ToString() => $"{Columns} × {Rows}";
}
public sealed record WindowTarget(WindowIdentity Identity, PixelRect Bounds);
public sealed record GridPlan(string MonitorDevice, PixelRect WorkArea, uint Dpi, LayoutSpec Layout, IReadOnlyList<WindowTarget> Targets);
public sealed record OperationResult(bool Success, string Message);

public interface IWindowSystem
{
    IReadOnlyList<ClientWindow> Detect();
    IReadOnlyList<MonitorInfo> GetMonitors();
    bool IsCurrent(WindowIdentity identity);
    WindowLimits GetLimits(WindowIdentity identity, uint targetDpi);
    Placement Capture(WindowIdentity identity);
    void MoveNormal(WindowIdentity identity, PixelRect bounds);
    PixelRect ReadBounds(WindowIdentity identity);
    bool IsNormal(WindowIdentity identity);
    void Restore(WindowIdentity identity, Placement placement);
    bool Focus(WindowIdentity identity);
    void RestoreForPreview(WindowIdentity identity);
}
public interface IAppLog { void Write(string message, Exception? exception = null); }
