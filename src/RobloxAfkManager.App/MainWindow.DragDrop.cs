using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using RobloxAfkManager.App.Controls;
using RobloxAfkManager.App.ViewModels;

namespace RobloxAfkManager.App;

public partial class MainWindow
{
    private const string ClientDragFormat = "RobloxAfkManager.ClientTile";
    private Point dragOrigin;
    private ClientTileViewModel? pendingDrag, draggedClient;
    private ListBoxItem? dropTarget;

    private void Dashboard_MouseDown(object sender, MouseButtonEventArgs e)
    {
        pendingDrag = null;
        if (DataContext is not MainViewModel { IsIdle: true }) return;
        var source = e.OriginalSource as DependencyObject;
        // Preview clicks still focus normally. Action buttons never start a drag.
        var button = Ancestor<ButtonBase>(source);
        if (button is not null && button.Style != FindResource("PreviewButton")) return;
        if (ItemsControl.ContainerFromElement(ClientDashboard, source) is not ListBoxItem { DataContext: ClientTileViewModel tile }) return;
        pendingDrag = tile;
        dragOrigin = e.GetPosition(ClientDashboard);
    }

    private void Dashboard_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { pendingDrag = null; return; }
        if (pendingDrag is null || DataContext is not MainViewModel { IsIdle: true }) return;
        var distance = e.GetPosition(ClientDashboard) - dragOrigin;
        if (Math.Abs(distance.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(distance.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        draggedClient = pendingDrag;
        pendingDrag = null;
        // Release the preview button before drag/drop so its eventual mouse-up cannot focus a game.
        Mouse.Capture(null);
        e.Handled = true;
        try { DragDrop.DoDragDrop(ClientDashboard, new DataObject(ClientDragFormat, "swap"), DragDropEffects.Move); }
        finally { draggedClient = null; MarkDropTarget(null); }
    }

    private void Dashboard_MouseUp(object sender, MouseButtonEventArgs e) => pendingDrag = null;

    private ListBoxItem? DropCandidate(DragEventArgs e)
    {
        if (draggedClient is null || !e.Data.GetDataPresent(ClientDragFormat) || DataContext is not MainViewModel { IsIdle: true } vm ||
            !vm.VisibleClients.Contains(draggedClient)) return null;
        var hit = ClientDashboard.InputHitTest(e.GetPosition(ClientDashboard)) as DependencyObject;
        return ItemsControl.ContainerFromElement(ClientDashboard, hit) is ListBoxItem { DataContext: ClientTileViewModel tile } candidate &&
            !ReferenceEquals(tile, draggedClient) && vm.VisibleClients.Contains(tile) ? candidate : null;
    }

    private void Dashboard_DragOver(object sender, DragEventArgs e)
    {
        var candidate = DropCandidate(e);
        MarkDropTarget(candidate);
        e.Effects = candidate is null ? DragDropEffects.None : DragDropEffects.Move;
        e.Handled = true;
    }

    private void Dashboard_DragLeave(object sender, DragEventArgs e) => MarkDropTarget(null);

    private void Dashboard_Drop(object sender, DragEventArgs e)
    {
        var candidate = DropCandidate(e);
        e.Effects = DragDropEffects.None;
        if (draggedClient is { } source && candidate?.DataContext is ClientTileViewModel target && DataContext is MainViewModel vm && vm.SwapClients(source, target))
        {
            vm.SelectedClient = source;
            e.Effects = DragDropEffects.Move;
        }
        MarkDropTarget(null);
        e.Handled = true;
    }

    private void MarkDropTarget(ListBoxItem? candidate)
    {
        if (ReferenceEquals(dropTarget, candidate)) return;
        if (dropTarget is not null) WorkspaceAppearance.SetIsDropTarget(dropTarget, false);
        dropTarget = candidate;
        if (dropTarget is not null) WorkspaceAppearance.SetIsDropTarget(dropTarget, true);
    }

    private void Dashboard_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2 && ClientDashboard.SelectedItem is ClientTileViewModel named &&
            ClientDashboard.ItemContainerGenerator.ContainerFromItem(named) is FrameworkElement anchor)
        { OpenNameEditor(named, anchor); e.Handled = true; return; }
        if (Keyboard.Modifiers != ModifierKeys.Control || e.Key is not (Key.Left or Key.Right) || DataContext is not MainViewModel vm ||
            ClientDashboard.SelectedItem is not ClientTileViewModel source) return;
        int targetIndex = vm.VisibleClients.IndexOf(source) + (e.Key == Key.Left ? -1 : 1);
        if (targetIndex >= 0 && targetIndex < vm.VisibleClients.Count && vm.SwapClients(source, vm.VisibleClients[targetIndex]))
        {
            vm.SelectedClient = source;
            if (ClientDashboard.ItemContainerGenerator.ContainerFromItem(source) is ListBoxItem item) item.Focus();
        }
        e.Handled = true;
    }

    private static T? Ancestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match) return match;
            element = element is FrameworkContentElement content ? content.Parent : VisualTreeHelper.GetParent(element);
        }
        return null;
    }
}
