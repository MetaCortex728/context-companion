using System.Text;
using ContextCompanion.Core.Delivery;
using ContextCompanion.Core.Packets;

namespace ContextCompanion.Core.Privacy;

/// <summary>What happened to a capture request.</summary>
public enum CaptureStatus
{
    /// <summary>A packet was built and queued in the outbox.</summary>
    Queued,
    /// <summary>A packet was built but dropped as a consecutive duplicate of the previous one.</summary>
    Duplicate,
    /// <summary>The captured window matched a denylist rule; nothing was kept.</summary>
    Blocked,
    /// <summary>Nothing was left to deliver after text that could not be scanned in time was withheld.</summary>
    Withheld,
}

/// <summary>Result of <see cref="PrivacyPipeline.Process"/>. Contains rule and pattern names, never captured content.</summary>
public sealed record CaptureResult
{
    public required CaptureStatus Status { get; init; }
    /// <summary>The queued packet, or for a duplicate the earlier packet it duplicates. Null when blocked.</summary>
    public string? PacketId { get; init; }
    /// <summary>The denylist rule that refused the capture, when blocked.</summary>
    public string? Rule { get; init; }
    /// <summary>The redactions recorded in the packet's manifest.</summary>
    public IReadOnlyList<PacketRedaction> Redactions { get; init; } = [];
    /// <summary>A short message for the user.</summary>
    public required string Message { get; init; }
}

/// <summary>
/// Turns a <see cref="CaptureRequest"/> into a packet in the outbox. The order is fixed:
/// denylist (refuse, or black out windows inside a region), then the metadata filter, then text redaction,
/// then the packet, then optional encryption, then the outbox. Nothing reaches a packet before these rules
/// have run, and the pipeline never changes the caller's pixel buffer.
/// </summary>
public sealed class PrivacyPipeline
{
    public const string ImageFileName = "capture.png";
    public const string TextFileName = "selection.txt";
    public const string NoteFileName = "note.txt";

    private readonly Outbox outbox;
    private readonly Denylist denylist;
    private readonly SecretRedactor? redactor;
    private readonly IPayloadProtector? protector;
    private readonly IImageEncoder encoder;
    private readonly TimeProvider? time;
    private readonly UlidGenerator? ids;

    /// <summary>
    /// Creates the pipeline. When encryption is enabled in <paramref name="settings"/>, <paramref name="protector"/> must
    /// provide the configured protection; otherwise the pipeline refuses to start rather than store plaintext.
    /// </summary>
    public PrivacyPipeline(PrivacySettings settings, Outbox outbox, string destination, IPayloadProtector? protector = null,
        IImageEncoder? encoder = null, TimeProvider? time = null, UlidGenerator? ids = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        // A private snapshot: later changes to the caller's settings object do not affect a running pipeline.
        settings = PrivacySettings.Parse(settings.ToJson());
        if (!PacketNames.IsValidAgentName(destination)) throw new ArgumentException("Invalid destination name.", nameof(destination));
        Settings = settings;
        denylist = Denylist.Compile(settings.Denylist);
        redactor = settings.Redaction.Secrets ? SecretRedactor.FromSettings(settings.Redaction) : null;
        if (settings.Encryption.Enabled)
        {
            if (protector is null || protector.Protection != settings.Encryption.Protection)
                throw new InvalidOperationException(
                    $"Encryption at rest is enabled with {settings.Encryption.Protection}, which is not available here; nothing will be captured until it is.");
            this.protector = protector;
        }
        this.outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        Destination = destination;
        this.encoder = encoder ?? new PngEncoder();
        this.time = time;
        this.ids = ids;
    }

    public PrivacySettings Settings { get; }
    public string Destination { get; }

    /// <summary>
    /// Runs every privacy rule and queues the resulting packet. Throws <see cref="ArgumentException"/> for a malformed
    /// request and <see cref="OutboxFullException"/> when the outbox is full; neither message contains captured content
    /// or source details.
    /// </summary>
    public CaptureResult Process(CaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        CheckShape(request);

        // 1. Denylist. A matching captured window is refused before any pixels or text are kept.
        var captured = request.Source;
        if (CheckDenylist(captured) is { } blocked) return blocked;
        var redactions = new List<PacketRedaction>();
        var image = request.Image?.Clone();
        if (image is not null) redactions.AddRange(BlackOutDenylistedWindows(image, captured.Region!.Value, request.VisibleWindows));

        // 2. Metadata filter: from here on only enabled fields exist.
        var source = FilterMetadata(request.Source, Settings.Metadata);

        // 3. Text redaction.
        string? text = RedactSecrets(request.Text, TextFileName, redactions);
        string? note = RedactSecrets(request.Note, NoteFileName, redactions);
        if (image is null && text is null && note is null)
            return new CaptureResult
            {
                Status = CaptureStatus.Withheld,
                Redactions = redactions,
                Message = "Nothing was queued: the text was withheld because the secret scan did not finish in time.",
            };

        // 4. Packet.
        var builder = new PacketBuilder(ids, time);
        if (image is not null) builder.AddItem(PacketItemKind.Image, ImageFileName, encoder.MediaType, encoder.Encode(image));
        if (text is not null) builder.AddText(TextFileName, text);
        if (note is not null) builder.AddText(NoteFileName, note, PacketItemKind.Note);
        builder.WithSource(source);
        foreach (var redaction in redactions) builder.AddRedaction(redaction);
        var draft = builder.Build();

        // 5. Optional encryption.
        if (protector is not null) draft = PacketProtection.Protect(draft, protector);

        // 6. Outbox.
        var queued = outbox.Enqueue(draft, Destination);
        return new CaptureResult
        {
            Status = queued.Queued ? CaptureStatus.Queued : CaptureStatus.Duplicate,
            PacketId = queued.Record.Id,
            Redactions = draft.Manifest.Redactions,
            Message = queued.Queued
                ? $"Packet {draft.Id} queued for {Destination}."
                : $"Same content as packet {queued.Record.Id}; not queued again.",
        };
    }

