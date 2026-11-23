using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using RobloxAfkManager.App;
using RobloxAfkManager.App.ViewModels;

namespace RobloxAfkManager.Tests;

// Opt-in: uses the mouse on a synthetic manager window, then restores its position.
internal static class DashboardDragSmoke
{
    public static void Run()
    {
        var application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (!application.Resources.Contains("CanvasBrush")) application.Resources.MergedDictionaries.Add(
            new ResourceDictionary { Source = new Uri("/RobloxAfkManager;component/Themes/Dark.xaml", UriKind.Relative) });
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        var fake = new FakeWindowSystem(12);
        using var vm = new MainViewModel(fake, new TestLog());
        var area = SystemParameters.WorkArea;
        var host = new MainWindow(vm) { WindowState = WindowState.Normal, Topmost = true,
            Left = area.Left + 16, Top = area.Top + 16, Width = Math.Min(1200, area.Width - 32), Height = Math.Min(800, area.Height - 32) };
        GetCursorPos(out var originalCursor);
        var foreground = GetForegroundWindow();
        try
        {
            host.Show(); host.Activate();
            UiSmoke.PumpUntil(() => !vm.Busy && vm.ClientCount == 12, TimeSpan.FromSeconds(5));
            var list = (ListBox)host.FindName("ClientDashboard");
            host.UpdateLayout();
            var third = vm.VisibleClients[2]; var eleventh = vm.VisibleClients[10];
            // Editing a label is a separate action, and must not focus or drag a game.
            Click(Center(FindCardPart(list, eleventh, "EditClientNameButton")));
            var namePopup = (System.Windows.Controls.Primitives.Popup)host.FindName("NamePopup");
            UiSmoke.PumpUntil(() => namePopup.IsOpen, TimeSpan.FromSeconds(3));
            ((TextBox)host.FindName("ClientNameInput")).Text = "FixtureRanger";
            ((Button)host.FindName("SaveClientNameButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            UiSmoke.Pump();
            Program.Check(!namePopup.IsOpen && eleventh.Label == "FixtureRanger" && fake.Focused.Count == 0, "Name editing changed client focus or did not save");
            Drag(Center(FindCardPart(list, eleventh, "ClientDragHandle")), Center(FindCardPart(list, third, "ClientDragHandle")));
            Program.Check(ReferenceEquals(vm.VisibleClients[2], eleventh) && ReferenceEquals(vm.VisibleClients[10], third), "Mouse drag did not swap clients 11 and 3");
            UiSmoke.Pump();
            // Dragging a clickable preview must swap without firing its focus command.
            var preview = NativeSmoke.Descendants((DependencyObject)list.ItemContainerGenerator.ContainerFromItem(eleventh)).OfType<Button>()
                .Single(b => b.Style == host.FindResource("PreviewButton"));
            Drag(Center(preview), Center(FindCardPart(list, third, "ClientDragHandle")));
            Program.Check(ReferenceEquals(vm.VisibleClients[2], third) && ReferenceEquals(vm.VisibleClients[10], eleventh), "Preview drag did not swap back");
            Program.Check(fake.Focused.Count == 0 && fake.Moved.Count == 0 && fake.Restored.Count == 0, "Dragging invoked a game-window action");
            // A regular preview click still opens the correct client after the drag.
            preview = NativeSmoke.Descendants((DependencyObject)list.ItemContainerGenerator.ContainerFromItem(eleventh)).OfType<Button>()
                .Single(b => b.Style == host.FindResource("PreviewButton"));
            Click(Center(preview));
            UiSmoke.PumpUntil(() => !vm.Busy && fake.Focused.Count == 1, TimeSpan.FromSeconds(5));
            Program.Check(fake.Focused.Single() == eleventh.Identity, "Preview click focused a different client after reordering");
            Console.WriteLine("Pointer interaction: account-name editor, header swap, preview swap without focus, and normal preview click passed on synthetic clients.");
        }
        finally
        {
            // Best-effort release must not prevent closing the test host if desktop input is unavailable.
            _ = SendInput(1, [new Input { Mouse = new MouseInput { Flags = 4 } }], Marshal.SizeOf<Input>());
            host.Close(); UiSmoke.Pump();
            SetCursorPos(originalCursor.X, originalCursor.Y);
            if (foreground != 0) SetForegroundWindow(foreground);
        }
    }

    private static FrameworkElement FindCardPart(ListBox list, ClientTileViewModel tile, string name) =>
        NativeSmoke.Descendants((DependencyObject)list.ItemContainerGenerator.ContainerFromItem(tile)).OfType<FrameworkElement>().Single(e => e.Name == name);
    private static Point Center(FrameworkElement element) => element.PointToScreen(new(element.ActualWidth / 2, element.ActualHeight / 2));
    private static void Move(Point point) { if (!SetCursorPos((int)Math.Round(point.X), (int)Math.Round(point.Y))) throw new InvalidOperationException("Cannot move test pointer"); }
    private static void Drag(Point from, Point to)
    {
        var task = Task.Run(() =>
        {
            try
            {
                Move(from); Thread.Sleep(120); SendMouse(2); Thread.Sleep(120);
                for (int step = 1; step <= 8; step++) { Move(from + (to - from) * (step / 8d)); Thread.Sleep(60); }
                Thread.Sleep(160);
            }
            finally { SendMouse(4); }
        });
        UiSmoke.PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(8)); task.GetAwaiter().GetResult();
        UiSmoke.Pump();
    }
    private static void Click(Point point)
    {
        var task = Task.Run(() => { Move(point); Thread.Sleep(100); SendMouse(2); Thread.Sleep(80); SendMouse(4); });
        UiSmoke.PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(5)); task.GetAwaiter().GetResult(); UiSmoke.Pump();
    }
    private static void SendMouse(uint flags)
    {
        var inputs = new[] { new Input { Mouse = new MouseInput { Flags = flags } } };
        if (SendInput(1, inputs, Marshal.SizeOf<Input>()) != 1)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Mouse test input was rejected");
    }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint hwnd);
}
