using System.Text.Json;
using ContextCompanion.Core.Delivery;
using ContextCompanion.Core.Packets;

namespace ContextCompanion.Core.Tests;

/// <summary>A destination whose behaviour the test sets.</summary>
internal sealed class FakeDestination(string name = "fake") : IDestination
{
    public string Name { get; } = name;
    public Func<string, CancellationToken, Task<DeliveryOutcome>> Deliver { get; set; } = (_, _) => Task.FromResult(DeliveryOutcome.Delivered);
    public Dictionary<string, PacketAcknowledgement> Acks { get; } = [];
    public List<string> Delivered { get; } = [];

    public Task<DeliveryOutcome> DeliverAsync(string packetDirectory, CancellationToken cancellationToken)
    {
        Delivered.Add(Path.GetFileName(packetDirectory));
        return Deliver(packetDirectory, cancellationToken);
    }

    public PacketAcknowledgement? ReadAcknowledgement(string packetId) => Acks.GetValueOrDefault(packetId);
}

public class OutboxTests : IDisposable
{
    private readonly TempDirectory temp = new();
    private readonly ManualTime time = new(Samples.Start);
    private int variant = 1; // variant 0 is the content of Same()

    public void Dispose() => temp.Dispose();

    private string OutboxRoot => temp.Combine("outbox");
    private Outbox Open(OutboxOptions? options = null) => new(OutboxRoot, options, time: time);

    /// <summary>A draft with content distinct from every earlier one.</summary>
    private PacketDraft Next()
    {
        time.Advance(TimeSpan.FromMilliseconds(5));
        return Samples.Draft(time, variant++);
    }

    private PacketDraft Same() { time.Advance(TimeSpan.FromMilliseconds(5)); return Samples.Draft(time, 0); }

    private static PacketAcknowledgement Ack(string id, DateTimeOffset at, string consumer = "agent") =>
        new() { Id = id, Consumer = consumer, Consumed = at };

    private OutboxRecord Delivering(Outbox outbox, string destination = "fake")
    {
        var record = outbox.Enqueue(Next(), destination).Record;
        return outbox.BeginDelivery(record.Id);
    }

    // Capacity

    [Fact]
    public void PendingPacketLimitDefaultsToFourAndPreservesExistingPackets()
    {
        var outbox = Open();
        Assert.Equal(4, outbox.Options.MaxPendingPackets);
        Assert.Equal(64L * 1024 * 1024, outbox.Options.MaxStoredBytes);
        for (int i = 0; i < 4; i++) Assert.True(outbox.Enqueue(Next(), "fake").Queued);

        var fifth = Next();
        Assert.Throws<OutboxFullException>(() => outbox.Enqueue(fifth, "fake"));
        Assert.Equal(4, outbox.Snapshot().Count);
        Assert.Equal(4, PacketReader.ListPacketDirectories(Path.Combine(OutboxRoot, "packets")).Count);
        Assert.False(Directory.Exists(outbox.StoredPacketDirectory(fifth.Id)));
    }

    [Fact]
    public void DeliveredPacketReleasesItsSlotButUncertainOneDoesNot()
    {
        var outbox = Open(new OutboxOptions { MaxPendingPackets = 2 });
        var a = outbox.Enqueue(Next(), "fake").Record;
        var b = outbox.Enqueue(Next(), "fake").Record;
        outbox.BeginDelivery(b.Id);
        outbox.CompleteDelivery(b.Id, DeliveryOutcome.Uncertain);
        Assert.Throws<OutboxFullException>(() => outbox.Enqueue(Next(), "fake"));

        outbox.BeginDelivery(a.Id);
        outbox.CompleteDelivery(a.Id, DeliveryOutcome.Delivered);
        Assert.True(outbox.Enqueue(Next(), "fake").Queued);
        Assert.Throws<OutboxFullException>(() => outbox.Enqueue(Next(), "fake"));
    }

    [Fact]
    public void FailedPacketFreesItsPendingSlotButItsBytesStayUntilDiscarded()
    {
        long size = Samples.Draft().TotalBytes;
        var outbox = Open(new OutboxOptions { MaxPendingPackets = 1, MaxStoredBytes = size * 2 });
        var failed = outbox.Enqueue(Next(), "fake").Record;
        outbox.BeginDelivery(failed.Id);
        outbox.CompleteDelivery(failed.Id, DeliveryOutcome.NotDelivered);
        var second = outbox.Enqueue(Next(), "fake").Record;     // pending slot is free again
        outbox.BeginDelivery(second.Id);
        outbox.CompleteDelivery(second.Id, DeliveryOutcome.NotDelivered);
        Assert.Throws<OutboxFullException>(() => outbox.Enqueue(Next(), "fake")); // bytes: two failed copies held

        outbox.Discard(failed.Id);
        Assert.True(outbox.Enqueue(Next(), "fake").Queued);
    }