    /// <summary>
    /// Step 1 on its own, for callers that check the target window before taking anything from it (capture calls this
    /// before grabbing pixels or sending a copy keystroke). When the window matches a denylist rule, records
    /// <c>blocked</c> with the rule name in the audit log and returns the refusal; otherwise returns null. Only the
    /// process name, class and title are read, and none of them is written anywhere.
    /// </summary>
    public CaptureResult? CheckDenylist(CaptureSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (denylist.Match(source.ProcessName, source.WindowClass, source.WindowTitle) is not { } rule) return null;
        outbox.Audit.RecordBlocked(rule);
        return new CaptureResult
        {
            Status = CaptureStatus.Blocked,
            Rule = rule,
            Message = $"Capture refused: the window matches denylist rule '{rule}'. Nothing was kept.",
        };
    }

    /// <summary>
    /// Replaces secret-shaped text and records one redaction per pattern. If a pattern times out the text cannot be
    /// shown to be clean, so it is withheld entirely (fail closed) and that is recorded instead.
    /// </summary>
    private string? RedactSecrets(string? text, string item, List<PacketRedaction> redactions)
    {
        if (text is null || redactor is null) return text;
        SecretRedactionResult result;
        try { result = redactor.Redact(text); }
        catch (SecretScanTimeoutException ex)
        {
            redactions.Add(new PacketRedaction
            {
                Kind = "withheld",
                Reason = $"The {(item == NoteFileName ? "note" : "text")} was left out because the secret scan did not finish in time",
                Pattern = ex.Pattern,
            });
            return null;
        }
        foreach (var count in result.Counts)
            redactions.Add(new PacketRedaction
            {
                Kind = "secret",
                Reason = "Secret-like text replaced with [REDACTED:" + count.Pattern + "]",
                Item = item,
                Pattern = count.Pattern,
                Count = count.Count,
            });
        return result.Text;
    }

    /// <summary>Fills every denylisted window inside the region with black. Returns one redaction record per rule.</summary>
    private IEnumerable<PacketRedaction> BlackOutDenylistedWindows(BgraImage image, ScreenRect region, IReadOnlyList<VisibleWindow> windows)
    {
        var counts = new OrderedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var window in windows)
        {
            if (denylist.Match(window.ProcessName, window.WindowClass, window.Title) is not { } rule) continue;
            if (Blackout.Fill(image, region, window.Bounds))
                counts[rule] = counts.GetValueOrDefault(rule) + 1;
        }
        return counts.Select(c => new PacketRedaction
        {
            Kind = "blackout",
            Reason = "Window on the denylist filled with black",
            Item = ImageFileName,
            Rule = c.Key,
            Count = c.Value,
        });
    }

    private void CheckShape(CaptureRequest request)
    {
        if (request.Source is null) throw new ArgumentException("The capture request has no source.", nameof(request));
        if (request.Image is null && request.Text is null && request.Note is null)
            throw new ArgumentException("The capture request holds no image, text or note.", nameof(request));
        if (!PacketNames.IsValidToken(request.Source.CaptureMode))
            throw new ArgumentException("The capture mode must be a lower-case token such as window or region.", nameof(request));
        if (request.Source.Region is { IsEmpty: true })
            throw new ArgumentException("The capture region must have a positive width and height.", nameof(request));
        if (request.Image is not null && request.Source.Region is null)
            throw new ArgumentException("A capture request with an image needs the captured region.", nameof(request));
        if (request.Source.Scale is { } scale && !(scale > 0 && scale <= PacketFormat.MaxScale))
            throw new ArgumentException($"The scale must be greater than 0 and at most {PacketFormat.MaxScale}.", nameof(request));
        if (request.VisibleWindows is null || request.VisibleWindows.Any(w => w is null))
            throw new ArgumentException("The list of visible windows is invalid.", nameof(request));
    }

    /// <summary>Copies only the enabled source fields, cleaned of control characters and cut to the format's limits.</summary>
    internal static PacketSource? FilterMetadata(CaptureSource source, MetadataSettings settings)
    {
        var result = new PacketSource
        {
            ProcessName = settings.ProcessName ? Clean(source.ProcessName, PacketFormat.MaxProcessNameLength) : null,
            WindowClass = settings.WindowClass ? Clean(source.WindowClass, PacketFormat.MaxWindowClassLength) : null,
            WindowTitle = settings.WindowTitle ? Clean(source.WindowTitle, PacketFormat.MaxWindowTitleLength) : null,
            ExecutablePath = settings.ExecutablePath ? Clean(source.ExecutablePath, PacketFormat.MaxExecutablePathLength) : null,
            CaptureMode = settings.CaptureMode ? source.CaptureMode : null,
            Region = settings.Region && source.Region is { } r ? new PacketRegion(r.X, r.Y, r.Width, r.Height) : null,
            Scale = settings.Scale ? source.Scale : null,
        };
        return result == new PacketSource() ? null : result;
    }

    private static string? Clean(string? value, int maxLength)
    {
        if (value is null) return null;
        var text = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (char c in value)
        {
            if (text.Length == maxLength) break;
            text.Append(char.IsControl(c) ? ' ' : c);
        }
        // Do not end on half of a surrogate pair after cutting.
        if (text.Length > 0 && char.IsHighSurrogate(text[^1])) text.Length--;
        string cleaned = text.ToString().Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }
}
