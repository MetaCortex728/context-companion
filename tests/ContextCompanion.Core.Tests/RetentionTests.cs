using System.Text.Json;
using ContextCompanion.Core.Delivery;
using ContextCompanion.Core.Packets;
using ContextCompanion.Core.Privacy;
using ContextCompanion.Tool;

namespace ContextCompanion.Core.Tests;

public class RetentionTests : IDisposable
{
    private readonly TempDirectory temp = new();
    private readonly ManualTime time = new(Samples.Start);
    private int variant;

    public void Dispose() => temp.Dispose();

    private string Inbox => temp.Combine("inbox");

    /// <summary>Writes a packet into the inbox one hour after the previous one. Returns its id.</summary>
    private string Write()
    {
        time.Advance(TimeSpan.FromHours(1));
        return Path.GetFileName(Samples.WritePacket(Inbox, time, variant++));
    }

    private long Size(string id) => new DirectoryInfo(Path.Combine(Inbox, id)).EnumerateFiles().Sum(f => f.Length);

    private (int Exit, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exit = Cli.Run(args, output, error, new CliEnvironment { Time = time });
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public void DefaultsAreDocumentedValues()
    {
        var limits = PrivacySettings.Default.Retention.ToLimits();
        Assert.Equal(new RetentionLimits(200, 512L * 1024 * 1024, TimeSpan.FromDays(7)), limits);
        Assert.Equal(RetentionLimits.None, PrivacySettings.Parse("""{"schema_version":1,"retention":{"max_packets":null,"max_bytes":null,"max_age":null}}""").Retention.ToLimits());
    }

    [Theory]
    [InlineData("""{"max_packets":0}""")]
    [InlineData("""{"max_bytes":-1}""")]
    [InlineData("""{"max_age":"7 days"}""")]
    [InlineData("""{"max_age":"0d"}""")]
    [InlineData("""{"max_age":"99999w"}""")]
    public void InvalidRetentionSettingsAreRefused(string retention) =>
        Assert.Throws<PrivacySettingsException>(() => PrivacySettings.Parse($$"""{"schema_version":1,"retention":{{retention}}}"""));

    [Theory]
    [InlineData("90s", 90)]
    [InlineData("30m", 1800)]
    [InlineData("12h", 43200)]
    [InlineData("7d", 604800)]
    [InlineData("2w", 1209600)]
    public void DurationsParse(string text, int seconds) => Assert.Equal(TimeSpan.FromSeconds(seconds), Duration.Parse(text));

    [Fact]
    public void ConsumedPacketsGoFirstThenTheOldest()
    {
        string a = Write(), b = Write(), c = Write(), d = Write();
        PacketAcknowledgements.Write(Path.Combine(Inbox, c), "reader", time: time);

        var plan = InboxRetention.Plan(Inbox, new RetentionLimits(2, null, null), time.GetUtcNow());

        Assert.Equal([(c, InboxRetention.ReasonMaxPackets), (a, InboxRetention.ReasonMaxPackets)], plan.Select(r => (r.Id, r.Reason)));
        Assert.Equal(Size(c), plan[0].Bytes);
        Assert.DoesNotContain(plan, r => r.Id == b || r.Id == d);
    }

    [Fact]
    public void AgeAndByteLimitsApply()
    {
        string a = Write(), b = Write(), c = Write();
        var byAge = InboxRetention.Plan(Inbox, new RetentionLimits(null, null, TimeSpan.FromMinutes(90)), time.GetUtcNow());
        Assert.Equal([(a, InboxRetention.ReasonMaxAge)], byAge.Select(r => (r.Id, r.Reason)));

        long keepTwo = Size(b) + Size(c);
        var byBytes = InboxRetention.Plan(Inbox, new RetentionLimits(null, keepTwo, null), time.GetUtcNow());
        Assert.Equal([(a, InboxRetention.ReasonMaxBytes)], byBytes.Select(r => (r.Id, r.Reason)));
        Assert.Empty(InboxRetention.Plan(Inbox, RetentionLimits.None, time.GetUtcNow()));
    }

    [Fact]
    public void QueuedAndDeliveringPacketsAreNeverRemoved()
    {
        string a = Write(), b = Write(), c = Write();
        var states = new Dictionary<string, DeliveryState> { [a] = DeliveryState.Delivering, [b] = DeliveryState.Queued, [c] = DeliveryState.Delivered };
        DeliveryState? StateOf(string id) => states.TryGetValue(id, out var s) ? s : null;

        var plan = InboxRetention.Plan(Inbox, new RetentionLimits(1, 1, TimeSpan.FromSeconds(1)), time.GetUtcNow(), StateOf);
        Assert.Equal([c], plan.Select(r => r.Id));
        Assert.Equal([c], InboxRetention.PlanPurge(Inbox, PurgeKind.All, time.GetUtcNow(), stateOf: StateOf).Select(r => r.Id));
        Assert.Throws<InvalidOperationException>(() => InboxRetention.PlanPurge(Inbox, PurgeKind.Id, time.GetUtcNow(), id: a, stateOf: StateOf));
    }

    [Fact]
    public void RetentionRunsAfterEachDeliveryAndIsAuditedWithIdsAndReasonsOnly()
    {
        var audit = new AuditLog(temp.Combine("audit.jsonl"), time);
        var outbox = new Outbox(temp.Combine("outbox"), audit: audit, time: time);
        var inbox = new InboxDestination(Inbox, retention: new RetentionLimits(2, null, null), audit: audit, time: time);
        using var queue = new DeliveryQueue(outbox, [inbox], time: time);

        string Deliver()
        {
            time.Advance(TimeSpan.FromMinutes(1));
            var id = outbox.Enqueue(Samples.Draft(time, variant++), inbox.Name).Record.Id;
            queue.DeliverQueuedAsync(CancellationToken.None).GetAwaiter().GetResult();
            return id;
        }

        string a = Deliver(), b = Deliver();
        PacketAcknowledgements.Write(Path.Combine(Inbox, b), "reader", time: time);
        outbox.RefreshAcknowledgements([inbox]);
        string c = Deliver();

        Assert.Equal([a, c], PacketReader.ListPacketDirectories(Inbox).Select(Path.GetFileName));
        var removed = Assert.Single(AuditLog.Read(audit.Path), e => e.To == AuditLog.Removed);
        Assert.Equal((b, "inbox", InboxRetention.ReasonMaxPackets), (removed.Packet, removed.Destination, removed.Reason));
        string line = File.ReadAllLines(audit.Path).Single(l => l.Contains("\"removed\"", StringComparison.Ordinal));
        using var json = JsonDocument.Parse(line);
        Assert.Equal(["time", "packet", "destination", "to", "reason"], json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(Inbox, ".tmp")));
    }

