using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using RobloxAfkManager.App.Services;
using RobloxAfkManager.Core;
using N = RobloxAfkManager.Platform.Windows.NativeMethods;

namespace RobloxAfkManager.App.Controls;

// DWM renders directly into our top-level window. No copied frames, capture timers,
// game hooks, child-window reparenting, or code inside the source process.
public sealed class LiveWindowPreview : FrameworkElement
{
    public static readonly DependencyProperty IdentityProperty = DependencyProperty.Register(nameof(Identity), typeof(WindowIdentity), typeof(LiveWindowPreview), new PropertyMetadata(default(WindowIdentity), SourceChanged));
    public static readonly DependencyProperty CanPreviewProperty = DependencyProperty.Register(nameof(CanPreview), typeof(bool), typeof(LiveWindowPreview), new PropertyMetadata(true, SourceChanged));
    public static readonly DependencyProperty SourceBoundsProperty = DependencyProperty.Register(nameof(SourceBounds), typeof(PixelRect), typeof(LiveWindowPreview), new PropertyMetadata(default(PixelRect), GeometryChanged));
    public static readonly DependencyProperty IdentityValidatorProperty = DependencyProperty.Register(nameof(IdentityValidator), typeof(Func<WindowIdentity, bool>), typeof(LiveWindowPreview), new PropertyMetadata(null, SourceChanged));
    public static readonly DependencyProperty RefreshRevisionProperty = DependencyProperty.Register(nameof(RefreshRevision), typeof(int), typeof(LiveWindowPreview), new PropertyMetadata(0, RefreshChanged));
    public static readonly DependencyProperty FillPreviewProperty = DependencyProperty.Register(nameof(FillPreview), typeof(bool), typeof(LiveWindowPreview), new PropertyMetadata(true, GeometryChanged));
    public WindowIdentity Identity { get => (WindowIdentity)GetValue(IdentityProperty); set => SetValue(IdentityProperty, value); }
    public bool CanPreview { get => (bool)GetValue(CanPreviewProperty); set => SetValue(CanPreviewProperty, value); }
    public PixelRect SourceBounds { get => (PixelRect)GetValue(SourceBoundsProperty); set => SetValue(SourceBoundsProperty, value); }
    public Func<WindowIdentity, bool>? IdentityValidator { get => (Func<WindowIdentity, bool>?)GetValue(IdentityValidatorProperty); set => SetValue(IdentityValidatorProperty, value); }
    public int RefreshRevision { get => (int)GetValue(RefreshRevisionProperty); set => SetValue(RefreshRevisionProperty, value); }
    public bool FillPreview { get => (bool)GetValue(FillPreviewProperty); set => SetValue(FillPreviewProperty, value); }

