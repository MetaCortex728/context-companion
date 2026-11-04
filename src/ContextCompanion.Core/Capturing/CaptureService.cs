using ContextCompanion.Core.Packets;
using ContextCompanion.Core.Privacy;

namespace ContextCompanion.Core.Capturing;

/// <summary>What happened to one capture.</summary>
public enum CaptureOutcomeStatus
{
    /// <summary>A packet was queued in the outbox.</summary>
    Queued,
    /// <summary>Same content as the previous packet; not queued again.</summary>
    Duplicate,
    /// <summary>The target window matched a denylist rule; nothing was taken from it.</summary>
    Blocked,
    /// <summary>The text was withheld because the secret scan did not finish in time, and nothing else was left.</summary>
    Withheld,
    /// <summary>The capture was not attempted: for example the window is minimised, protected or gone.</summary>
    Refused,
    /// <summary>No text could be obtained from any source.</summary>
    NoText,
    /// <summary>Every capture method failed.</summary>
    Failed,
}

/// <summary>
/// The result of a capture: status, packet id, capture mode and redactions. The message names modes, rules and
/// reasons only; it never contains captured content, window titles, classes or paths.
/// </summary>
public sealed record CaptureOutcome
{
    public required CaptureOutcomeStatus Status { get; init; }
    public string? PacketId { get; init; }
    /// <summary>The capture mode, for example <c>window-wgc</c>, <c>region-gdi</c> or <c>text-selection</c>.</summary>
    public string? Mode { get; init; }
    public string? Rule { get; init; }
    public IReadOnlyList<PacketRedaction> Redactions { get; init; } = [];
    /// <summary>For text captures: each source tried, in order, and what happened.</summary>
    public IReadOnlyList<TextAttempt> TextAttempts { get; init; } = [];
    public required string Message { get; init; }
}

/// <summary>
/// Takes a window, region or text capture and hands it to the <see cref="PrivacyPipeline"/>, so nothing is kept
/// before the privacy rules have run. The target window is checked against the denylist before anything is taken
/// from it. Images are downscaled to <see cref="CaptureSettings.MaxLongEdge"/> before the pipeline encodes them.
/// </summary>
public sealed class CaptureService
{
    private readonly PrivacyPipeline pipeline;
    private readonly IScreenCapturer? capturer;
    private readonly IWindowInventory windows;
    private readonly TextAcquisition? text;

    public CaptureService(PrivacyPipeline pipeline, IWindowInventory windows, IScreenCapturer? capturer = null, TextAcquisition? text = null)
    {
        this.pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        this.windows = windows ?? throw new ArgumentNullException(nameof(windows));
        this.capturer = capturer;
        this.text = text;
    }

    private CaptureSettings Settings => pipeline.Settings.Capture;

    /// <summary>Captures one top-level window.</summary>
    public CaptureOutcome CaptureWindow(nint window)
    {
        var capturer = this.capturer ?? throw new InvalidOperationException("No screen capturer is available.");
        if (windows.Describe(window) is not { } info)
            return Refused("window", "Not captured: the window does not exist or is not a top-level window.");
        var source = SourceOf(info, "window");
        if (pipeline.CheckDenylist(source) is { } blocked) return From(blocked, "window");
        if (info.IsMinimized)
            return Refused("window", "Not captured: the window is minimised, so only stale content could be captured. Restore it and try again.");
        if (info.IsCaptureProtected)
            return Refused("window", "Not captured: the application protects this window from screen capture.");

        CapturedFrame frame;
        try { frame = capturer.CaptureWindow(window, Settings.IncludeCursor); }
        catch (CaptureFailedException ex) { return Failed("window", ex); }

        string mode = "window-" + frame.Method;
        // Only the window itself is in a window capture: other windows on top of it do not appear in the frame.
        return Process(new CaptureRequest
        {
            Image = ImageOps.Downscale(frame.Image, Settings.MaxLongEdge),
            Source = source with { CaptureMode = mode, Region = frame.Bounds, Scale = frame.Scale ?? info.Scale },
        }, mode);
    }

    /// <summary>Captures the foreground window.</summary>
    public CaptureOutcome CaptureForegroundWindow()
    {
        nint window = windows.ForegroundWindow();
        return window == 0 ? Refused("window", "Not captured: there is no foreground window.") : CaptureWindow(window);
    }

