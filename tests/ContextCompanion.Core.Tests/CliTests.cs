using ContextCompanion.Core.Packets;
using ContextCompanion.Tool;

namespace ContextCompanion.Core.Tests;

public class CliTests
{
    private static (int Exit, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exit = Cli.Run(args, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public void ValidateAcceptsAValidPacketFolder()
    {
        using var temp = new TempDirectory();
        string packet = Samples.WritePacket(temp.Path);
        var (exit, output, _) = Run("validate", packet);
        Assert.Equal(Cli.Ok, exit);
        Assert.Contains($"OK      {Path.GetFileName(packet)}: 2 items", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateChecksEveryPacketInAnInboxAndNeverPrintsPayloadOrTitles()
    {
        using var temp = new TempDirectory();
        var time = new ManualTime(Samples.Start);
        string good = Samples.WritePacket(temp.Path, time);
        time.Advance(TimeSpan.FromSeconds(1));
        string bad = Samples.WritePacket(temp.Path, time, variant: 1);
        File.WriteAllText(Path.Combine(bad, "extra.txt"), "x");
        File.WriteAllText(temp.Combine("README.txt"), "not a packet");
        Directory.CreateDirectory(temp.Combine(".tmp", "leftover"));

        var (exit, output, _) = Run("validate", temp.Path);

        Assert.Equal(Cli.Failed, exit);
        Assert.Contains($"OK      {Path.GetFileName(good)}", output, StringComparison.Ordinal);
        Assert.Contains($"INVALID {Path.GetFileName(bad)}", output, StringComparison.Ordinal);
        Assert.Contains("ExtraFile: 'extra.txt' is not listed in the manifest", output, StringComparison.Ordinal);
        Assert.Contains("SKIP    README.txt", output, StringComparison.Ordinal);
        Assert.Contains("2 packets checked, 1 invalid.", output, StringComparison.Ordinal);
        Assert.DoesNotContain(Samples.SecretWindowTitle, output, StringComparison.Ordinal);
        Assert.DoesNotContain(Samples.SecretText, output, StringComparison.Ordinal);
    }

    [Fact]
    public void AckWritesAcknowledgementThatValidateReports()
    {
        using var temp = new TempDirectory();
        string packet = Samples.WritePacket(temp.Path);

        var (exit, output, _) = Run("ack", packet, "--consumer", "reference-consumer", "--note", "taken");
        Assert.Equal(Cli.Ok, exit);
        Assert.StartsWith($"Acknowledged {Path.GetFileName(packet)} as reference-consumer at ", output, StringComparison.Ordinal);
        Assert.Equal("taken", PacketAcknowledgements.Read(packet)!.Note);

        var validate = Run("validate", packet);
        Assert.Equal(Cli.Ok, validate.Exit);
        Assert.Contains("acknowledged by reference-consumer", validate.Output, StringComparison.Ordinal);

        var again = Run("ack", packet, "--consumer", "other");
        Assert.Equal(Cli.Failed, again.Exit);
        Assert.Contains("already acknowledged", again.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void AckRefusesAnInvalidPacket()
    {
        using var temp = new TempDirectory();
        string packet = Samples.WritePacket(temp.Path);
        File.Delete(Path.Combine(packet, "selection.txt"));
        var (exit, _, error) = Run("ack", packet, "--consumer", "c");
        Assert.Equal(Cli.Failed, exit);
        Assert.Contains("MissingFile", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData]
    [InlineData("frobnicate")]
    [InlineData("validate")]
    [InlineData("validate", "a", "b")]
    [InlineData("ack")]
    [InlineData("ack", "packet")]
    [InlineData("ack", "packet", "--consumer")]
    [InlineData("ack", "packet", "--consumer", "bad name")]
    [InlineData("ack", "packet", "--consumer", "c", "--force")]
    public void UsageErrorsExitWithTwo(params string[] args) => Assert.Equal(Cli.Usage, Run(args).Exit);

    [Fact]
    public void ValidateOfAMissingFolderFails()
    {
        using var temp = new TempDirectory();
        Assert.Equal(Cli.Failed, Run("validate", temp.Combine("missing")).Exit);
    }
}
