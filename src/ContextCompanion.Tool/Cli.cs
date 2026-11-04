using ContextCompanion.Core.Delivery;
using ContextCompanion.Core.Packets;
using ContextCompanion.Core.Privacy;
#if WINDOWS
using ContextCompanion.Platform.Windows;
#endif

namespace ContextCompanion.Tool;

/// <summary>What <c>ccomp</c> uses from its surroundings; tests replace it.</summary>
public sealed class CliEnvironment
{
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Decryptors available to <c>open</c> and <c>validate</c>: DPAPI in the Windows build on Windows, none elsewhere.</summary>
    public IReadOnlyList<IPayloadProtector> Protectors { get; init; } = DefaultProtectors();

    private static IReadOnlyList<IPayloadProtector> DefaultProtectors()
    {
#if WINDOWS
        if (OperatingSystem.IsWindows()) return [new DpapiCurrentUserProtector()];
#endif
        return [];
    }
}

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
          ccomp open <packet> --to <dir>
              Write a plaintext copy of a packet to <dir>/<id>/, decrypting encrypted items
              and verifying every plaintext size and SHA-256. DPAPI-encrypted packets can be
              opened only on Windows, by the user account that created them.
          ccomp purge <inbox> [--all | --older-than <duration> | --id <id>] [--yes]
                [--outbox <dir>] [--audit <log>] [--settings <privacy.json>]
              Remove packets from an inbox. Without a selector, apply the retention limits
              (from --settings, or the defaults). Durations look like 90m, 12h, 7d or 2w.
              A dry run unless --yes is given. Packets that the outbox (--outbox) still has
              as queued or delivering are never removed. Each removal is written to the audit
              log (default <inbox>/.audit.jsonl) as the packet id and a reason.
              Deleting files does not guarantee the bytes are unrecoverable.
          ccomp audit <inbox-or-log> [--since <time>] [--packet <id>] [--json]
              Show an audit log as a table: time, packet, destination, state change, reason.
              A folder means its audit.jsonl (outbox) or .audit.jsonl (inbox). --since takes
              a UTC time such as 2026-11-03T15:00:00Z, a date, or a duration such as 2h.
              The log holds ids, states, hashes, sizes and rule or reason names; never content.
          ccomp --help
        """;

    public static int Run(string[] args, TextWriter output, TextWriter error, CliEnvironment? environment = null)
    {
        environment ??= new CliEnvironment();
        if (args.Length == 0) { error.WriteLine(Help); return Usage; }
        try
        {
            return args[0] switch
            {
                "-h" or "--help" or "help" => PrintHelp(output),
                "validate" => Validate(args[1..], output, error, environment),
                "open" => Open(args[1..], output, error, environment),
                "ack" => Ack(args[1..], output, error),
                "purge" => Purge(args[1..], output, error, environment),
                "audit" => Audit(args[1..], output, error, environment),
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

    private static int Validate(string[] args, TextWriter output, TextWriter error, CliEnvironment environment)
    {
        if (args.Length != 1 || args[0].StartsWith('-')) return UsageError(error, "validate takes exactly one path");
        string path = Path.GetFullPath(args[0]);
        if (!Directory.Exists(path)) { error.WriteLine($"ccomp: '{args[0]}' is not a folder."); return Failed; }

        if (PacketReader.IsPacketDirectory(path))
            return Report(PacketValidator.ValidateDirectory(path, environment.Protectors), Path.GetFileName(path), output) ? Ok : Failed;

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
            if (!Report(PacketValidator.ValidateDirectory(entry.FullName, environment.Protectors), entry.Name, output)) invalid++;
        }
        output.WriteLine($"{checkedCount} packet{(checkedCount == 1 ? "" : "s")} checked, {invalid} invalid.");
        return invalid == 0 ? Ok : Failed;
    }

    private static bool Report(PacketValidationResult result, string name, TextWriter output)
    {
        if (result.IsValid && result.Manifest is { } manifest)
        {
            string ack = result.Acknowledgement is { } a ? $", acknowledged by {a.Consumer}" : "";
            int encrypted = manifest.Items.Count(i => i.Stored is not null);
            string protection = encrypted == 0 ? ""
                : result.PlaintextUnchecked > 0 ? $", {encrypted} encrypted (stored form checked; plaintext not checked here)"
                : $", {encrypted} encrypted";
            output.WriteLine($"OK      {manifest.Id}: {manifest.Items.Count} item{(manifest.Items.Count == 1 ? "" : "s")}, {manifest.TotalBytes} bytes{protection}{ack}");
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

    private static int Purge(string[] args, TextWriter output, TextWriter error, CliEnvironment environment)
    {
        string? inbox = null, outbox = null, auditPath = null, settingsPath = null, id = null;
        TimeSpan? olderThan = null;
        bool all = false, yes = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--all": all = true; break;
                case "--yes": yes = true; break;
                case "--older-than" when i + 1 < args.Length:
                    if (!Duration.TryParse(args[++i], out var age)) return UsageError(error, "--older-than takes a duration such as 12h, 7d or 2w");
                    olderThan = age;
                    break;
                case "--id" when i + 1 < args.Length:
                    id = args[++i];
                    if (!Ulid.IsValid(id)) return UsageError(error, "--id takes a packet id (a canonical ULID)");
                    break;
                case "--outbox" when i + 1 < args.Length: outbox = args[++i]; break;
                case "--audit" when i + 1 < args.Length: auditPath = args[++i]; break;
                case "--settings" when i + 1 < args.Length: settingsPath = args[++i]; break;
                case var a when a.StartsWith('-'): return UsageError(error, $"unknown or incomplete option '{a}'");
                case var a when inbox is null: inbox = a; break;
                default: return UsageError(error, "purge takes one inbox folder");
            }
        }
        if (inbox is null) return UsageError(error, "purge needs an inbox folder");
        int selectors = (all ? 1 : 0) + (olderThan is null ? 0 : 1) + (id is null ? 0 : 1);
        if (selectors > 1) return UsageError(error, "use only one of --all, --older-than and --id");
        if (settingsPath is not null && selectors > 0) return UsageError(error, "--settings applies only without --all, --older-than or --id");
        string root = Path.GetFullPath(inbox);
        if (!Directory.Exists(root)) { error.WriteLine($"ccomp: '{inbox}' is not a folder."); return Failed; }

        RetentionLimits limits;
        try { limits = (settingsPath is null ? PrivacySettings.Default : PrivacySettings.LoadOrDefault(settingsPath)).Retention.ToLimits(); }
        catch (PrivacySettingsException ex) { error.WriteLine($"ccomp: {ex.Message}"); return Failed; }

        Func<string, DeliveryState?>? stateOf = null;
        if (outbox is not null)
        {
            if (!Directory.Exists(outbox)) { error.WriteLine($"ccomp: '{outbox}' is not a folder."); return Failed; }
            var states = Outbox.ReadRecords(outbox).ToDictionary(r => r.Id, r => r.State, StringComparer.Ordinal);
            stateOf = packet => states.TryGetValue(packet, out var state) ? state : null;
        }

        var kind = all ? PurgeKind.All : olderThan is not null ? PurgeKind.OlderThan : id is not null ? PurgeKind.Id : PurgeKind.Retention;
        IReadOnlyList<PacketRemoval> plan;
        try { plan = InboxRetention.PlanPurge(root, kind, environment.Time.GetUtcNow(), olderThan, id, limits, stateOf); }
        catch (InvalidOperationException ex) { error.WriteLine($"ccomp: {ex.Message}"); return Failed; }

        if (plan.Count == 0) { output.WriteLine("Nothing to remove."); return Ok; }
        if (!yes)
        {
            foreach (var removal in plan) output.WriteLine($"Would remove {removal.Id}: {removal.Reason}, {removal.Bytes} bytes");
            output.WriteLine($"{Plural(plan.Count, "packet")}, {plan.Sum(r => r.Bytes)} bytes would be removed. Run again with --yes to delete.");
            return Ok;
        }

        var audit = new AuditLog(auditPath ?? InboxRetention.DefaultAuditPath(root), environment.Time);
        var done = InboxRetention.Remove(root, plan, audit);
        foreach (var removal in done) output.WriteLine($"Removed {removal.Id}: {removal.Reason}, {removal.Bytes} bytes");
        foreach (var skipped in plan.Except(done)) error.WriteLine($"ccomp: could not remove {skipped.Id}; it may be open in another program.");
        output.WriteLine($"{Plural(done.Count, "packet")} removed, {done.Sum(r => r.Bytes)} bytes. " +
            "Deleted files may still be recoverable from the disk or from backups; see PRIVACY.md.");
        return done.Count == plan.Count ? Ok : Failed;
    }

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    private static int Open(string[] args, TextWriter output, TextWriter error, CliEnvironment environment)
    {
        string? packet = null, target = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--to" when i + 1 < args.Length: target = args[++i]; break;
                case var a when a.StartsWith('-'): return UsageError(error, $"unknown or incomplete option '{a}'");
                case var a when packet is null: packet = a; break;
                default: return UsageError(error, "open takes one packet folder");
            }
        }
        if (packet is null) return UsageError(error, "open needs a packet folder");
        if (target is null) return UsageError(error, "open needs --to <dir>");

        var result = PacketValidator.ValidateDirectory(packet, environment.Protectors);
        if (!result.IsValid || result.Manifest is null)
        {
            error.WriteLine("ccomp: not opened, the packet is invalid:");
            foreach (var problem in result.Problems) error.WriteLine($"  - {problem.Kind}: {problem.Message}");
            return Failed;
        }
        var manifest = result.Manifest;
        PacketDraft plain;
        try
        {
            plain = PacketProtection.Unprotect(new Packet(result.Directory, manifest, result.Acknowledgement), environment.Protectors);
        }
        catch (MissingProtectorException ex) when (ex.Protection == PacketFormat.DpapiCurrentUser && !OperatingSystem.IsWindows())
        {
            error.WriteLine($"ccomp: packet {manifest.Id} is encrypted with Windows DPAPI ({ex.Protection}). " +
                "It can be decrypted only on Windows, by the user account that created it. Nothing was written.");
            return Failed;
        }
        catch (MissingProtectorException ex)
        {
            string hint = ex.Protection == PacketFormat.DpapiCurrentUser ? " Use the Windows build of ccomp (net10.0-windows)." : "";
            error.WriteLine($"ccomp: packet {manifest.Id} is encrypted with {ex.Protection}, which this build of ccomp cannot decrypt.{hint} Nothing was written.");
            return Failed;
        }
        catch (InvalidPacketException ex)
        {
            error.WriteLine("ccomp: not opened:");
            foreach (var problem in ex.Problems) error.WriteLine($"  - {problem.Kind}: {problem.Message}");
            return Failed;
        }

        string written = new InboxWriter(target).Write(plain);
        int encrypted = manifest.Items.Count(i => i.Stored is not null);
        output.WriteLine($"Opened {manifest.Id} into {written}: {Plural(plain.Manifest.Items.Count, "item")}, {plain.TotalBytes} bytes, " +
            $"{encrypted} decrypted, plaintext sizes and SHA-256 verified.");
        return Ok;
    }

    private static int Audit(string[] args, TextWriter output, TextWriter error, CliEnvironment environment)
    {
        string? path = null, packet = null;
        DateTimeOffset? since = null;
        bool json = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--json": json = true; break;
                case "--packet" when i + 1 < args.Length:
                    packet = args[++i];
                    if (!Ulid.IsValid(packet)) return UsageError(error, "--packet takes a packet id (a canonical ULID)");
                    break;
                case "--since" when i + 1 < args.Length:
                    since = ParseSince(args[++i], environment.Time.GetUtcNow());
                    if (since is null) return UsageError(error, "--since takes a UTC time such as 2026-11-03T15:00:00Z, a date such as 2026-11-03, or a duration such as 2h");
                    break;
                case var a when a.StartsWith('-'): return UsageError(error, $"unknown or incomplete option '{a}'");
                case var a when path is null: path = a; break;
                default: return UsageError(error, "audit takes one inbox, outbox or log file");
            }
        }
        if (path is null) return UsageError(error, "audit needs an inbox, an outbox or a log file");

        string? log = File.Exists(path) ? path
            : Directory.Exists(path) ? new[] { Outbox.AuditFileName, InboxRetention.AuditFileName }.Select(n => Path.Combine(path, n)).FirstOrDefault(File.Exists)
            : null;
        if (log is null)
        {
            error.WriteLine(Directory.Exists(path)
                ? $"ccomp: '{path}' holds no audit log ({Outbox.AuditFileName} or {InboxRetention.AuditFileName})."
                : $"ccomp: '{path}' does not exist.");
            return Failed;
        }

        var entries = AuditLog.Read(log, out int skipped)
            .Where(e => since is null || e.Time >= since)
            .Where(e => packet is null || e.Packet == packet)
            .ToArray();
        if (json)
        {
            foreach (var entry in entries) output.WriteLine(System.Text.Json.JsonSerializer.Serialize(entry, PacketJson.Compact));
        }
        else
        {
            string[] header = ["TIME", "PACKET", "DESTINATION", "CHANGE", "REASON"];
            var rows = entries.Select(e => new[]
            {
                PacketJson.FormatTimestamp(e.Time),
                e.Packet ?? "-",
                e.Destination ?? "-",
                $"{e.From ?? "-"} -> {e.To}",
                e.Reason ?? (e.Rule is not null ? $"denylist rule {e.Rule}" : e.Consumer is not null ? $"consumer {e.Consumer}" : ""),
            }).ToList();
            int[] widths = header.Select((h, c) => rows.Select(r => r[c].Length).Prepend(h.Length).Max()).ToArray();
            foreach (var row in rows.Prepend(header))
                output.WriteLine(string.Join("  ", row.Select((cell, c) => c == row.Length - 1 ? cell : cell.PadRight(widths[c]))).TrimEnd());
            output.WriteLine(entries.Length == 1 ? "1 entry." : $"{entries.Length} entries.");
        }
        if (skipped > 0) error.WriteLine($"ccomp: {Plural(skipped, "line")} of the log skipped: not valid audit entries.");
        return Ok;
    }

    private static DateTimeOffset? ParseSince(string text, DateTimeOffset now)
    {
        if (PacketJson.TryParseTimestamp(text, out var time)) return time;
        if (DateTimeOffset.TryParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var date))
            return date;
        return Duration.TryParse(text, out var ago) ? now - ago : null;
    }
}
