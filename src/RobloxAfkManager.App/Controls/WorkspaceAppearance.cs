using System.Windows;

namespace RobloxAfkManager.App.Controls;

public static class WorkspaceAppearance
{
    public static readonly DependencyProperty IsDropTargetProperty = DependencyProperty.RegisterAttached(
        "IsDropTarget", typeof(bool), typeof(WorkspaceAppearance), new PropertyMetadata(false));
    public static bool GetIsDropTarget(DependencyObject target) => (bool)target.GetValue(IsDropTargetProperty);
    public static void SetIsDropTarget(DependencyObject target, bool value) => target.SetValue(IsDropTargetProperty, value);
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.RegisterAttached(
        "IsCompact", typeof(bool), typeof(WorkspaceAppearance),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsCompact(DependencyObject target) => (bool)target.GetValue(IsCompactProperty);
    public static void SetIsCompact(DependencyObject target, bool value) => target.SetValue(IsCompactProperty, value);

    // Only short, user-triggered fades. Windows' animation preference also applies here.
    public static Duration FadeDuration => new(TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 140 : 0));
}
