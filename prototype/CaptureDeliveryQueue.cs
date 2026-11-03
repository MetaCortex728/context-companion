using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace DomoCompanion;

public sealed record CaptureSendResult(string Status, Dictionary<string, double>? Timings = null);

public sealed class CaptureDeliveryQueue : IDisposable
{
    public const int Concurrency = CaptureOutbox.PendingLimit;
    private readonly CaptureOutbox outbox;
    private readonly Func<bool> available;
    private readonly Func<CaptureAttempt, CancellationToken, Task<CaptureSendResult>> send;
    private readonly Action failed;
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> active = new();
    private volatile bool paused;
    private long retryAfter;

    public CaptureDeliveryQueue(CaptureOutbox outbox, Func<bool> available,
        Func<CaptureAttempt, CancellationToken, Task<CaptureSendResult>> send, Action failed)
    { this.outbox = outbox; this.available = available; this.send = send; this.failed = failed; }

    public void Wake() { try { wake.Release(); } catch (SemaphoreFullException) { } }
    public void Pause()
    {
        paused = true;
        foreach (var cancellation in active.Values)
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        Wake();
    }
    public void Resume() { paused = false; Wake(); }

    public async Task Run(CancellationToken stop)
    {
        var tasks = new List<Task>();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                foreach (var task in tasks.Where(t => t.IsCompleted).ToArray())
                { tasks.Remove(task); await task; }
                if (!paused && available() && Environment.TickCount64 >= Interlocked.Read(ref retryAfter))
                {
                    foreach (var attempt in outbox.Snapshot().Where(a => a.Status == "queued").Take(Concurrency - tasks.Count))
                        tasks.Add(SendOne(attempt, lifetime.Token));
                }
                await wake.WaitAsync(TimeSpan.FromMilliseconds(tasks.Count > 0 ? 50 : 3000), lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { failed(); }
        finally
        {
            lifetime.Cancel();
            try { await Task.WhenAll(tasks); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or OperationCanceledException)
            { failed(); }
        }
    }

    private async Task SendOne(CaptureAttempt attempt, CancellationToken stop)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stop);
        if (!active.TryAdd(attempt.Id, cancellation)) return;
        try
        {
            if (paused || stop.IsCancellationRequested) return;
            outbox.SetStatus(attempt.Id, "sending", "Local image-reference handoff in progress.");
            CaptureSendResult result;
            try { result = await send(attempt, cancellation.Token); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
            { result = new("uncertain"); }
            var (status, note) = result.Status switch
            {
                "accepted" => ("accepted", "App accepted reference; image viewing is separate."),
                "unavailable" => ("queued", "App connection unavailable; definitely unsent, waiting."),
                "rejected" => ("failed", "Capture validation failed before dispatch; not sent."),
                _ => ("uncertain", "May have arrived. Not retried automatically.")
            };
            if (status == "queued") Interlocked.Exchange(ref retryAfter, Environment.TickCount64 + 3000);
            outbox.SetStatus(attempt.Id, status, note, result.Timings);
        }
        finally { active.TryRemove(attempt.Id, out _); }
    }

    public void Dispose() => wake.Dispose();
}