    /// <summary>
    /// Captures a screen rectangle in physical pixels. Denylisted windows inside it are blacked out; when the topmost
    /// window at the rectangle's centre (the window the source details describe) is denylisted, the capture is refused.
    /// </summary>
    public CaptureOutcome CaptureRegion(ScreenRect region)
    {
        var capturer = this.capturer ?? throw new InvalidOperationException("No screen capturer is available.");
        if (region.IsEmpty || region.Width > BgraImage.MaxDimension || region.Height > BgraImage.MaxDimension)
            return Refused("region", $"Not captured: the region must be 1 to {BgraImage.MaxDimension} pixels wide and high.");

        var visible = windows.VisibleWindows(region);
        long centreX = region.X + region.Width / 2, centreY = region.Y + region.Height / 2;
        var under = visible.FirstOrDefault(w => !w.IsMinimized &&
            w.Bounds.X <= centreX && centreX < w.Bounds.Right && w.Bounds.Y <= centreY && centreY < w.Bounds.Bottom);
        var source = under is null ? new CaptureSource { CaptureMode = "region" } : SourceOf(under, "region");
        if (pipeline.CheckDenylist(source) is { } blocked) return From(blocked, "region");

        CapturedFrame frame;
        try { frame = capturer.CaptureRegion(region, Settings.IncludeCursor); }
        catch (CaptureFailedException ex) { return Failed("region", ex); }

        string mode = "region-" + frame.Method;
        return Process(new CaptureRequest
        {
            Image = ImageOps.Downscale(frame.Image, Settings.MaxLongEdge),
            Source = source with { CaptureMode = mode, Region = frame.Bounds, Scale = frame.Scale },
            VisibleWindows = visible.Select(w => new VisibleWindow
            {
                Bounds = w.Bounds,
                ProcessName = w.ProcessName,
                WindowClass = w.WindowClass,
                Title = w.Title,
            }).ToArray(),
        }, mode);
    }

    /// <summary>
    /// Captures text from the foreground window: the UI Automation selection, then (only when
    /// <paramref name="includeClipboard"/>) the clipboard, then an explicit copy. Refused before any source is tried
    /// when the window is denylisted.
    /// </summary>
    public async Task<CaptureOutcome> CaptureTextAsync(bool includeClipboard, CancellationToken cancellationToken = default)
    {
        var acquisition = text ?? throw new InvalidOperationException("No text sources are available.");
        nint window = windows.ForegroundWindow();
        var info = window == 0 ? null : windows.Describe(window);
        var source = info is null ? new CaptureSource { CaptureMode = "text" } : SourceOf(info, "text");
        if (pipeline.CheckDenylist(source) is { } blocked) return From(blocked, "text");

        var result = await acquisition.AcquireAsync(new TextTarget(info?.ProcessName, info?.WindowClass, includeClipboard), cancellationToken)
            .ConfigureAwait(false);
        if (result.Text is null)
            return new CaptureOutcome
            {
                Status = CaptureOutcomeStatus.NoText,
                Mode = "text",
                TextAttempts = result.Attempts,
                Message = "No text captured: " + string.Join("; ", result.Attempts.Select(a => $"{a.Source}: {a.Detail}")) + ".",
            };

        string mode = "text-" + result.Source;
        return Process(new CaptureRequest { Text = result.Text, Source = source with { CaptureMode = mode } }, mode) with { TextAttempts = result.Attempts };
    }

    private CaptureOutcome Process(CaptureRequest request, string mode)
    {
        var result = pipeline.Process(request);
        return From(result, mode);
    }

    private static CaptureSource SourceOf(WindowInfo info, string mode) => new()
    {
        ProcessName = info.ProcessName,
        WindowClass = info.WindowClass,
        WindowTitle = info.Title,
        ExecutablePath = info.ExecutablePath,
        CaptureMode = mode,
        Scale = info.Scale,
    };

    private static CaptureOutcome From(CaptureResult result, string mode) => new()
    {
        Status = result.Status switch
        {
            CaptureStatus.Queued => CaptureOutcomeStatus.Queued,
            CaptureStatus.Duplicate => CaptureOutcomeStatus.Duplicate,
            CaptureStatus.Blocked => CaptureOutcomeStatus.Blocked,
            _ => CaptureOutcomeStatus.Withheld,
        },
        PacketId = result.PacketId,
        Mode = mode,
        Rule = result.Rule,
        Redactions = result.Redactions,
        Message = result.Message,
    };

    private static CaptureOutcome Refused(string mode, string message) =>
        new() { Status = CaptureOutcomeStatus.Refused, Mode = mode, Message = message };

    private static CaptureOutcome Failed(string mode, CaptureFailedException ex) =>
        new() { Status = CaptureOutcomeStatus.Failed, Mode = mode, Message = $"Not captured: {ex.Message}" };
}
