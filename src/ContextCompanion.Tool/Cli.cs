using ContextCompanion.Core.Packets;

namespace ContextCompanion.Tool;

/// <summary>
/// The <c>ccomp</c> command line. Output names packets, files and problems only; it never prints
/// payload content or source details such as window titles.
/// </summary>
public static class Cli
{
    public const int Ok = 0, Failed = 1, Usage = 2;

    private const string Help = """
        usage:
          ccomp validate <inbox-or-packet>
              Validate one packet folder, or every packet folder in an inbox.
              Exit code 0 if all are valid, 1 if any is invalid.
          ccomp ack <packet> --consumer <name> [--note <text>]
              Write ack.json into a valid packet to record that <name> consumed it.
              An acknowledgement means taken, not read or answered.
          ccomp --help
        """;

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0) { error.WriteLine(Help); return Usage; }
        try
        {
            return args[0] switch
            {
                "-h" or "--help" or "help" => PrintHelp(output),
                "validate" => Validate(args[1..], output, error),
                "ack" => Ack(args[1..], output, error),
                _ => UsageError(error, $"unknown command '{args[0]}'"),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"ccomp: {ex.Message}");
            return Failed;
        }
    }

    private static int PrintHelp(TextWriter output) { output.WriteLine(Help); return Ok; }

    private static int UsageError(TextWriter error, string message)
    {
        error.WriteLine($"ccomp: {message}. Run 'ccomp --help'.");
        return Usage;
    }

    private static int Validate(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length != 1 || args[0].StartsWith('-')) return UsageError(error, "validate takes exactly one path");
        string path = Path.GetFullPath(args[0]);
        if (!Directory.Exists(path)) { error.WriteLine($"ccomp: '{args[0]}' is not a folder."); return Failed; }

        if (PacketReader.IsPacketDirectory(path))
            return Report(PacketValidator.ValidateDirectory(path), Path.GetFileName(path), output) ? Ok : Failed;

        int checkedCount = 0, invalid = 0;
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            if (entry.Name.StartsWith('.')) continue; // the staging folder and hidden entries are not packets
            if (entry is not DirectoryInfo || !Ulid.IsValid(entry.Name))
            {
                output.WriteLine($"SKIP    {entry.Name}: not a packet folder");
                continue;
            }
            checkedCount++;
            if (!Report(PacketValidator.ValidateDirectory(entry.FullName), entry.Name, output)) invalid++;
        }
        output.WriteLine($"{checkedCount} packet{(checkedCount == 1 ? "" : "s")} checked, {invalid} invalid.");
        return invalid == 0 ? Ok : Failed;
    }

    private static bool Report(PacketValidationResult result, string name, TextWriter output)
    {
        if (result.IsValid && result.Manifest is { } manifest)
        {
            string ack = result.Acknowledgement is { } a ? $", acknowledged by {a.Consumer}" : "";
            output.WriteLine($"OK      {manifest.Id}: {manifest.Items.Count} item{(manifest.Items.Count == 1 ? "" : "s")}, {manifest.TotalBytes} bytes{ack}");
            return true;
        }
        output.WriteLine($"INVALID {name}");
        foreach (var problem in result.Problems)
            output.WriteLine($"  - {problem.Kind}: {problem.Message}");
        return false;
    }

    private static int Ack(string[] args, TextWriter output, TextWriter error)
    {
        string? packet = null, consumer = null, note = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--consumer" when i + 1 < args.Length: consumer = args[++i]; break;
                case "--note" when i + 1 < args.Length: note = args[++i]; break;
                case var a when a.StartsWith('-'): return UsageError(error, $"unknown or incomplete option '{a}'");
                case var a when packet is null: packet = a; break;
                default: return UsageError(error, "ack takes one packet folder");
            }
        }
        if (packet is null) return UsageError(error, "ack needs a packet folder");
        if (consumer is null) return UsageError(error, "ack needs --consumer <name>");
        if (!PacketNames.IsValidAgentName(consumer))
            return UsageError(error, "--consumer must be 1 to 64 characters from A-Z, a-z, 0-9, '.', '_' and '-'");
        if (note is not null && note.Length > PacketFormat.MaxNoteLength)
            return UsageError(error, $"--note must be at most {PacketFormat.MaxNoteLength} characters");

        try
        {
            var ack = PacketAcknowledgements.Write(packet, consumer, note);
            output.WriteLine($"Acknowledged {ack.Id} as {ack.Consumer} at {PacketJson.FormatTimestamp(ack.Consumed)}.");
            return Ok;
        }
        catch (InvalidPacketException ex)
        {
            error.WriteLine($"ccomp: not acknowledged, the packet is invalid:");
            foreach (var problem in ex.Problems) error.WriteLine($"  - {problem.Kind}: {problem.Message}");
            return Failed;
        }
        catch (ArgumentException ex)
        {
            error.WriteLine($"ccomp: {ex.Message}");
            return Failed;
        }
    }
}
