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
    private readonly IImageEncoder encoder;
    private readonly TimeProvider? time;
    private readonly UlidGenerator? ids;

    public PrivacyPipeline(PrivacySettings settings, Outbox outbox, string destination,
        IImageEncoder? encoder = null, TimeProvider? time = null, UlidGenerator? ids = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        // A private snapshot: later changes to the caller's settings object do not affect a running pipeline.
        settings = PrivacySettings.Parse(settings.ToJson());
        if (!PacketNames.IsValidAgentName(destination)) throw new ArgumentException("Invalid destination name.", nameof(destination));
        Settings = settings;
        denylist = Denylist.Compile(settings.Denylist);
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
        if (denylist.Match(captured.ProcessName, captured.WindowClass, captured.WindowTitle) is { } rule)
        {
            outbox.Audit.RecordBlocked(rule);
            return new CaptureResult
            {
                Status = CaptureStatus.Blocked,
                Rule = rule,
                Message = $"Capture refused: the window matches denylist rule '{rule}'. Nothing was kept.",
            };
        }
        var redactions = new List<PacketRedaction>();
        var image = request.Image?.Clone();
        if (image is not null) redactions.AddRange(BlackOutDenylistedWindows(image, captured.Region!.Value, request.VisibleWindows));

        // 2. Metadata filter: from here on only enabled fields exist.
        var source = FilterMetadata(request.Source, Settings.Metadata);

        // 3. Text redaction.
        string? text = request.Text;
        string? note = request.Note;

        // 4. Packet.
        var builder = new PacketBuilder(ids, time);
        if (image is not null) builder.AddItem(PacketItemKind.Image, ImageFileName, encoder.MediaType, encoder.Encode(image));
        if (text is not null) builder.AddText(TextFileName, text);
        if (note is not null) builder.AddText(NoteFileName, note, PacketItemKind.Note);
        builder.WithSource(source);
        foreach (var redaction in redactions) builder.AddRedaction(redaction);
        var draft = builder.Build();

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
