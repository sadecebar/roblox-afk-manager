using RobloxAfkManager.Core;

namespace RobloxAfkManager.App.Remote;

public sealed record MonitoredClient(ClientWindow Window, string Label, string? GameName = null, string? SessionStatus = null, DateTimeOffset? JoinedAt = null);
public sealed record RemoteClient(string Id, string Label, int ProcessId, bool? Running, bool? Responding,
    string Status, long? MemoryBytes, DateTimeOffset? ScreenshotAt, string PreviewStatus,
    string? GameName = null, string? SessionStatus = null, DateTimeOffset? JoinedAt = null);
public sealed record RemoteSnapshot(DateTimeOffset ServerTime, DateTimeOffset? DetectedAt, IReadOnlyList<RemoteClient> Clients);

// Only immutable DTOs/JPEGs cross into HTTP handlers. No web route can invoke the window manager.
public sealed class MonitoringFeed(IWindowScreenshot capture, IAppLog log, TimeProvider? clock = null)
{
    private sealed class Entry(MonitoredClient source, DateTimeOffset now)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public MonitoredClient Source = source;
        public DateTimeOffset Seen = now;
        public bool Running = true;
        public DateTimeOffset? Attempt, Captured;
        public byte[]? Jpeg;
        public string PreviewStatus = "Waiting for preview";
    }
    private readonly object gate = new();
    private readonly Dictionary<WindowIdentity, Entry> entries = [];
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private DateTimeOffset lastViewer = DateTimeOffset.MinValue;
    private DateTimeOffset? detectedAt;
    private const int MaximumEntries = 128;
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan ViewerLease = TimeSpan.FromSeconds(20);

    public void Publish(IEnumerable<MonitoredClient> included, IEnumerable<WindowIdentity> ignored, DateTimeOffset? detectionTime = null)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            detectedAt = detectionTime ?? now;
            var sources = included.Take(MaximumEntries).ToArray();
            var current = sources.Select(s => s.Window.Identity).ToHashSet();
            var ignoredProcesses = ignored.Select(ProcessIdentity.From).ToHashSet();
            foreach (var identity in entries.Keys.Where(id => ignoredProcesses.Contains(ProcessIdentity.From(id))).ToArray()) entries.Remove(identity);
            foreach (var pair in entries.ToArray())
            {
                if (!current.Contains(pair.Key))
                {
                    pair.Value.Running = false;
                    pair.Value.PreviewStatus = "Window closed or no longer detected";
                    if (now - pair.Value.Seen > TimeSpan.FromMinutes(1)) entries.Remove(pair.Key);
                }
            }
            foreach (var source in sources)
            {
                if (!entries.TryGetValue(source.Window.Identity, out var entry))
                {
                    // Active clients take precedence over bounded, recent closed records.
                    if (entries.Count >= MaximumEntries)
                    {
                        var oldest = entries.Where(p => !p.Value.Running).OrderBy(p => p.Value.Seen).FirstOrDefault();
                        if (oldest.Value is null) continue;
                        entries.Remove(oldest.Key);
                    }
                    entries[source.Window.Identity] = entry = new(source, now);
                }
                entry.Source = source;
                entry.Seen = now; entry.Running = true;
            }
        }
    }

    public RemoteSnapshot Read(bool viewing = true)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            if (viewing) lastViewer = now;
            return new(now, detectedAt, entries.Values.Select(e => new RemoteClient(e.Id, e.Source.Label,
                e.Source.Window.Identity.ProcessId, e.Running ? true : null, e.Running ? e.Source.Window.Status != "Not responding" : null,
                e.Running ? e.Source.Window.Status : "Window closed / not detected", e.Running ? e.Source.Window.MemoryBytes : null,
                e.Captured, e.PreviewStatus, e.Source.GameName, e.Running ? e.Source.SessionStatus : "Session unknown", e.Running ? e.Source.JoinedAt : null)).ToArray());
        }
    }

    public byte[]? Image(string id)
    {
        lock (gate) return entries.Values.FirstOrDefault(e => e.Id == id)?.Jpeg;
    }

    public async Task RunAsync(CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                CaptureDue(cancellation);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { ClearImages(); }
    }

    public void CaptureDue(CancellationToken cancellation = default)
    {
        Entry[] due;
        lock (gate)
        {
            if (clock.GetUtcNow() - lastViewer >= ViewerLease) { ClearImages(); return; }
            due = entries.Values.Where(e => e.Running && (e.Attempt is null || clock.GetUtcNow() - e.Attempt >= RefreshInterval)).ToArray();
        }
        foreach (var entry in due)
        {
            cancellation.ThrowIfCancellationRequested();
            WindowIdentity identity;
            lock (gate)
            {
                if (clock.GetUtcNow() - lastViewer >= ViewerLease) { ClearImages(); return; }
                identity = entry.Source.Window.Identity;
                if (!entries.TryGetValue(identity, out var current) || !ReferenceEquals(entry, current) || !entry.Running) continue;
                entry.Attempt = clock.GetUtcNow();
                if (entry.Source.Window.Status is "Minimized externally" or "Other desktop / cloaked" or "Not responding")
                { entry.PreviewStatus = "Preview unavailable: " + entry.Source.Window.Status; continue; }
            }
            byte[]? jpeg = null; string? error = null;
            try
            {
                jpeg = capture.Capture(identity);
                if (jpeg.Length is 0 or > 524288) { jpeg = null; error = "Preview exceeded the image size limit"; }
            }
            catch (Exception ex)
            {
                error = "Preview unavailable; last image may be outdated";
                // Avoid repeating the same capture error every eight seconds.
                if (entry.PreviewStatus != error) log.Write($"Remote capture failed for PID {identity.ProcessId}.", ex);
            }
            lock (gate)
            {
                if (!entries.TryGetValue(identity, out var current) || !ReferenceEquals(entry, current) || !entry.Running) continue;
                if (jpeg is not null && !cancellation.IsCancellationRequested)
                {
                    entry.Jpeg = jpeg; entry.Captured = clock.GetUtcNow(); entry.PreviewStatus = "Preview available";
                    // A hard cache budget, independent of client count or image complexity.
                    long bytes = entries.Values.Sum(e => (long)(e.Jpeg?.Length ?? 0));
                    foreach (var old in entries.Values.Where(e => e.Jpeg is not null && e != entry).OrderBy(e => e.Captured))
                    {
                        if (bytes <= 8 * 1048576) break;
                        bytes -= old.Jpeg!.Length; old.Jpeg = null; old.Captured = null;
                        old.PreviewStatus = "Preview released to keep memory use low";
                    }
                }
                else entry.PreviewStatus = error ?? "Monitoring stopped";
            }
        }
    }

    public void ClearImages()
    {
        lock (gate) foreach (var entry in entries.Values)
        {
            entry.Jpeg = null; entry.Captured = null; entry.Attempt = null;
            entry.PreviewStatus = "Waiting for a viewer";
        }
    }

    public void ResetViewers()
    {
        lock (gate) { lastViewer = DateTimeOffset.MinValue; ClearImages(); }
    }
}
