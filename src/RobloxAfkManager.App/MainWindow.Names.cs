using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using RobloxAfkManager.App.ViewModels;

namespace RobloxAfkManager.App;

public partial class MainWindow
{
    private void EditClientName_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ClientTileViewModel client } anchor) OpenNameEditor(client, anchor);
        e.Handled = true;
    }

    private void OpenNameEditor(ClientTileViewModel client, FrameworkElement anchor)
    {
        IgnoredPopup.IsOpen = ResourcePopup.IsOpen = MobilePopup.IsOpen = ActivityPopup.IsOpen = false;
        NamePopup.DataContext = client;
        NamePopup.PlacementTarget = anchor;
        ClientNameInput.Text = client.CustomName ?? client.DetectedName ?? "";
        ClientNameError.Text = "";
        NamePopup.IsOpen = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { if (NamePopup.IsOpen) { ClientNameInput.Focus(); ClientNameInput.SelectAll(); } }));
    }

    private void SaveClientName_Click(object sender, RoutedEventArgs e) => SaveClientName(ClientNameInput.Text);
    private void AutomaticClientName_Click(object sender, RoutedEventArgs e) => SaveClientName(null);
    private void SaveClientName(string? name)
    {
        if (NamePopup.DataContext is not ClientTileViewModel client || DataContext is not MainViewModel vm) return;
        var result = vm.SetClientName(client, name);
        if (result.Success) NamePopup.IsOpen = false;
        else ClientNameError.Text = result.Message;
    }
    private void ClientNameInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { SaveClientName(ClientNameInput.Text); e.Handled = true; }
        else if (e.Key == Key.Escape) { NamePopup.IsOpen = false; e.Handled = true; }
    }
}
