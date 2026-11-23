using System.Windows;
using System.Windows.Controls;
using RobloxAfkManager.Core;

namespace RobloxAfkManager.App.Controls;

// Packs aspect-aware tiles inside the viewport. There is deliberately no scrolling extent.
public sealed class CompactDashboardPanel : Panel
{
    public static readonly DependencyProperty PreviewAspectRatioProperty = DependencyProperty.Register(nameof(PreviewAspectRatio), typeof(double), typeof(CompactDashboardPanel),
        new FrameworkPropertyMetadata(1.5, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty IsDenseProperty = DependencyProperty.RegisterAttached("IsDense", typeof(bool), typeof(CompactDashboardPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public double PreviewAspectRatio { get => (double)GetValue(PreviewAspectRatioProperty); set => SetValue(PreviewAspectRatioProperty, value); }
    public static bool GetIsDense(DependencyObject target) => (bool)target.GetValue(IsDenseProperty);
    public static void SetIsDense(DependencyObject target, bool value) => target.SetValue(IsDenseProperty, value);

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : 1200;
        double height = double.IsFinite(availableSize.Height) ? availableSize.Height : 700;
        var layout = DashboardLayout.Fit(InternalChildren.Count, width, height, PreviewAspectRatio);
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var cell = layout.Cell(i, InternalChildren.Count, width);
            SetIsDense(InternalChildren[i], layout.IsDense);
            InternalChildren[i].Measure(new Size(cell.Width, cell.Height));
        }
        return new Size(width, height);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        var layout = DashboardLayout.Fit(InternalChildren.Count, finalSize.Width, finalSize.Height, PreviewAspectRatio);
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var cell = layout.Cell(i, InternalChildren.Count, finalSize.Width);
            InternalChildren[i].Arrange(new Rect(cell.X, cell.Y, cell.Width, cell.Height));
        }
        return finalSize;
    }
}
