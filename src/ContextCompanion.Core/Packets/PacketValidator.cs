using System.Security.Cryptography;
using System.Text.Json;

namespace ContextCompanion.Core.Packets;

/// <summary>Kinds of validation problem. The tests and <c>ccomp validate</c> rely on these names.</summary>
public enum PacketProblemKind
{
    /// <summary>The packet folder or its manifest is missing, unreadable, too large or not JSON.</summary>
    Manifest,
    /// <summary>The manifest does not follow schema/packet.schema.json.</summary>
    Schema,
    /// <summary>An item file name is unsafe or duplicated.</summary>
    FileName,
    /// <summary>A listed item file does not exist.</summary>
    MissingFile,
    /// <summary>The folder holds a file, folder or link the manifest does not list.</summary>
    ExtraFile,
    /// <summary>A file's size differs from the manifest, or a size limit is exceeded.</summary>
    Size,
    /// <summary>A file's SHA-256 differs from the manifest.</summary>
    Hash,
    /// <summary>The folder name differs from the manifest id.</summary>
    Identity,
    /// <summary><c>ack.json</c> is present but invalid.</summary>
    Acknowledgement,
}

public sealed record PacketProblem(PacketProblemKind Kind, string Message)
{
    public override string ToString() => $"{Kind}: {Message}";
}

public sealed class PacketValidationResult
{
    internal PacketValidationResult(string directory, IReadOnlyList<PacketProblem> problems,
        PacketManifest? manifest, PacketAcknowledgement? acknowledgement)
    {
        Directory = directory; Problems = problems; Manifest = manifest; Acknowledgement = acknowledgement;
    }

    public string Directory { get; }
    public IReadOnlyList<PacketProblem> Problems { get; }
    public bool IsValid => Problems.Count == 0;
    /// <summary>The manifest, when it passed the schema and file-name rules (the files may still be wrong).</summary>
    public PacketManifest? Manifest { get; }
    public PacketAcknowledgement? Acknowledgement { get; }
}

/// <summary>
/// Validates packet folders against packet format v1: the manifest schema, item file names,
/// the exact set of files, sizes and SHA-256 hashes, and an optional <c>ack.json</c>.
/// Implements the rules of schema/packet.schema.json in code, without a JSON Schema library.
/// </summary>
public static class PacketValidator
{
    internal static readonly HashSet<string> ManifestKeys = ["format", "schema_version", "id", "created", "items", "source", "redactions"];
    internal static readonly HashSet<string> ManifestRequired = ["format", "schema_version", "id", "created", "items", "redactions"];
    internal static readonly HashSet<string> ItemKeys = ["kind", "file", "media_type", "bytes", "sha256"];
    internal static readonly HashSet<string> SourceKeys = ["process_name", "window_class", "window_title", "executable_path", "capture_mode", "region", "scale"];
    internal static readonly HashSet<string> RegionKeys = ["x", "y", "width", "height"];
    internal static readonly HashSet<string> RedactionKeys = ["kind", "reason", "item", "count"];
    internal static readonly HashSet<string> RedactionRequired = ["kind", "reason"];
    internal static readonly HashSet<string> AckKeys = ["format", "schema_version", "id", "consumer", "consumed", "note"];
    internal static readonly HashSet<string> AckRequired = ["format", "schema_version", "id", "consumer", "consumed"];
    private static readonly Dictionary<string, PacketItemKind> Kinds = new(StringComparer.Ordinal)
    {
        ["image"] = PacketItemKind.Image, ["text"] = PacketItemKind.Text,
        ["audio"] = PacketItemKind.Audio, ["note"] = PacketItemKind.Note,
    };

