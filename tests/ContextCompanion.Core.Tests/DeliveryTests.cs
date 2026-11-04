using System.Text.Json;
using ContextCompanion.Core.Delivery;
using ContextCompanion.Core.Packets;

namespace ContextCompanion.Core.Tests;

public class DeliveryTests : IDisposable
{
    private readonly TempDirectory temp = new();
    private readonly ManualTime time = new(Samples.Start);
    private int variant;

    public void Dispose() => temp.Dispose();

    private Outbox Open() => new(temp.Combine("outbox"), time: time);

    private PacketDraft Next()
    {
        time.Advance(TimeSpan.FromMilliseconds(5));
        return Samples.Draft(time, variant++);
    }

    // InboxDestination

    [Fact]
    public async Task InboxDestinationDeliversAValidPacketAndReadsItsAcknowledgement()
    {
        var outbox = Open();
        var inbox = new InboxDestination(temp.Combine("inbox"));
        using var queue = new DeliveryQueue(outbox, [inbox], time: time);
        var record = outbox.Enqueue(Next(), inbox.Name).Record;

        Assert.Equal(1, await queue.DeliverQueuedAsync(CancellationToken.None));

        Assert.Equal(DeliveryState.Delivered, outbox.Find(record.Id)!.State);
        string delivered = Path.Combine(inbox.Root, record.Id);
        var packet = PacketReader.Read(delivered);
        Assert.Equal(record.ItemSha256, packet.Manifest.Items.Select(i => i.Sha256));
        Assert.Equal(Samples.SecretWindowTitle, packet.Manifest.Source!.WindowTitle);
        Assert.False(Directory.Exists(outbox.StoredPacketDirectory(record.Id)));

        Assert.Null(inbox.ReadAcknowledgement(record.Id));
        PacketAcknowledgements.Write(delivered, "reference-consumer", time: time);
        Assert.Equal(1, outbox.RefreshAcknowledgements([inbox]));
        Assert.Equal("reference-consumer", outbox.Find(record.Id)!.Consumer);
    }

    [Fact]
    public async Task InvalidAcknowledgementInTheInboxDoesNotMakeAPacketConsumed()
    {
        var outbox = Open();
        var inbox = new InboxDestination(temp.Combine("inbox"));
        using var queue = new DeliveryQueue(outbox, [inbox], time: time);
        var record = outbox.Enqueue(Next(), inbox.Name).Record;
        await queue.DeliverQueuedAsync(CancellationToken.None);

        File.WriteAllText(Path.Combine(inbox.Root, record.Id, "ack.json"), """{"id":"x"}""");
        Assert.Null(inbox.ReadAcknowledgement(record.Id));
        Assert.Equal(0, outbox.RefreshAcknowledgements([inbox]));
        Assert.Equal(DeliveryState.Delivered, outbox.Find(record.Id)!.State);
    }

    [Fact]
    public async Task DamagedStoredCopyIsNotDelivered()
    {
        var outbox = Open();
        var inbox = new InboxDestination(temp.Combine("inbox"));
        var record = outbox.Enqueue(Next(), inbox.Name).Record;
        File.AppendAllText(Path.Combine(outbox.StoredPacketDirectory(record.Id), "selection.txt"), "tampered");

        Assert.Equal(DeliveryOutcome.NotDelivered, await inbox.DeliverAsync(outbox.StoredPacketDirectory(record.Id), CancellationToken.None));
        Assert.Empty(PacketReader.ListPacketDirectories(inbox.Root));
    }

    [Fact]
    public async Task PacketAlreadyInTheInboxIsReportedHonestly()
    {
        var outbox = Open();
        var inbox = new InboxDestination(temp.Combine("inbox"));
        var record = outbox.Enqueue(Next(), inbox.Name).Record;
        string stored = outbox.StoredPacketDirectory(record.Id);

        Assert.Equal(DeliveryOutcome.Delivered, await inbox.DeliverAsync(stored, CancellationToken.None));
        // Same packet again (for example an explicit retry of an uncertain delivery): it is there, so delivered.
        Assert.Equal(DeliveryOutcome.Delivered, await inbox.DeliverAsync(stored, CancellationToken.None));
        // Something else under the same id: cannot tell what the consumer saw.
        File.AppendAllText(Path.Combine(inbox.Root, record.Id, "capture.png"), "x");
        Assert.Equal(DeliveryOutcome.Uncertain, await inbox.DeliverAsync(stored, CancellationToken.None));
    }