    // ccomp purge

    [Fact]
    public void PurgeIsADryRunUnlessYesIsGiven()
    {
        string a = Write(), b = Write();

        var dry = Run("purge", Inbox, "--all");
        Assert.Equal(Cli.Ok, dry.Exit);
        Assert.Contains($"Would remove {a}: purge-all, {Size(a)} bytes", dry.Output, StringComparison.Ordinal);
        Assert.Contains($"Would remove {b}: purge-all", dry.Output, StringComparison.Ordinal);
        Assert.Contains("--yes", dry.Output, StringComparison.Ordinal);
        Assert.Equal(2, PacketReader.ListPacketDirectories(Inbox).Count);
        Assert.False(File.Exists(InboxRetention.DefaultAuditPath(Inbox)));

        var real = Run("purge", Inbox, "--all", "--yes");
        Assert.Equal(Cli.Ok, real.Exit);
        Assert.Contains($"Removed {a}: purge-all", real.Output, StringComparison.Ordinal);
        Assert.Contains("may still be recoverable", real.Output, StringComparison.Ordinal);
        Assert.Empty(PacketReader.ListPacketDirectories(Inbox));
        var entries = AuditLog.Read(InboxRetention.DefaultAuditPath(Inbox));
        Assert.Equal([(a, "purge-all"), (b, "purge-all")], entries.Select(e => (e.Packet!, e.Reason!)));
        string log = File.ReadAllText(InboxRetention.DefaultAuditPath(Inbox));
        Assert.DoesNotContain(Samples.SecretText, log, StringComparison.Ordinal);
        Assert.DoesNotContain(Samples.SecretWindowTitle, log, StringComparison.Ordinal);
        Assert.DoesNotContain("notepad", log, StringComparison.Ordinal);

        Assert.Contains("Nothing to remove.", Run("purge", Inbox, "--all", "--yes").Output, StringComparison.Ordinal);
    }

