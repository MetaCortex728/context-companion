using System.Text.Json;

namespace ContextCompanion.Core.Delivery;

/// <summary>
/// Single-consumer delivery queue over an <see cref="Outbox"/>: delivers queued packets in order, one at a time,
/// with pause and resume. A destination that is unavailable is retried after a delay; an uncertain or
/// failed packet is never retried automatically.
/// </summary>
public sealed class DeliveryQueue : IDisposable
{
    private readonly Outbox outbox;
    private readonly IReadOnlyDictionary<string, IDestination> destinations;
    private readonly Action<Exception>? failed;
    private readonly TimeSpan retryDelay;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly Dictionary<string, DateTimeOffset> retryAfter = new(StringComparer.Ordinal);
    private CancellationTokenSource? active;
    private volatile bool paused;

    public DeliveryQueue(Outbox outbox, IEnumerable<IDestination> destinations, Action<Exception>? failed = null,
        TimeSpan? retryDelay = null, TimeProvider? time = null)
    {
        this.outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        this.destinations = destinations.ToDictionary(d => d.Name, StringComparer.Ordinal);
        this.failed = failed;
        this.retryDelay = retryDelay ?? TimeSpan.FromSeconds(3);
        this.time = time ?? TimeProvider.System;
    }

    public bool IsPaused => paused;

    /// <summary>Asks the running loop to look for work now.</summary>
    public void Wake()
    {
        try { wake.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Stops starting deliveries and cancels the one in progress (which then becomes uncertain unless the destination knows better).</summary>
    public void Pause()
    {
        paused = true;
        try { active?.Cancel(); } catch (ObjectDisposedException) { }
        Wake();
    }

    public void Resume() { paused = false; Wake(); }

    /// <summary>
    /// Delivers every queued packet whose destination is available, in order. Returns how many attempts finished.
    /// </summary>
    public async Task<int> DeliverQueuedAsync(CancellationToken cancellationToken)
    {
        int attempts = 0;
        foreach (var record in outbox.Snapshot().Where(r => r.State == DeliveryState.Queued))
        {
            if (paused || cancellationToken.IsCancellationRequested) break;
            if (!destinations.TryGetValue(record.Destination, out var destination))
            {
                outbox.FailUndeliverable(record.Id);
                continue;
            }
            lock (retryAfter)
                if (retryAfter.TryGetValue(destination.Name, out var after) && time.GetUtcNow() < after) continue;

            outbox.BeginDelivery(record.Id);
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            active = attempt;
            DeliveryOutcome outcome;
            try
            {
                outcome = await destination.DeliverAsync(outbox.StoredPacketDirectory(record.Id), attempt.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // Whatever happened, the packet might have arrived.
                outcome = DeliveryOutcome.Uncertain;
            }
            finally { active = null; }

            if (outcome == DeliveryOutcome.Unavailable)
                lock (retryAfter) retryAfter[destination.Name] = time.GetUtcNow() + retryDelay;
            else
                lock (retryAfter) retryAfter.Remove(destination.Name);
            outbox.CompleteDelivery(record.Id, outcome);
            attempts++;
        }
        return attempts;
    }

    /// <summary>Runs until <paramref name="stop"/> is cancelled, delivering when woken or every few seconds.</summary>
    public async Task RunAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (!paused)
                {
                    await DeliverQueuedAsync(stop).ConfigureAwait(false);
                    outbox.RefreshAcknowledgements(destinations.Values);
                }
                await wake.WaitAsync(retryDelay, stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            failed?.Invoke(ex);
        }
    }

    public void Dispose() => wake.Dispose();
}
