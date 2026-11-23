using System.IO;
using System.Text.Json;
using RobloxAfkManager.App.Remote;
using RobloxAfkManager.Core;

namespace RobloxAfkManager.Tests;

internal static class RemoteBrowserFixture
{
    private sealed class Screenshot(byte[] jpeg) : IWindowScreenshot
    {
        public byte[] Capture(WindowIdentity identity) => jpeg;
    }
    public static async Task RunAsync()
    {
        // This fixture exposes only invented metadata and an owned synthetic-window image.
        var image = await File.ReadAllBytesAsync("artifacts/remote-synthetic-preview.jpg");
        var feed = new MonitoringFeed(new Screenshot(image), new TestLog());
        MonitoredClient[] clients = Enumerable.Range(1, 16).Select(i => new MonitoredClient(
            new(new(i, 50000 + i, i), "Synthetic", new(0, 0, 800, 600), (1000L + i * 40) * 1048576, "Open"), "Client " + i.ToString("00"),
            "Fixture <game> & friends", i == 2 ? "Disconnected (log)" : "Joined (log)", i == 2 ? null : DateTimeOffset.UtcNow.AddMinutes(-75))).ToArray();
        feed.Publish(clients, []);
        await using var server = new MonitoringServer(feed, new TestLog());
        await server.StartAsync(0);
        await File.WriteAllTextAsync("artifacts/remote-browser-fixture.json", JsonSerializer.Serialize(new { server.Address, server.AccessCode }));
        // A test-only file tells this isolated fixture to exit; it is not an application route.
        for (int i = 0; i < 180 && !File.Exists("artifacts/remote-browser-stop"); i++)
        {
            feed.Publish(clients, []); await Task.Delay(1000);
        }
    }
}