    private static readonly FileLog Log = new();
    private nint thumbnail, destination;
    private Window? owner;
    private N.Size sourceSize;
    private (PixelRect Destination, PixelRect Source, bool Visible)? last;
    private bool registrationFailed;
    private string fallback = "Waiting for preview";
    private static readonly Brush PreviewBackground = FrozenBrush(0x05, 0x05, 0x08);
    private static readonly Brush PreviewText = FrozenBrush(0x68, 0x68, 0x78);
    private static Brush FrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b)); brush.Freeze(); return brush;
    }
    public bool HasLiveThumbnail => thumbnail != 0;
    public PixelRect? DisplayedBounds => last is { Visible: true } value ? value.Destination : null;
    public PixelRect? DisplayedSourceBounds => last is { Visible: true } value ? value.Source : null;

    public LiveWindowPreview()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => UpdateThumbnail();
    }

    private static void SourceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var preview = (LiveWindowPreview)sender;
        preview.Release(); preview.registrationFailed = false;
        preview.UpdateThumbnail();
    }
    private static void GeometryChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var preview = (LiveWindowPreview)sender;
        if (preview.thumbnail != 0 && N.DwmQueryThumbnailSourceSize(preview.thumbnail, out preview.sourceSize) != 0) preview.Release();
        preview.UpdateThumbnail();
    }
    private static void RefreshChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var preview = (LiveWindowPreview)sender;
        preview.registrationFailed = false;
        if (preview.IdentityValidator?.Invoke(preview.Identity) != true) preview.Release();
        preview.UpdateThumbnail();
    }
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        owner = Window.GetWindow(this);
        if (owner is not null) { owner.StateChanged += OwnerChanged; owner.Closed += OwnerClosed; }
        LayoutUpdated += LayoutChanged;
        UpdateThumbnail();
    }
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        LayoutUpdated -= LayoutChanged;
        if (owner is not null) { owner.StateChanged -= OwnerChanged; owner.Closed -= OwnerClosed; }
        Release(); owner = null;
    }
    private void OwnerChanged(object? sender, EventArgs e) => UpdateThumbnail();
    private void OwnerClosed(object? sender, EventArgs e) => Release();
    private void LayoutChanged(object? sender, EventArgs e) => UpdateThumbnail();

    private void UpdateThumbnail()
    {
        if (!IsLoaded || owner is null) return;
        if (!CanPreview)
        {
            Release(); SetFallback("Minimized / other desktop\nClick to restore, or use Restore minimized clients"); return;
        }
        if (!IsVisible || owner.WindowState == WindowState.Minimized || ActualWidth < 2 || ActualHeight < 2)
        { Hide(); return; }
        if (thumbnail == 0)
        {
            if (registrationFailed) return;
            if (IdentityValidator?.Invoke(Identity) != true) { SetFallback("Waiting for this client…"); return; }
            destination = new WindowInteropHelper(owner).Handle;
            if (destination == 0) return;
            int hr = N.DwmRegisterThumbnail(destination, Identity.Handle, out thumbnail);
            if (hr != 0) { Fail("Register preview", hr); return; }
            hr = N.DwmQueryThumbnailSourceSize(thumbnail, out sourceSize);
            if (hr != 0 || sourceSize.Width <= 0 || sourceSize.Height <= 0) { Fail("Read preview size", hr); return; }
            // If DWM cannot supply pixels (protected or exclusive rendering), this
            // background remains visible instead of promising a working live feed.
            SetFallback("Preview unavailable from this client\nClick to open the game");
        }
        try
        {
            var surface = ScreenRectangle(this);
            if (!N.GetClientRect(destination, out var client)) { Hide(); return; }
            var clip = PreviewGeometry.Intersect(surface, client.ToPixels());
            // Native thumbnails do not inherit WPF clipping. Explicitly crop both
            // destination and matching source when an ancestor clips its children.
            for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null && parent != owner; parent = VisualTreeHelper.GetParent(parent))
                if (parent is FrameworkElement element && element.ClipToBounds) clip = PreviewGeometry.Intersect(clip, ScreenRectangle(element));
            var placement = PreviewGeometry.Map(surface, sourceSize.Width, sourceSize.Height, FillPreview, clip);
            if (!placement.IsVisible) { Hide(); return; }
            clip = placement.Destination;
            var source = placement.Source;
            if (last == (clip, source, true)) return;
            var properties = new N.ThumbnailProperties
            {
                Flags = 0x1 | 0x2 | 0x4 | 0x8 | 0x10, Destination = N.Rect.From(clip), Source = N.Rect.From(source),
                Opacity = 255, Visible = true, SourceClientAreaOnly = false
            };
            int hr = N.DwmUpdateThumbnailProperties(thumbnail, in properties);
            if (hr != 0) { Fail("Position preview", hr); return; }
            last = (clip, source, true);
        }
        catch (InvalidOperationException) { Hide(); } // Visual may be detaching during a refresh.
    }

    private PixelRect ScreenRectangle(FrameworkElement element)
    {
        var a = element.PointToScreen(new System.Windows.Point(0, 0));
        var b = element.PointToScreen(new System.Windows.Point(element.ActualWidth, element.ActualHeight));
        var origin = new N.Point { X = (int)Math.Ceiling(a.X), Y = (int)Math.Ceiling(a.Y) };
        N.ScreenToClient(destination, ref origin);
        return new(origin.X, origin.Y, Math.Max(0, (int)Math.Floor(b.X) - (int)Math.Ceiling(a.X)), Math.Max(0, (int)Math.Floor(b.Y) - (int)Math.Ceiling(a.Y)));
    }
    private void Hide()
    {
        if (thumbnail == 0 || last is { Visible: false }) return;
        var properties = new N.ThumbnailProperties { Flags = 0x8, Visible = false };
        N.DwmUpdateThumbnailProperties(thumbnail, in properties);
        last = (default, default, false);
    }
    private void Fail(string action, int hr)
    {
        Log.Write($"{action} for PID {Identity.ProcessId}: HRESULT 0x{hr:X8}.");
        Release(); registrationFailed = true; SetFallback("Windows could not display this preview\nClick to open the game");
    }
    private void Release()
    {
        if (thumbnail != 0) N.DwmUnregisterThumbnail(thumbnail);
        thumbnail = 0; last = null;
    }
    private void SetFallback(string text) { if (fallback == text) return; fallback = text; InvalidateVisual(); }
    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(PreviewBackground, null, new Rect(RenderSize));
        if (ActualWidth < 30 || ActualHeight < 25) return;
        var text = new FormattedText(fallback, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11,
            PreviewText, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxTextWidth = Math.Max(1, ActualWidth - 20), MaxTextHeight = Math.Max(1, ActualHeight - 12), TextAlignment = TextAlignment.Center, Trimming = TextTrimming.CharacterEllipsis };
        drawingContext.DrawText(text, new System.Windows.Point(10, Math.Max(6, (ActualHeight - text.Height) / 2)));
    }
}
