using System.Diagnostics;
using System.Text.Json;
using ContextCompanion.Core.Packets;

namespace ContextCompanion.Core.Tests;

/// <summary>Finds a Python 3 interpreter for the reference consumer, or says why there is none.</summary>
internal static class Python
{
    private static readonly Lazy<string?> Found = new(() =>
    {
        foreach (string candidate in OperatingSystem.IsWindows() ? new[] { "python", "py" } : new[] { "python3", "python" })
        {
            try
            {
                var (code, output, _) = Run(candidate, ["--version"], TimeSpan.FromSeconds(10));
                if (code == 0 && output.StartsWith("Python 3.", StringComparison.Ordinal)) return candidate;
            }
            catch (System.ComponentModel.Win32Exception) { }
        }
        return null;
    });

    public static string? Executable => Found.Value;

    public static (int Code, string Output, string Error) Run(string exe, IEnumerable<string> args, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout)) { process.Kill(true); throw new TimeoutException($"{exe} did not finish."); }
        return (process.ExitCode, output.Result, error.Result);
    }
}

public sealed class PythonFactAttribute : FactAttribute
{
    public PythonFactAttribute()
    {
        if (Python.Executable is null) Skip = "No Python 3 interpreter on PATH; the reference consumer cannot run here.";
    }
}

public class ReferenceConsumerTests
{
    private static string Script()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ContextCompanion.sln"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found."), "examples", "consumer", "read_packets.py");
    }

    private static (int Code, string Output, string Error) Consumer(params string[] args) =>
        Python.Run(Python.Executable!, [Script(), .. args], TimeSpan.FromSeconds(60));

    [PythonFact]
    public void ItReadsAPacketAndWritesAnAcknowledgementTheValidatorAccepts()
    {
        using var temp = new TempDirectory();
        string inbox = temp.Combine("inbox");
        string packet = Samples.WritePacket(inbox);
        string id = Path.GetFileName(packet);

        var (code, output, error) = Consumer(inbox, "--ack", "reference-consumer", "--json");
        Assert.True(code == 0, error);
        using var line = JsonDocument.Parse(output.Trim());
        Assert.Equal(id, line.RootElement.GetProperty("id").GetString());
        Assert.True(line.RootElement.GetProperty("acknowledged").GetBoolean());
        Assert.Empty(line.RootElement.GetProperty("problems").EnumerateArray());

        var ack = PacketAcknowledgements.Read(packet);
        Assert.Equal("reference-consumer", ack!.Consumer);
        Assert.True(PacketValidator.ValidateDirectory(packet).IsValid);

        // Acknowledged packets are no longer listed, and an acknowledgement is never replaced.
        var second = Consumer(inbox, "--ack", "someone-else", "--json");
        Assert.Equal(0, second.Code);
        Assert.Equal("", second.Output.Trim());
        Assert.Equal("reference-consumer", PacketAcknowledgements.Read(packet)!.Consumer);
    }

    [PythonFact]
    public void AChangedItemIsReportedAndNotAcknowledged()
    {
        using var temp = new TempDirectory();
        string inbox = temp.Combine("inbox");
        string packet = Samples.WritePacket(inbox);
        string file = Directory.EnumerateFiles(packet).First(f => !f.EndsWith("manifest.json", StringComparison.Ordinal));
        File.AppendAllText(file, "x");

        var (code, output, _) = Consumer(inbox, "--ack", "reference-consumer");
        Assert.Equal(1, code);
        Assert.Contains("INVALID", output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(packet, PacketFormat.AckFileName)));
    }
}
