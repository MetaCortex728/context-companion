using System.Text.Json;
using ContextCompanion.Core.Delivery;
using ContextCompanion.Core.Privacy;
using ContextCompanion.Tool;

namespace ContextCompanion.Core.Tests;

public class AuditViewerTests
{
    private static (int Exit, string Output, string Error) Run(TimeProvider time, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exit = Cli.Run(args, output, error, new CliEnvironment { Time = time, Protectors = [] });
        return (exit, output.ToString(), error.ToString());
    }

    /// <summary>A blocked capture, a delivered packet and a purge, all in the inbox's audit log.</summary>
    private static (PipelineHarness Harness, string Delivered) History()
    {
        var harness = new PipelineHarness(new PrivacySettings { Metadata = new MetadataSettings { WindowTitle = true, ExecutablePath = true } });
        harness.Process(new CaptureRequest { Text = Samples.SecretText, Source = Captures.Source(process: "KeePass.exe") });
        var queued = harness.Process(Captures.Region(text: Samples.SecretText));
        using var queue = new DeliveryQueue(harness.Outbox, [harness.Inbox], time: harness.Time);
        queue.DeliverQueuedAsync(CancellationToken.None).GetAwaiter().GetResult();
        harness.Time.Advance(TimeSpan.FromHours(1));
        InboxRetention.Remove(harness.Inbox.Root, InboxRetention.PlanPurge(harness.Inbox.Root, PurgeKind.All, harness.Time.GetUtcNow()), harness.Audit, harness.Inbox.Name);
        return (harness, queued.PacketId!);
    }

    [Fact]
    public void TheTableShowsTimePacketDestinationChangeAndReasonAndNoContent()
    {
        var (harness, id) = History();
        using var _ = harness;

        var (exit, output, error) = Run(harness.Time, "audit", harness.Inbox.Root);

        Assert.Equal(Cli.Ok, exit);
        Assert.Equal("", error);
        string[] lines = output.TrimEnd().ReplaceLineEndings("\n").Split('\n');
        Assert.Matches(@"^TIME\s+PACKET\s+DESTINATION\s+CHANGE\s+REASON$", lines[0]);
        Assert.Matches(@"^2026-11-03T15:04:05\.\d{3}Z\s+-\s+-\s+- -> blocked\s+denylist rule keepass$", lines[1]);
        Assert.Matches($@"^\S+\s+{id}\s+inbox\s+- -> queued$", lines[2]);
        Assert.Matches($@"^\S+\s+{id}\s+inbox\s+queued -> delivering$", lines[3]);
        Assert.Matches($@"^\S+\s+{id}\s+inbox\s+delivering -> delivered$", lines[4]);
        Assert.Matches($@"^\S+\s+{id}\s+inbox\s+- -> removed\s+purge-all$", lines[5]);
        Assert.Equal("5 entries.", lines[6]);

        foreach (string hidden in new[] { Samples.SecretText, Captures.Title, "private-user-marker", "KeePass.exe", "editor.exe", "selection.txt" })
        {
            Assert.DoesNotContain(hidden, output, StringComparison.Ordinal);
            Assert.DoesNotContain(hidden, harness.AuditText(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FiltersByPacketAndTimeAndPrintsJson()
    {
        var (harness, id) = History();
        using var _ = harness;

        var byPacket = Run(harness.Time, "audit", harness.Audit.Path, "--packet", id);
        Assert.Contains("4 entries.", byPacket.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("blocked", byPacket.Output, StringComparison.Ordinal);

        var recent = Run(harness.Time, "audit", harness.Inbox.Root, "--since", "30m");
        Assert.Contains("1 entry.", recent.Output, StringComparison.Ordinal);
        Assert.Contains("purge-all", recent.Output, StringComparison.Ordinal);
        Assert.Contains("5 entries.", Run(harness.Time, "audit", harness.Inbox.Root, "--since", "2026-11-03").Output, StringComparison.Ordinal);
        Assert.Contains("0 entries.", Run(harness.Time, "audit", harness.Inbox.Root, "--since", "2026-11-04T00:00:00Z").Output, StringComparison.Ordinal);

        var json = Run(harness.Time, "audit", harness.Inbox.Root, "--json");
        string[] lines = json.Output.TrimEnd().ReplaceLineEndings("\n").Split('\n');
        Assert.Equal(5, lines.Length);
        Assert.Equal(AuditLog.Read(harness.Audit.Path).Select(e => e.To), lines.Select(l => JsonDocument.Parse(l).RootElement.GetProperty("to").GetString()));
        Assert.Equal(File.ReadAllLines(harness.Audit.Path), lines);
    }

    [Fact]
    public void AnOutboxFolderMeansItsAuditJsonl()
    {
        using var temp = new TempDirectory();
        var time = new ManualTime(Samples.Start);
        var outbox = new Outbox(temp.Combine("outbox"), time: time);
        outbox.Enqueue(Samples.Draft(time), "inbox");
        Assert.Contains("- -> queued", Run(time, "audit", temp.Combine("outbox")).Output, StringComparison.Ordinal);
    }

    [Fact]
    public void LinesThatAreNotEntriesAreSkippedAndCounted()
    {
        using var temp = new TempDirectory();
        string log = temp.Combine("audit.jsonl");
        File.WriteAllText(log, """
            {"time":"2026-11-03T15:04:05.123Z","to":"blocked","rule":"keepass"}
            {"time":"2026-11-03T15:04:06.123Z","to":"queued","window_title":"should never be shown"}
            {"time":"2026-11-0
            """);
        var (exit, output, error) = Run(new ManualTime(Samples.Start), "audit", log);
        Assert.Equal(Cli.Ok, exit);
        Assert.Contains("1 entry.", output, StringComparison.Ordinal);
        Assert.DoesNotContain("should never be shown", output, StringComparison.Ordinal);
        Assert.Contains("2 lines of the log skipped", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AFolderWithoutALogFails()
    {
        using var temp = new TempDirectory();
        Assert.Equal(Cli.Failed, Run(TimeProvider.System, "audit", temp.Path).Exit);
        Assert.Equal(Cli.Failed, Run(TimeProvider.System, "audit", temp.Combine("missing")).Exit);
    }

    [Theory]
    [InlineData("audit")]
    [InlineData("audit", "a", "b")]
    [InlineData("audit", "a", "--packet", "x")]
    [InlineData("audit", "a", "--since", "yesterday")]
    [InlineData("audit", "a", "--since")]
    [InlineData("audit", "a", "--table")]
    public void AuditUsageErrorsExitWithTwo(params string[] args) => Assert.Equal(Cli.Usage, Run(TimeProvider.System, args).Exit);
}