    [Fact]
    public void InboxDestinationClearsStaleStagingOnStart()
    {
        string root = temp.Combine("inbox");
        var staged = new InboxWriter(root).Stage(Next());
        _ = new InboxDestination(root);
        Assert.False(Directory.Exists(staged.StagingDirectory));
        Assert.Empty(PacketReader.ListPacketDirectories(root));
    }

    // DeliveryQueue

    [Fact]
    public async Task QueueDeliversInOrder()
    {
        var outbox = Open();
        var destination = new FakeDestination();
        using var queue = new DeliveryQueue(outbox, [destination], time: time);
        var ids = Enumerable.Range(0, 3).Select(_ => outbox.Enqueue(Next(), "fake").Record.Id).ToArray();

        Assert.Equal(3, await queue.DeliverQueuedAsync(CancellationToken.None));
        Assert.Equal(ids, destination.Delivered);
        Assert.All(outbox.Snapshot(), r => Assert.Equal(DeliveryState.Delivered, r.State));
    }

    [Fact]
    public async Task UnavailableDestinationIsRetriedOnlyAfterTheDelay()
    {
        var outbox = Open();
        var destination = new FakeDestination { Deliver = (_, _) => Task.FromResult(DeliveryOutcome.Unavailable) };
        using var queue = new DeliveryQueue(outbox, [destination], retryDelay: TimeSpan.FromSeconds(3), time: time);
        var record = outbox.Enqueue(Next(), "fake").Record;

        await queue.DeliverQueuedAsync(CancellationToken.None);
        Assert.Equal(DeliveryState.Queued, outbox.Find(record.Id)!.State);
        Assert.Equal(0, await queue.DeliverQueuedAsync(CancellationToken.None));
        Assert.Single(destination.Delivered);

        destination.Deliver = (_, _) => Task.FromResult(DeliveryOutcome.Delivered);
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(1, await queue.DeliverQueuedAsync(CancellationToken.None));
        Assert.Equal(DeliveryState.Delivered, outbox.Find(record.Id)!.State);
    }

    [Fact]
    public async Task DestinationErrorOrCancellationMeansUncertainAndNoAutomaticRetry()
    {
        var outbox = Open();
        var destination = new FakeDestination { Deliver = (_, _) => throw new IOException("lost") };
        using var queue = new DeliveryQueue(outbox, [destination], time: time);
        var failed = outbox.Enqueue(Next(), "fake").Record;
        await queue.DeliverQueuedAsync(CancellationToken.None);
        Assert.Equal(DeliveryState.Uncertain, outbox.Find(failed.Id)!.State);

        destination.Deliver = (_, token) => Task.FromCanceled<DeliveryOutcome>(new CancellationToken(canceled: true));
        var cancelled = outbox.Enqueue(Next(), "fake").Record;
        await queue.DeliverQueuedAsync(CancellationToken.None);
        Assert.Equal(DeliveryState.Uncertain, outbox.Find(cancelled.Id)!.State);

        destination.Deliver = (_, _) => Task.FromResult(DeliveryOutcome.Delivered);
        Assert.Equal(0, await queue.DeliverQueuedAsync(CancellationToken.None));
        Assert.Equal(2, destination.Delivered.Count);
    }

    [Fact]
    public async Task UnknownDestinationFailsThePacket()
    {
        var outbox = Open();
        using var queue = new DeliveryQueue(outbox, [new FakeDestination()], time: time);
        var record = outbox.Enqueue(Next(), "elsewhere").Record;
        await queue.DeliverQueuedAsync(CancellationToken.None);
        Assert.Equal(DeliveryState.Failed, outbox.Find(record.Id)!.State);
    }

