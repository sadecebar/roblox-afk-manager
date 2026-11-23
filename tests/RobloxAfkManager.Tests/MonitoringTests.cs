using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using RobloxAfkManager.App.Remote;
using RobloxAfkManager.App.ViewModels;
using RobloxAfkManager.Core;

namespace RobloxAfkManager.Tests;

internal static class MonitoringTests
{
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(int seconds) => now += TimeSpan.FromSeconds(seconds);
    }
    internal sealed class FakeCapture : IWindowScreenshot
    {
        public readonly List<WindowIdentity> Calls = [];
        public bool Fail;
        public int Size = 100;
        public Action? DuringCapture;
        public byte[] Capture(WindowIdentity id)
        {
            Calls.Add(id); DuringCapture?.Invoke();
            if (Fail) throw new InvalidOperationException("Simulated capture error");
            return new byte[Size];
        }
    }
    private static MonitoredClient Client(int pid = 1, long start = 10, string status = "Open") => new(new(new(pid, pid, start), "Roblox", new(0, 0, 800, 600), 1200000000, status), "Client " + pid);

    public static void Register(Action<string, Action> test)
    {
        test("Remote capture is viewer-driven, throttled, and clears images after disconnect", () =>
        {
            var clock = new Clock(); var capture = new FakeCapture(); var feed = new MonitoringFeed(capture, new TestLog(), clock);
            feed.Publish([Client()], []); feed.CaptureDue(); Program.Check(capture.Calls.Count == 0);
            var id = feed.Read().Clients.Single().Id; feed.CaptureDue(); Program.Check(capture.Calls.Count == 1 && feed.Image(id)?.Length == 100);
            clock.Advance(7); feed.CaptureDue(); Program.Check(capture.Calls.Count == 1);
            clock.Advance(1); feed.CaptureDue(); Program.Check(capture.Calls.Count == 2);
            clock.Advance(13); feed.CaptureDue(); Program.Check(capture.Calls.Count == 2 && feed.Image(id) is null);
            feed.Read(); feed.CaptureDue(); Program.Check(capture.Calls.Count == 3);
            feed.ResetViewers(); feed.CaptureDue(); Program.Check(capture.Calls.Count == 3 && feed.Image(id) is null);
        });
        test("Remote ignore purges screenshots and refuses in-flight completion", () =>
        {
            var capture = new FakeCapture(); var feed = new MonitoringFeed(capture, new TestLog()); var client = Client();
            feed.Publish([client], []); var id = feed.Read().Clients.Single().Id;
            capture.DuringCapture = () => feed.Publish([], [client.Window.Identity]);
            feed.CaptureDue(); Program.Check(feed.Read(false).Clients.Count == 0 && feed.Image(id) is null);
            using var vm = new MainViewModel(new FakeWindowSystem(2), new TestLog()); vm.RefreshAsync().GetAwaiter().GetResult();
            Program.Check(vm.Remote.Feed.Read(false).Clients.Count == 2 && !vm.Remote.Running);
            vm.Clients[0].IsIgnored = true; Program.Check(vm.Remote.Feed.Read(false).Clients.Count == 1);
            feed.Publish([client], []); feed.Read(); capture.DuringCapture = null; feed.CaptureDue();
            feed.Publish([], [client.Window.Identity with { Handle = 999 }]);
            Program.Check(feed.Read(false).Clients.Count == 0, "Ignoring a process left a prior HWND image visible");
        });
        test("Remote closed records expire and reused PIDs receive separate image identities", () =>
        {
            var clock = new Clock(); var feed = new MonitoringFeed(new FakeCapture(), new TestLog(), clock);
            feed.Publish([Client()], []); var original = feed.Read().Clients.Single().Id; feed.CaptureDue();
            feed.Publish([Client(1, 11)], []);
            var state = feed.Read(); Program.Check(state.Clients.Count == 2 && state.Clients.Single(c => c.Id == original).Running is null);
            Program.Check(state.Clients.Single(c => c.Running == true).ScreenshotAt is null);
            clock.Advance(61); feed.Publish([Client(1, 11)], []); Program.Check(feed.Read(false).Clients.Count == 1 && feed.Image(original) is null);
        });
        test("Unavailable windows are not captured and capture failures keep honest timestamps", () =>
        {
            var clock = new Clock(); var capture = new FakeCapture(); var feed = new MonitoringFeed(capture, new TestLog(), clock);
            feed.Publish([Client(), Client(2, status: "Minimized externally"), Client(3, status: "Not responding")], []);
            feed.Read(); feed.CaptureDue(); Program.Check(capture.Calls.Count == 1);
            var timestamp = feed.Read(false).Clients[0].ScreenshotAt;
            clock.Advance(8); capture.Fail = true; feed.CaptureDue();
            var failed = feed.Read(false).Clients[0]; Program.Check(failed.ScreenshotAt == timestamp && failed.PreviewStatus.StartsWith("Preview unavailable"));
            feed.CaptureDue(); Program.Check(capture.Calls.Count == 2);
        });
        test("Remote image cache and client count stay bounded", () =>
        {
            var capture = new FakeCapture { Size = 524288 }; var feed = new MonitoringFeed(capture, new TestLog());
            feed.Publish(Enumerable.Range(1, 200).Select(i => Client(i)), []);
            var state = feed.Read(); Program.Check(state.Clients.Count == 128); feed.CaptureDue();
            Program.Check(state.Clients.Sum(c => (long)(feed.Image(c.Id)?.Length ?? 0)) <= 8 * 1048576);
        });
    }

    public static void Http() => VerifyHttpAsync().GetAwaiter().GetResult();
    private static async Task VerifyHttpAsync()
    {
        var capture = new FakeCapture(); var feed = new MonitoringFeed(capture, new TestLog()); feed.Publish([Client()], []);
        await using var server = new MonitoringServer(feed, new TestLog());
        await server.StartAsync(0);
        Program.Check(new Uri(server.Address).Host == "127.0.0.1");
        using var http = new HttpClient { BaseAddress = new Uri(server.Address), Timeout = TimeSpan.FromSeconds(5) };
        using (var shell = await http.GetAsync("/"))
        {
            Program.Check(shell.StatusCode == HttpStatusCode.OK);
            Program.Check((await shell.Content.ReadAsStringAsync()).Contains("Dashboard access code"));
            Program.Check(shell.Headers.GetValues("Content-Security-Policy").Single().Contains("frame-ancestors 'none'"));
        }
        Program.Check((await http.GetAsync("/api/clients")).StatusCode == HttpStatusCode.Unauthorized);
        Program.Check((await http.GetAsync("/api/preview/unknown")).StatusCode == HttpStatusCode.Unauthorized);
        Program.Check((await http.GetAsync("/api/clients?code=" + server.AccessCode)).StatusCode == HttpStatusCode.Unauthorized);
        Program.Check(capture.Calls.Count == 0, "Anonymous requests started capturing");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
        Program.Check((await http.GetAsync("/api/clients")).StatusCode == HttpStatusCode.Unauthorized);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", server.AccessCode);
        var state = await http.GetFromJsonAsync<RemoteSnapshot>("/api/clients"); Program.Check(state!.Clients.Count == 1);
        Program.Check((await http.PostAsync("/api/focus", null)).StatusCode == HttpStatusCode.MethodNotAllowed);
        Program.Check((await http.GetAsync("/api/focus")).StatusCode == HttpStatusCode.NotFound);
        using (var forged = new HttpRequestMessage(HttpMethod.Get, "/api/clients"))
        {
            forged.Headers.Host = "attacker.example";
            Program.Check((await http.SendAsync(forged)).StatusCode == HttpStatusCode.BadRequest);
        }
        using (var cross = new HttpRequestMessage(HttpMethod.Get, "/api/clients"))
        {
            cross.Headers.Add("Origin", "https://attacker.example");
            Program.Check((await http.SendAsync(cross)).StatusCode == HttpStatusCode.Forbidden);
        }
        for (int i = 0; i < 30 && feed.Image(state.Clients[0].Id) is null; i++) await Task.Delay(100);
        using (var image = await http.GetAsync("/api/preview/" + state.Clients[0].Id))
        {
            Program.Check(image.StatusCode == HttpStatusCode.OK && image.Content.Headers.ContentType?.MediaType == "image/jpeg");
            Program.Check(image.Headers.CacheControl?.NoStore == true);
        }
        feed.Publish([], [Client().Window.Identity]);
        Program.Check((await http.GetAsync("/api/preview/" + state.Clients[0].Id)).StatusCode == HttpStatusCode.NotFound);
        var oldCode = server.AccessCode; await server.DisposeAsync(); Program.Check(!server.Running && server.AccessCode == "");
        await server.StartAsync(0); Program.Check(oldCode != server.AccessCode);
        using var restarted = new HttpClient { BaseAddress = new Uri(server.Address) };
        restarted.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", oldCode);
        Program.Check((await restarted.GetAsync("/api/clients")).StatusCode == HttpStatusCode.Unauthorized);
    }
}
