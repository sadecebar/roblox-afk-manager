using RobloxAfkManager.App.Remote;
using RobloxAfkManager.Core;

namespace RobloxAfkManager.App.ViewModels;

public sealed class RemoteMonitoringViewModel : ObservableObject, IDisposable
{
    private readonly MonitoringServer server;
    private readonly IAppLog log;
    private bool busy;
    private string status = "Off. Start monitoring before connecting your phone.";
    public MonitoringFeed Feed { get; }
    public AsyncCommand StartCommand { get; }
    public AsyncCommand StopCommand { get; }
    public bool Running => server.Running;
    public bool Busy { get => busy; private set { Set(ref busy, value); StartCommand.Notify(); StopCommand.Notify(); } }
    public string Status { get => status; private set => Set(ref status, value); }
    public string AccessCode => server.AccessCode;
    public string LocalAddress => server.Address;

    public RemoteMonitoringViewModel(Func<WindowIdentity, bool> isCurrent, IAppLog log)
    {
        this.log = log;
        Feed = new(new WindowScreenshot(isCurrent), log);
        server = new(Feed, log);
        StartCommand = new(StartAsync, () => !Busy && !Running, Error);
        StopCommand = new(StopAsync, () => !Busy && Running, Error);
    }
    public async Task StartAsync()
    {
        if (Busy || Running) return;
        Busy = true;
        try
        {
            await server.StartAsync();
            Status = "On. Previews are captured only while an authenticated dashboard is being viewed.";
        }
        catch (Exception ex) { Error(ex); }
        finally { Changed(); Busy = false; }
    }
    public async Task StopAsync()
    {
        if (Busy) return;
        Busy = true;
        try { await server.DisposeAsync(); Status = "Off. Phone access stopped and preview images cleared."; }
        catch (Exception ex) { Error(ex); }
        finally { Changed(); Busy = false; }
    }
    private void Changed() { Raise(nameof(Running)); Raise(nameof(AccessCode)); Raise(nameof(LocalAddress)); }
    private void Error(Exception ex) { log.Write("Remote monitoring error.", ex); Status = "Monitoring error: " + ex.Message; }
    public void Dispose() => server.DisposeAsync().AsTask().GetAwaiter().GetResult();
}