    [Fact]
    public async Task PausedQueueDeliversNothingUntilResumed()
    {
        var outbox = Open();
        var destination = new FakeDestination();
        using var queue = new DeliveryQueue(outbox, [destination], time: time);
        outbox.Enqueue(Next(), "fake");
        queue.Pause();
        Assert.Equal(0, await queue.DeliverQueuedAsync(CancellationToken.None));
        queue.Resume();
        Assert.Equal(1, await queue.DeliverQueuedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RunningQueueDeliversWhenWokenAndStopsOnCancel()
    {
        var outbox = Open();
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var destination = new FakeDestination
        {
            Deliver = (_, _) => { delivered.TrySetResult(); return Task.FromResult(DeliveryOutcome.Delivered); },
        };
        using var queue = new DeliveryQueue(outbox, [destination], retryDelay: TimeSpan.FromMinutes(5));
        using var stop = new CancellationTokenSource();
        var run = queue.RunAsync(stop.Token);

        outbox.Enqueue(Next(), "fake");
        queue.Wake();
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(destination.Delivered);
    }

    // Audit log

    private static readonly HashSet<string> AuditKeys =
        ["time", "packet", "destination", "from", "to", "content_sha256", "item_sha256", "bytes", "consumer"];

    [Fact]
    public async Task AuditLogRecordsEveryStateChangeWithHashesAndNoContent()
    {
        var outbox = Open();
        var inbox = new InboxDestination(temp.Combine("inbox"));
        var flaky = new FakeDestination("flaky") { Deliver = (_, _) => Task.FromResult(DeliveryOutcome.NotDelivered) };
        using var queue = new DeliveryQueue(outbox, [inbox, flaky], time: time);

        var draft = Next();
        var delivered = outbox.Enqueue(draft, inbox.Name).Record;
        var failed = outbox.Enqueue(Next(), flaky.Name).Record;
        await queue.DeliverQueuedAsync(CancellationToken.None);
        PacketAcknowledgements.Write(Path.Combine(inbox.Root, delivered.Id), "reader", note: "ACK-NOTE-SECRET", time: time);
        outbox.RefreshAcknowledgements([inbox, flaky]);
        outbox.Retry(failed.Id);
        await queue.DeliverQueuedAsync(CancellationToken.None);
        outbox.Discard(failed.Id);

        string text = File.ReadAllText(outbox.Audit.Path);
        var entries = AuditLog.Read(outbox.Audit.Path);
        Assert.Equal(
            [(null, "queued"), ("queued", "delivering"), ("delivering", "delivered"), ("delivered", "consumed")],
            entries.Where(e => e.Packet == delivered.Id).Select(e => (e.From, e.To)));
        Assert.Equal(
            [(null, "queued"), ("queued", "delivering"), ("delivering", "failed"), ("failed", "queued"),
             ("queued", "delivering"), ("delivering", "failed"), ("failed", "discarded")],
            entries.Where(e => e.Packet == failed.Id).Select(e => (e.From, e.To)));

        var consumed = entries.Single(e => e.To == "consumed");
        Assert.Equal("reader", consumed.Consumer);
        Assert.Equal(inbox.Name, consumed.Destination);
        Assert.Equal(draft.ContentSha256, consumed.ContentSha256);
        Assert.Equal(draft.Manifest.Items.Select(i => i.Sha256), consumed.ItemSha256);
        Assert.Equal(draft.TotalBytes, consumed.Bytes);
        Assert.All(entries, e => Assert.InRange(e.Time, Samples.Start, time.Now));
        Assert.Equal(entries.OrderBy(e => e.Time).Select(e => e.Time), entries.Select(e => e.Time));

        // No payload, window title, process name, region, redaction text or acknowledgement note.
        Assert.DoesNotContain(Samples.SecretText, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Samples.SecretWindowTitle, text, StringComparison.Ordinal);
        Assert.DoesNotContain("notepad", text, StringComparison.Ordinal);
        Assert.DoesNotContain("API key", text, StringComparison.Ordinal);
        Assert.DoesNotContain("-1920", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ACK-NOTE-SECRET", text, StringComparison.Ordinal);
        Assert.DoesNotContain("selection.txt", text, StringComparison.Ordinal);
        foreach (string line in File.ReadAllLines(outbox.Audit.Path))
        {
            using var json = JsonDocument.Parse(line);
            Assert.All(json.RootElement.EnumerateObject(), p => Assert.Contains(p.Name, AuditKeys));
        }

        // Neither does the outbox state.
        string state = File.ReadAllText(temp.Combine("outbox", "outbox.json"));
        Assert.DoesNotContain(Samples.SecretWindowTitle, state, StringComparison.Ordinal);
        Assert.DoesNotContain("ACK-NOTE-SECRET", state, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditLogIsAppendOnlyAndSurvivesATruncatedLine()
    {
        var outbox = Open();
        outbox.Enqueue(Next(), "fake");
        string before = File.ReadAllText(outbox.Audit.Path);
        File.AppendAllText(outbox.Audit.Path, "{\"time\":\"2026-11"); // a write cut short by a crash

        var reopened = Open();
        reopened.Enqueue(Next(), "fake");
        string after = File.ReadAllText(reopened.Audit.Path);
        Assert.StartsWith(before, after, StringComparison.Ordinal);
        Assert.Equal(2, AuditLog.Read(reopened.Audit.Path).Count);
    }
}