    /// <summary>Validates a packet folder, including every file's size and hash.</summary>
    public static PacketValidationResult ValidateDirectory(string packetDirectory)
    {
        string directory = Path.GetFullPath(packetDirectory);
        var problems = new List<PacketProblem>();
        var info = new DirectoryInfo(directory);
        if (!info.Exists)
            return Fail(PacketProblemKind.Manifest, "packet folder does not exist");
        if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return Fail(PacketProblemKind.Manifest, "packet folder is a link");

        var manifestFile = new FileInfo(Path.Combine(directory, PacketFormat.ManifestFileName));
        if (!manifestFile.Exists)
            return Fail(PacketProblemKind.Manifest, "manifest.json is missing");
        if (manifestFile.LinkTarget is not null)
            return Fail(PacketProblemKind.Manifest, "manifest.json is a link");
        if (manifestFile.Length > PacketFormat.MaxManifestBytes)
            return Fail(PacketProblemKind.Manifest, $"manifest.json is larger than {PacketFormat.MaxManifestBytes} bytes");

        PacketManifest? manifest;
        try
        {
            byte[] json = File.ReadAllBytes(manifestFile.FullName);
            using var document = JsonDocument.Parse(json, PacketJson.DocumentOptions);
            problems.AddRange(ValidateManifest(document.RootElement));
            manifest = problems.Count == 0 ? document.RootElement.Deserialize<PacketManifest>(PacketJson.Options) : null;
        }
        catch (JsonException ex)
        {
            return Fail(PacketProblemKind.Manifest, "manifest.json is not valid JSON: " + ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(PacketProblemKind.Manifest, "manifest.json cannot be read: " + ex.Message);
        }
        if (manifest is null)
            return new(directory, problems, null, null);

        if (!string.Equals(info.Name, manifest.Id, StringComparison.Ordinal))
            problems.Add(new(PacketProblemKind.Identity, $"folder name '{info.Name}' differs from manifest id '{manifest.Id}'"));

        var listed = manifest.Items.ToDictionary(i => i.File, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        FileInfo? ackFile = null;
        try
        {
            foreach (var entry in info.EnumerateFileSystemInfos())
            {
                seen.Add(entry.Name);
                if (entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    problems.Add(new(PacketProblemKind.ExtraFile, $"'{entry.Name}' is a link; packets hold only plain files"));
                    continue;
                }
                if (entry is DirectoryInfo)
                {
                    problems.Add(new(PacketProblemKind.ExtraFile, $"'{entry.Name}' is a folder; packets hold only plain files"));
                    continue;
                }
                if (entry.Name == PacketFormat.ManifestFileName) continue;
                if (entry.Name == PacketFormat.AckFileName) { ackFile = (FileInfo)entry; continue; }
                if (!listed.ContainsKey(entry.Name))
                {
                    problems.Add(new(PacketProblemKind.ExtraFile, $"'{entry.Name}' is not listed in the manifest"));
                    continue;
                }
                CheckItemFile((FileInfo)entry, listed[entry.Name], problems);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add(new(PacketProblemKind.Manifest, "packet folder cannot be listed: " + ex.Message));
        }

        foreach (var item in manifest.Items)
            if (!seen.Contains(item.File))
                problems.Add(new(PacketProblemKind.MissingFile, $"'{item.File}' is listed but missing"));

        PacketAcknowledgement? ack = null;
        if (ackFile is not null)
        {
            var ackProblems = new List<string>();
            ack = ReadAcknowledgementFile(ackFile.FullName, manifest.Id, ackProblems);
            problems.AddRange(ackProblems.Select(m => new PacketProblem(PacketProblemKind.Acknowledgement, m)));
        }
        return new(directory, problems, manifest, ack);

        PacketValidationResult Fail(PacketProblemKind kind, string message) =>
            new(directory, [new PacketProblem(kind, message)], null, null);
    }

    private static void CheckItemFile(FileInfo file, PacketItem item, List<PacketProblem> problems)
    {
        try
        {
            if (file.Length != item.Bytes)
            {
                problems.Add(new(PacketProblemKind.Size, $"'{item.File}' has {file.Length} bytes, manifest says {item.Bytes}"));
                return;
            }
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
            string hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (stream.Length != item.Bytes)
                problems.Add(new(PacketProblemKind.Size, $"'{item.File}' changed size while being read"));
            else if (!string.Equals(hash, item.Sha256, StringComparison.Ordinal))
                problems.Add(new(PacketProblemKind.Hash, $"'{item.File}' SHA-256 differs from the manifest"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add(new(PacketProblemKind.MissingFile, $"'{item.File}' cannot be read: {ex.Message}"));
        }
    }

    /// <summary>Checks a parsed manifest against the schema and the file-name rules.</summary>
    public static IReadOnlyList<PacketProblem> ValidateManifest(JsonElement root)
    {
        var problems = new List<PacketProblem>();
        void Schema(string message) => problems.Add(new(PacketProblemKind.Schema, message));

        if (!CheckObject(root, "manifest", ManifestKeys, ManifestRequired, Schema)) return problems;

        if (root.TryGetProperty("format", out var format) &&
            (format.ValueKind != JsonValueKind.String || format.GetString() != PacketFormat.FormatName))
            Schema($"format must be \"{PacketFormat.FormatName}\"");
        if (root.TryGetProperty("schema_version", out var version) &&
            (!TryGetInteger(version, out long v) || v != PacketFormat.SchemaVersion))
            Schema($"schema_version must be {PacketFormat.SchemaVersion}");
        if (root.TryGetProperty("id", out var id) && (id.ValueKind != JsonValueKind.String || !Ulid.IsValid(id.GetString())))
            Schema("id must be a canonical upper-case ULID");
        if (root.TryGetProperty("created", out var created) &&
            (created.ValueKind != JsonValueKind.String || !PacketJson.TryParseTimestamp(created.GetString(), out _)))
            Schema("created must be a UTC ISO 8601 timestamp ending in 'Z'");

        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("items", out var items))
        {
            if (items.ValueKind != JsonValueKind.Array) Schema("items must be an array");
            else
            {
                int count = items.GetArrayLength();
                if (count is < 1 or > PacketFormat.MaxItems) Schema($"items must hold 1 to {PacketFormat.MaxItems} entries");
                long total = 0;
                int index = 0;
                foreach (var item in items.EnumerateArray())
                    total += CheckItem(item, index++, fileNames, problems);
                if (total > PacketFormat.MaxPacketBytes)
                    problems.Add(new(PacketProblemKind.Size, $"items total {total} bytes, more than {PacketFormat.MaxPacketBytes}"));
            }
        }

        if (root.TryGetProperty("source", out var source)) CheckSource(source, Schema);

        if (root.TryGetProperty("redactions", out var redactions))
        {
            if (redactions.ValueKind != JsonValueKind.Array) Schema("redactions must be an array");
            else if (redactions.GetArrayLength() > PacketFormat.MaxRedactions) Schema($"redactions must hold at most {PacketFormat.MaxRedactions} entries");
            else
            {
                int index = 0;
                foreach (var redaction in redactions.EnumerateArray())
                    CheckRedaction(redaction, $"redactions[{index++}]", fileNames, Schema);
            }
        }
        return problems;
    }

    private static long CheckItem(JsonElement item, int index, HashSet<string> fileNames, List<PacketProblem> problems)
    {
        string at = $"items[{index}]";
        void Schema(string message) => problems.Add(new(PacketProblemKind.Schema, message));
        if (!CheckObject(item, at, ItemKeys, ItemKeys, Schema)) return 0;

        PacketItemKind? kind = null;
        var kindElement = item.GetProperty("kind");
        if (kindElement.ValueKind == JsonValueKind.String && Kinds.TryGetValue(kindElement.GetString()!, out var k)) kind = k;
        else Schema($"{at}.kind must be one of image, text, audio, note");

        var file = item.GetProperty("file");
        if (file.ValueKind != JsonValueKind.String) Schema($"{at}.file must be a string");
        else
        {
            string name = file.GetString()!;
            string? reason = PacketNames.CheckItemFileName(name);
            if (reason is not null) problems.Add(new(PacketProblemKind.FileName, $"{at}.file: {reason}"));
            else if (!fileNames.Add(name)) problems.Add(new(PacketProblemKind.FileName, $"{at}.file: '{name}' is listed more than once (names are compared ignoring case)"));
        }

        var media = item.GetProperty("media_type");
        if (media.ValueKind != JsonValueKind.String || !PacketNames.IsValidMediaType(media.GetString()))
            Schema($"{at}.media_type must be a lower-case media type such as image/png");
        else if (kind is { } kk && !PacketNames.MediaTypeMatchesKind(kk, media.GetString()!))
            Schema($"{at}.media_type does not match kind");

        long bytes = 0;
        if (!TryGetInteger(item.GetProperty("bytes"), out bytes) || bytes < 0)
        {
            Schema($"{at}.bytes must be a non-negative integer");
            bytes = 0;
        }
        else if (bytes > PacketFormat.MaxPacketBytes)
            problems.Add(new(PacketProblemKind.Size, $"{at}.bytes is larger than {PacketFormat.MaxPacketBytes}"));

        var sha = item.GetProperty("sha256");
        if (sha.ValueKind != JsonValueKind.String || !IsLowerHex(sha.GetString()!, 64))
            Schema($"{at}.sha256 must be 64 lower-case hexadecimal characters");
        return bytes;
    }

    private static void CheckSource(JsonElement source, Action<string> schema)
    {
        if (!CheckObject(source, "source", SourceKeys, [], schema)) return;
        if (source.TryGetProperty("process_name", out var process) &&
            !IsBoundedText(process, 1, PacketFormat.MaxProcessNameLength, allowLineBreaks: false))
            schema($"source.process_name must be 1 to {PacketFormat.MaxProcessNameLength} characters without control characters");
        if (source.TryGetProperty("window_class", out var windowClass) &&
            !IsBoundedText(windowClass, 1, PacketFormat.MaxWindowClassLength, allowLineBreaks: false))
            schema($"source.window_class must be 1 to {PacketFormat.MaxWindowClassLength} characters without control characters");
        if (source.TryGetProperty("executable_path", out var executable) &&
            !IsBoundedText(executable, 1, PacketFormat.MaxExecutablePathLength, allowLineBreaks: false))
            schema($"source.executable_path must be 1 to {PacketFormat.MaxExecutablePathLength} characters without control characters");
        if (source.TryGetProperty("window_title", out var title) &&
            !IsBoundedText(title, 0, PacketFormat.MaxWindowTitleLength, allowLineBreaks: false))
            schema($"source.window_title must be at most {PacketFormat.MaxWindowTitleLength} characters without control characters");
        if (source.TryGetProperty("capture_mode", out var mode) &&
            (mode.ValueKind != JsonValueKind.String || !PacketNames.IsValidToken(mode.GetString())))
            schema("source.capture_mode must be a lower-case token such as window or region");
        if (source.TryGetProperty("region", out var region) && CheckObject(region, "source.region", RegionKeys, RegionKeys, schema))
        {
            foreach (string key in RegionKeys)
            {
                if (!TryGetInteger(region.GetProperty(key), out long n) || n < int.MinValue || n > int.MaxValue)
                    schema($"source.region.{key} must be a 32-bit integer");
                else if (key is "width" or "height" && n < 1)
                    schema($"source.region.{key} must be at least 1");
            }
        }
        if (source.TryGetProperty("scale", out var scale) &&
            (scale.ValueKind != JsonValueKind.Number || !scale.TryGetDouble(out double s) || !(s > 0) || s > PacketFormat.MaxScale))
            schema($"source.scale must be a number greater than 0 and at most {PacketFormat.MaxScale}");
    }

    private static void CheckRedaction(JsonElement redaction, string at, HashSet<string> fileNames, Action<string> schema)
    {
        if (!CheckObject(redaction, at, RedactionKeys, RedactionRequired, schema)) return;
        var kind = redaction.GetProperty("kind");
        if (kind.ValueKind != JsonValueKind.String || !PacketNames.IsValidToken(kind.GetString()))
            schema($"{at}.kind must be a lower-case token such as secret");
        if (!IsBoundedText(redaction.GetProperty("reason"), 1, PacketFormat.MaxRedactionReasonLength, allowLineBreaks: false))
            schema($"{at}.reason must be 1 to {PacketFormat.MaxRedactionReasonLength} characters without control characters");
        if (redaction.TryGetProperty("item", out var item) &&
            (item.ValueKind != JsonValueKind.String || !fileNames.Contains(item.GetString()!)))
            schema($"{at}.item must name a file listed in items");
        if (redaction.TryGetProperty("count", out var count) && (!TryGetInteger(count, out long c) || c < 1 || c > int.MaxValue))
            schema($"{at}.count must be a positive integer");
    }

    /// <summary>Checks an <c>ack.json</c> document. <paramref name="packetId"/> is the id it must name.</summary>
    public static IReadOnlyList<string> ValidateAcknowledgement(JsonElement root, string packetId)
    {
        var problems = new List<string>();
        if (!CheckObject(root, "ack.json", AckKeys, AckRequired, problems.Add)) return problems;
        if (root.GetProperty("format") is var f && (f.ValueKind != JsonValueKind.String || f.GetString() != PacketFormat.AckFormatName))
            problems.Add($"format must be \"{PacketFormat.AckFormatName}\"");
        if (!TryGetInteger(root.GetProperty("schema_version"), out long v) || v != PacketFormat.SchemaVersion)
            problems.Add($"schema_version must be {PacketFormat.SchemaVersion}");
        var id = root.GetProperty("id");
        if (id.ValueKind != JsonValueKind.String || !Ulid.IsValid(id.GetString()))
            problems.Add("id must be a canonical upper-case ULID");
        else if (id.GetString() != packetId)
            problems.Add("id does not match the packet");
        var consumer = root.GetProperty("consumer");
        if (consumer.ValueKind != JsonValueKind.String || !PacketNames.IsValidAgentName(consumer.GetString()))
            problems.Add("consumer must be 1 to 64 characters from A-Z, a-z, 0-9, '.', '_' and '-'");
        var consumed = root.GetProperty("consumed");
        if (consumed.ValueKind != JsonValueKind.String || !PacketJson.TryParseTimestamp(consumed.GetString(), out _))
            problems.Add("consumed must be a UTC ISO 8601 timestamp ending in 'Z'");
        if (root.TryGetProperty("note", out var note) && !IsBoundedText(note, 0, PacketFormat.MaxNoteLength, allowLineBreaks: true))
            problems.Add($"note must be at most {PacketFormat.MaxNoteLength} characters");
        return problems;
    }

    internal static PacketAcknowledgement? ReadAcknowledgementFile(string path, string packetId, List<string> problems)
    {
        try
        {
            var file = new FileInfo(path);
            if (file.LinkTarget is not null) { problems.Add("ack.json is a link"); return null; }
            if (file.Length > PacketFormat.MaxAckBytes) { problems.Add($"ack.json is larger than {PacketFormat.MaxAckBytes} bytes"); return null; }
            using var document = JsonDocument.Parse(File.ReadAllBytes(path), PacketJson.DocumentOptions);
            var found = ValidateAcknowledgement(document.RootElement, packetId);
            problems.AddRange(found);
            return found.Count == 0 ? document.RootElement.Deserialize<PacketAcknowledgement>(PacketJson.Options) : null;
        }
        catch (JsonException ex) { problems.Add("ack.json is not valid JSON: " + ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problems.Add("ack.json cannot be read: " + ex.Message); }
        return null;
    }

    private static bool CheckObject(JsonElement element, string at, HashSet<string> allowed, HashSet<string> required, Action<string> report)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            report($"{at} must be an object");
            return false;
        }
        bool ok = true;
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name)) { report($"{at} has unknown property '{property.Name}'"); ok = false; }
        foreach (string name in required)
            if (!element.TryGetProperty(name, out _)) { report($"{at} is missing required property '{name}'"); ok = false; }
        return ok;
    }

    private static bool TryGetInteger(JsonElement element, out long value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value);
    }

    private static bool IsBoundedText(JsonElement element, int min, int max, bool allowLineBreaks)
    {
        if (element.ValueKind != JsonValueKind.String) return false;
        string text = element.GetString()!;
        return text.Length >= min && text.Length <= max && PacketNames.HasNoControlCharacters(text, allowLineBreaks);
    }

    internal static bool IsLowerHex(string value, int length) =>
        value.Length == length && value.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
}