    [Fact]
    public void PurgeOlderThanAndById()
    {
        string a = Write(), b = Write(), c = Write();
        var older = Run("purge", Inbox, "--older-than", "90m", "--yes", "--audit", temp.Combine("custom.jsonl"));
        Assert.Equal(Cli.Ok, older.Exit);
        Assert.Equal([b, c], PacketReader.ListPacketDirectories(Inbox).Select(Path.GetFileName));
        Assert.Equal((a, "purge-older-than"), (AuditLog.Read(temp.Combine("custom.jsonl")).Single().Packet, AuditLog.Read(temp.Combine("custom.jsonl")).Single().Reason));

        Assert.Equal(Cli.Ok, Run("purge", Inbox, "--id", c, "--yes").Exit);
        Assert.Equal([b], PacketReader.ListPacketDirectories(Inbox).Select(Path.GetFileName));
        Assert.Equal(Cli.Failed, Run("purge", Inbox, "--id", c, "--yes").Exit); // no longer there
    }

    [Fact]
    public void PurgeWithoutASelectorAppliesTheRetentionLimits()
    {
        string a = Write(), b = Write(), c = Write();
        File.WriteAllText(temp.Combine("privacy.json"), """{"schema_version":1,"retention":{"max_packets":1,"max_age":null}}""");
        var result = Run("purge", Inbox, "--settings", temp.Combine("privacy.json"));
        Assert.Contains($"Would remove {a}: retention-max-packets", result.Output, StringComparison.Ordinal);
        Assert.Contains($"Would remove {b}: retention-max-packets", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(c, result.Output, StringComparison.Ordinal);

        // The default limits (7 days) remove nothing here.
        Assert.Contains("Nothing to remove.", Run("purge", Inbox).Output, StringComparison.Ordinal);
        time.Advance(TimeSpan.FromDays(8));
        Assert.Contains("retention-max-age", Run("purge", Inbox).Output, StringComparison.Ordinal);
    }

    [Fact]
    public void PurgeRespectsTheOutboxState()
    {
        var outbox = new Outbox(temp.Combine("outbox"), time: time);
        time.Advance(TimeSpan.FromMinutes(1));
        var record = outbox.Enqueue(Samples.Draft(time, 42), "inbox").Record;
        outbox.BeginDelivery(record.Id);
        new InboxWriter(Inbox).Write(PacketDraft.FromPacket(PacketReader.Read(outbox.StoredPacketDirectory(record.Id))));

        var result = Run("purge", Inbox, "--id", record.Id, "--yes", "--outbox", temp.Combine("outbox"));
        Assert.Equal(Cli.Failed, result.Exit);
        Assert.Contains("still being delivered", result.Error, StringComparison.Ordinal);
        Assert.Contains("Nothing to remove.", Run("purge", Inbox, "--all", "--outbox", temp.Combine("outbox")).Output, StringComparison.Ordinal);
        Assert.Single(PacketReader.ListPacketDirectories(Inbox));
    }

    [Theory]
    [InlineData("purge")]
    [InlineData("purge", "inbox", "--all", "--older-than", "1d")]
    [InlineData("purge", "inbox", "--older-than", "soon")]
    [InlineData("purge", "inbox", "--id", "nope")]
    [InlineData("purge", "inbox", "--force")]
    [InlineData("purge", "inbox", "other")]
    [InlineData("purge", "inbox", "--all", "--settings", "x.json")]
    public void PurgeUsageErrorsExitWithTwo(params string[] args) => Assert.Equal(Cli.Usage, Run(args).Exit);
}