    [Fact]
    public void StoredByteLimitIsConfigurable()
    {
        long size = Samples.Draft().TotalBytes;
        var outbox = Open(new OutboxOptions { MaxStoredBytes = size * 2 + 1 });
        outbox.Enqueue(Next(), "fake");
        outbox.Enqueue(Next(), "fake");
        var ex = Assert.Throws<OutboxFullException>(() => outbox.Enqueue(Next(), "fake"));
        Assert.Contains("bytes", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, outbox.Snapshot().Count);

        var tiny = Open(new OutboxOptions { MaxStoredBytes = size - 1 });
        Assert.Throws<OutboxFullException>(() => tiny.Enqueue(Next(), "other"));
    }

    [Fact]
    public void InvalidLimitsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => Open(new OutboxOptions { MaxPendingPackets = 0 }));
        Assert.Throws<ArgumentException>(() => Open(new OutboxOptions { MaxStoredBytes = 0 }));
    }

    [Fact]
    public void FinishedRecordsArePrunedBeyondTheRecordLimit()
    {
        var outbox = Open(new OutboxOptions { MaxPendingPackets = 1, MaxRecords = 3 });
        var ids = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            var record = outbox.Enqueue(Next(), "fake").Record;
            outbox.BeginDelivery(record.Id);
            outbox.CompleteDelivery(record.Id, DeliveryOutcome.Delivered);
            ids.Add(record.Id);
        }
        Assert.Equal(ids.TakeLast(3), outbox.Snapshot().Select(r => r.Id));
        Assert.Equal(15, AuditLog.Read(outbox.Audit.Path).Count); // the audit log keeps everything
    }

    // Duplicate suppression

    [Fact]
    public void ConsecutiveDuplicateForTheSameDestinationIsDropped()
    {
        var outbox = Open();
        var first = outbox.Enqueue(Same(), "fake");
        var second = outbox.Enqueue(Same(), "fake");

        Assert.True(first.Queued);
        Assert.True(second.Duplicate);
        Assert.Equal(first.Record.Id, second.Record.Id);
        Assert.Single(outbox.Snapshot());
        Assert.Single(PacketReader.ListPacketDirectories(Path.Combine(OutboxRoot, "packets")));
    }

    [Fact]
    public void SourceDetailsAndFileNamesDoNotMakeContentDifferent()
    {
        var outbox = Open();
        outbox.Enqueue(Same(), "fake");
        var renamed = Samples.Builder(time)
            .AddItem(PacketItemKind.Image, "shot.png", "image/png", Samples.Png(0))
            .AddText("text.txt", Samples.SecretText)
            .WithSource(new PacketSource { CaptureMode = "window" })
            .Build();
        Assert.True(outbox.Enqueue(renamed, "fake").Duplicate);
    }

    [Fact]
    public void SameContentForAnotherDestinationIsQueued()
    {
        var outbox = Open();
        outbox.Enqueue(Same(), "fake");
        Assert.True(outbox.Enqueue(Same(), "other").Queued);
        Assert.True(outbox.Enqueue(Same(), "fake").Duplicate); // other destination does not reset the check
    }

    [Fact]
    public void OnlyConsecutiveDuplicatesAreDropped()
    {
        var outbox = Open();
        outbox.Enqueue(Same(), "fake");
        outbox.Enqueue(Next(), "fake");
        Assert.True(outbox.Enqueue(Same(), "fake").Queued);
    }

    [Theory]
    [InlineData(DeliveryOutcome.Delivered, true)]
    [InlineData(DeliveryOutcome.Uncertain, true)]
    [InlineData(DeliveryOutcome.Unavailable, true)]
    [InlineData(DeliveryOutcome.NotDelivered, false)]
    public void DuplicateOfAnyButAFailedPacketIsDropped(DeliveryOutcome outcome, bool dropped)
    {
        var outbox = Open();
        var record = outbox.Enqueue(Same(), "fake").Record;
        outbox.BeginDelivery(record.Id);
        outbox.CompleteDelivery(record.Id, outcome);
        Assert.Equal(dropped, outbox.Enqueue(Same(), "fake").Duplicate);
    }

    [Fact]
    public void DuplicateSuppressionSurvivesRestart()
    {
        Open().Enqueue(Same(), "fake");
        Assert.True(Open().Enqueue(Same(), "fake").Duplicate);
    }

    // State transitions

    public static TheoryData<DeliveryState, DeliveryState> AllowedTransitions => new()
    {
        { DeliveryState.Queued, DeliveryState.Delivering },
        { DeliveryState.Queued, DeliveryState.Failed },
        { DeliveryState.Delivering, DeliveryState.Delivered },
        { DeliveryState.Delivering, DeliveryState.Failed },
        { DeliveryState.Delivering, DeliveryState.Uncertain },
        { DeliveryState.Delivering, DeliveryState.Queued },
        { DeliveryState.Delivered, DeliveryState.Consumed },
        { DeliveryState.Uncertain, DeliveryState.Consumed },
        { DeliveryState.Uncertain, DeliveryState.Queued },
        { DeliveryState.Failed, DeliveryState.Queued },
    };

    [Fact]
    public void TransitionTableIsExactlyTheDocumentedOne()
    {
        var allowed = AllowedTransitions.Select(row => ((DeliveryState)row[0], (DeliveryState)row[1])).ToHashSet();
        foreach (var from in Enum.GetValues<DeliveryState>())
            foreach (var to in Enum.GetValues<DeliveryState>())
                Assert.True(allowed.Contains((from, to)) == DeliveryTransitions.IsAllowed(from, to), $"{from} -> {to}");
    }

    [Fact]
    public void QueuedToDeliveringToDeliveredToConsumed()
    {
        var outbox = Open();
        var destination = new FakeDestination();
        var record = outbox.Enqueue(Next(), "fake").Record;
        Assert.Equal(DeliveryState.Queued, record.State);
        Assert.True(Directory.Exists(outbox.StoredPacketDirectory(record.Id)));

        Assert.Equal(DeliveryState.Delivering, outbox.BeginDelivery(record.Id).State);
        var delivered = outbox.CompleteDelivery(record.Id, DeliveryOutcome.Delivered);
        Assert.Equal(DeliveryState.Delivered, delivered.State);
        Assert.Equal(time.Now, delivered.Delivered);
        Assert.False(delivered.Stored);
        Assert.False(Directory.Exists(outbox.StoredPacketDirectory(record.Id)));

        Assert.False(outbox.RecordAcknowledgement(record.Id, destination)); // no ack.json yet
        Assert.Equal(DeliveryState.Delivered, outbox.Find(record.Id)!.State);

        destination.Acks[record.Id] = Ack(record.Id, time.Now, "reader-1");
        Assert.True(outbox.RecordAcknowledgement(record.Id, destination));
        var consumed = outbox.Find(record.Id)!;
        Assert.Equal(DeliveryState.Consumed, consumed.State);
        Assert.Equal("reader-1", consumed.Consumer);
        Assert.Equal(time.Now, consumed.Consumed);
    }

    [Theory]
    [InlineData(DeliveryOutcome.NotDelivered, DeliveryState.Failed)]
    [InlineData(DeliveryOutcome.Uncertain, DeliveryState.Uncertain)]
    [InlineData(DeliveryOutcome.Unavailable, DeliveryState.Queued)]
    public void DeliveringEndsInTheReportedState(DeliveryOutcome outcome, DeliveryState expected)
    {
        var outbox = Open();
        var record = Delivering(outbox);
        var after = outbox.CompleteDelivery(record.Id, outcome);
        Assert.Equal(expected, after.State);
        Assert.True(after.Stored);
        Assert.True(Directory.Exists(outbox.StoredPacketDirectory(record.Id)));
    }

    [Fact]
    public void UncertainBecomesConsumedOnlyFromAnAcknowledgement()
    {
        var outbox = Open();
        var destination = new FakeDestination();
        var record = Delivering(outbox);
        outbox.CompleteDelivery(record.Id, DeliveryOutcome.Uncertain);

        destination.Acks[record.Id] = Ack("01ARYZ6S41TSV4RRFFQ69G5FAV", time.Now); // names another packet
        Assert.False(outbox.RecordAcknowledgement(record.Id, destination));
        destination.Acks[record.Id] = Ack(record.Id, time.Now.AddHours(1)); // from the future
        Assert.False(outbox.RecordAcknowledgement(record.Id, destination));
        Assert.False(outbox.RecordAcknowledgement(record.Id, new FakeDestination("other"))); // wrong destination
        Assert.Equal(DeliveryState.Uncertain, outbox.Find(record.Id)!.State);

        destination.Acks[record.Id] = Ack(record.Id, time.Now);
        Assert.Equal(1, outbox.RefreshAcknowledgements([destination]));
        Assert.Equal(DeliveryState.Consumed, outbox.Find(record.Id)!.State);
        Assert.False(Directory.Exists(outbox.StoredPacketDirectory(record.Id)));
    }

    [Fact]
    public void QueuedOrFailedPacketCannotBeConsumed()
    {
        var outbox = Open();
        var destination = new FakeDestination();
        var queued = outbox.Enqueue(Next(), "fake").Record;
        destination.Acks[queued.Id] = Ack(queued.Id, time.Now);
        Assert.False(outbox.RecordAcknowledgement(queued.Id, destination));
        outbox.BeginDelivery(queued.Id);
        outbox.CompleteDelivery(queued.Id, DeliveryOutcome.NotDelivered);
        Assert.False(outbox.RecordAcknowledgement(queued.Id, destination));
        Assert.Equal(DeliveryState.Failed, outbox.Find(queued.Id)!.State);
    }

    [Theory]
    [InlineData(DeliveryOutcome.NotDelivered)]
    [InlineData(DeliveryOutcome.Uncertain)]
    public void FailedAndUncertainCanBeRetriedOnlyExplicitly(DeliveryOutcome outcome)
    {
        var outbox = Open();
        var record = Delivering(outbox);
        outbox.CompleteDelivery(record.Id, outcome);
        Assert.Equal(DeliveryState.Queued, outbox.Retry(record.Id).State);
    }

    [Fact]
    public void QueuedPacketWithoutAConfiguredDestinationFails()
    {
        var outbox = Open();
        var record = outbox.Enqueue(Next(), "nowhere").Record;
        Assert.Equal(DeliveryState.Failed, outbox.FailUndeliverable(record.Id).State);
    }

    [Fact]
    public void DisallowedTransitionsThrowAndChangeNothing()
    {
        var outbox = Open();
        var destination = new FakeDestination();
        var queued = outbox.Enqueue(Next(), "fake").Record;
        Assert.Throws<InvalidOperationException>(() => outbox.CompleteDelivery(queued.Id, DeliveryOutcome.Delivered));
        Assert.Throws<InvalidOperationException>(() => outbox.Retry(queued.Id));
        Assert.Throws<InvalidOperationException>(() => outbox.Discard(queued.Id));

        outbox.BeginDelivery(queued.Id);
        Assert.Throws<InvalidOperationException>(() => outbox.BeginDelivery(queued.Id));
        outbox.CompleteDelivery(queued.Id, DeliveryOutcome.Delivered);
        Assert.Throws<InvalidOperationException>(() => outbox.Retry(queued.Id));
        Assert.Throws<InvalidOperationException>(() => outbox.CompleteDelivery(queued.Id, DeliveryOutcome.Uncertain));

        destination.Acks[queued.Id] = Ack(queued.Id, time.Now);
        outbox.RecordAcknowledgement(queued.Id, destination);
        Assert.Throws<InvalidOperationException>(() => outbox.BeginDelivery(queued.Id));
        Assert.Throws<InvalidOperationException>(() => outbox.Discard(queued.Id));
        Assert.False(outbox.RecordAcknowledgement(queued.Id, destination));
        Assert.Equal(DeliveryState.Consumed, outbox.Find(queued.Id)!.State);
        Assert.Throws<ArgumentException>(() => outbox.BeginDelivery("01ARYZ6S41TSV4RRFFQ69G5FAV"));
    }

    // Persistence and restart recovery

    [Fact]
    public void StateIsPersistedAndReloaded()
    {
        var outbox = Open();
        var record = Delivering(outbox);
        outbox.CompleteDelivery(record.Id, DeliveryOutcome.NotDelivered);
        outbox.Enqueue(Next(), "fake");

        var reopened = Open();
        Assert.Equal(
            JsonSerializer.Serialize(outbox.Snapshot().ToList(), PacketJson.Options),
            JsonSerializer.Serialize(reopened.Snapshot().ToList(), PacketJson.Options));
        Assert.False(File.Exists(Path.Combine(OutboxRoot, "outbox.json.tmp")));
    }

    [Fact]
    public async Task RestartTurnsDeliveringIntoUncertainAndNeverResendsIt()
    {
        var outbox = Open();
        var record = Delivering(outbox);
        // Simulated crash: the process stops during delivery.

        var reopened = Open();
        var recovered = reopened.Find(record.Id)!;
        Assert.Equal(DeliveryState.Uncertain, recovered.State);
        Assert.Contains("not retried", recovered.Note, StringComparison.Ordinal);
        Assert.True(Directory.Exists(reopened.StoredPacketDirectory(record.Id)));

        var destination = new FakeDestination();
        using var queue = new DeliveryQueue(reopened, [destination], time: time);
        Assert.Equal(0, await queue.DeliverQueuedAsync(CancellationToken.None));
        Assert.Empty(destination.Delivered);
        Assert.Equal(DeliveryState.Uncertain, reopened.Find(record.Id)!.State);

        var audit = AuditLog.Read(reopened.Audit.Path).Last();
        Assert.Equal(("delivering", "uncertain"), (audit.From, audit.To));
    }

    [Fact]
    public void RestartRemovesLeftoversOfInterruptedWrites()
    {
        var outbox = Open();
        var kept = outbox.Enqueue(Next(), "fake").Record;
        // Crash after the stored copy was written but before the state file committed it.
        string orphan = new InboxWriter(Path.Combine(OutboxRoot, "packets")).Write(Next());
        // Crash while staging.
        var staged = new InboxWriter(Path.Combine(OutboxRoot, "packets")).Stage(Next());

        var reopened = Open();
        Assert.False(Directory.Exists(orphan));
        Assert.False(Directory.Exists(staged.StagingDirectory));
        Assert.True(Directory.Exists(reopened.StoredPacketDirectory(kept.Id)));
        Assert.Single(reopened.Snapshot());
    }

    [Fact]
    public void QueuedPacketWhoseStoredCopyVanishedFailsOnRestart()
    {
        var outbox = Open();
        var record = outbox.Enqueue(Next(), "fake").Record;
        Directory.Delete(outbox.StoredPacketDirectory(record.Id), recursive: true);
        var after = Open().Find(record.Id)!;
        Assert.Equal(DeliveryState.Failed, after.State);
        Assert.False(after.Stored);
    }

    [Theory]
    [InlineData("{bad")]
    [InlineData("[null]")]
    [InlineData("""{"schema_version":2,"records":[]}""")]
    [InlineData("""{"schema_version":1,"records":[null]}""")]
    public void CorruptStateFailsClosedWithoutErasingPackets(string content)
    {
        var record = Open().Enqueue(Next(), "fake").Record;
        File.WriteAllText(Path.Combine(OutboxRoot, "outbox.json"), content);
        Assert.Throws<IOException>(() => Open());
        Assert.True(Directory.Exists(Path.Combine(OutboxRoot, "packets", record.Id)));
    }

    [Fact]
    public void FailedStateWriteAddsNothingAndKeepsTheCommittedState()
    {
        var outbox = Open();
        var kept = outbox.Enqueue(Next(), "fake").Record;
        string state = Path.Combine(OutboxRoot, "outbox.json");
        string before = File.ReadAllText(state);
        Directory.CreateDirectory(state + ".tmp"); // makes the atomic write fail

        var draft = Next();
        AssertWriteFailure(() => outbox.Enqueue(draft, "fake"));
        Assert.Single(outbox.Snapshot());
        Assert.False(Directory.Exists(outbox.StoredPacketDirectory(draft.Id)));
        Assert.Equal(before, File.ReadAllText(state));

        AssertWriteFailure(() => outbox.BeginDelivery(kept.Id));
        Assert.Equal(DeliveryState.Queued, outbox.Find(kept.Id)!.State); // no claim in memory either

        Directory.Delete(state + ".tmp");
        Assert.Equal(kept.Id, Open().Snapshot().Single().Id);
    }

    private static void AssertWriteFailure(Action action)
    {
        var ex = Assert.ThrowsAny<Exception>(action);
        Assert.True(ex is IOException or UnauthorizedAccessException, ex.ToString());
    }
}
