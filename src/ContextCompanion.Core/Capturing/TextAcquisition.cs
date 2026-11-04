namespace ContextCompanion.Core.Capturing;

/// <summary>The window text is taken from, as far as the sources need to know, and what the user asked for.</summary>
/// <param name="ProcessName">Executable name of the focused window's process.</param>
/// <param name="WindowClass">Class of the focused top-level window; used to recognise terminals.</param>
/// <param name="IncludeClipboard">The user asked for the clipboard's current text explicitly.</param>
public sealed record TextTarget(string? ProcessName, string? WindowClass, bool IncludeClipboard);

public enum TextAttemptResult
{
    /// <summary>The source returned text; later sources were not tried.</summary>
    Answered,
    /// <summary>The source worked but had no text.</summary>
    Empty,
    /// <summary>The source was not tried, for example the explicit copy in a terminal.</summary>
    Skipped,
    /// <summary>The source failed.</summary>
    Failed,
    /// <summary>The source gave no answer in time.</summary>
    TimedOut,
}

/// <summary>One source tried, and what happened. Details are fixed phrases; they never contain the text itself.</summary>
public sealed record TextAttempt(string Source, TextAttemptResult Result, string Detail);

/// <summary>The text, the name of the source that answered, and every attempt in order.</summary>
public sealed record TextAcquisitionResult(string? Text, string? Source, IReadOnlyList<TextAttempt> Attempts);

/// <summary>What one source returned.</summary>
public sealed record TextSourceResult(TextAttemptResult Result, string Detail, string? Text = null)
{
    public static TextSourceResult Answered(string text) => new(TextAttemptResult.Answered, "answered", text);
    public static TextSourceResult Empty(string detail) => new(TextAttemptResult.Empty, detail);
    public static TextSourceResult Skipped(string detail) => new(TextAttemptResult.Skipped, detail);
    public static TextSourceResult Failed(string detail) => new(TextAttemptResult.Failed, detail);
    public static TextSourceResult TimedOut(string detail) => new(TextAttemptResult.TimedOut, detail);
}

/// <summary>A place text can come from. <see cref="Name"/> is a lower-case token that becomes part of the capture mode.</summary>
public interface ITextSource
{
    string Name { get; }
    Task<TextSourceResult> ReadAsync(TextTarget target, CancellationToken cancellationToken);
}

/// <summary>A platform failure inside a text source. The message never contains captured text or titles.</summary>
public sealed class TextSourceException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Tries text sources in order and keeps the first non-blank answer, recording every attempt. The standard order
/// (<see cref="Create"/>) is: the focused element's selection through UI Automation, then the clipboard's current text
/// (only when asked for explicitly), then an explicit copy.
/// </summary>
public sealed class TextAcquisition(IEnumerable<ITextSource> sources)
{
    private readonly ITextSource[] sources = sources?.ToArray() ?? throw new ArgumentNullException(nameof(sources));

    public IReadOnlyList<string> Order => sources.Select(s => s.Name).ToArray();

    /// <summary>The standard sources in the standard order.</summary>
    public static TextAcquisition Create(ISelectionReader selection, IClipboardAccess clipboard, IKeyboardInput keyboard,
        CaptureSettings settings, TimeProvider? time = null) =>
        new([new SelectionTextSource(selection), new ClipboardTextSource(clipboard), new ExplicitCopyTextSource(clipboard, keyboard, settings, time)]);

    public async Task<TextAcquisitionResult> AcquireAsync(TextTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var attempts = new List<TextAttempt>();
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TextSourceResult result;
            try { result = await source.ReadAsync(target, cancellationToken).ConfigureAwait(false); }
            catch (TextSourceException ex) { result = TextSourceResult.Failed(ex.Message); }
            if (result.Result == TextAttemptResult.Answered && string.IsNullOrWhiteSpace(result.Text))
                result = TextSourceResult.Empty("no text");
            attempts.Add(new TextAttempt(source.Name, result.Result, result.Detail));
            if (result.Result == TextAttemptResult.Answered) return new TextAcquisitionResult(result.Text, source.Name, attempts);
        }
        return new TextAcquisitionResult(null, null, attempts);
    }
}

/// <summary>Reads the focused element's selected text, for example through UI Automation's TextPattern.</summary>
public interface ISelectionReader
{
    /// <summary>The selected text, or null when the focused element exposes no selection. Throws <see cref="TextSourceException"/>.</summary>
    string? ReadSelection();
}

/// <summary>Text on the clipboard, and whether the application that put it there marked it as sensitive.</summary>
public sealed record ClipboardText(string? Text, bool MarkedSensitive);

/// <summary>A saved copy of the clipboard's restorable formats; only <see cref="IClipboardAccess"/> knows its contents.</summary>
public interface IClipboardSnapshot
{
    /// <summary>How many formats were saved.</summary>
    int FormatCount { get; }
}

/// <summary>
/// The clipboard. Whenever an implementation writes to it, it must also mark the content as excluded from clipboard
/// history, cloud clipboard and clipboard monitors (on Windows <c>ExcludeClipboardContentFromMonitorProcessing</c>,
/// <c>CanIncludeInClipboardHistory</c> = 0 and <c>CanUploadToCloudClipboard</c> = 0).
/// </summary>
public interface IClipboardAccess
{
    /// <summary>Changes whenever the clipboard's content changes.</summary>
    uint SequenceNumber { get; }
    /// <summary>The clipboard's text. Throws <see cref="TextSourceException"/>.</summary>
    ClipboardText ReadText();
    /// <summary>Saves the formats that can be restored faithfully. Throws <see cref="TextSourceException"/>.</summary>
    IClipboardSnapshot Save();
    /// <summary>Replaces the clipboard's content with the snapshot. Throws <see cref="TextSourceException"/>.</summary>
    void Restore(IClipboardSnapshot snapshot);
}

/// <summary>Synthesised keyboard input.</summary>
public interface IKeyboardInput
{
    /// <summary>Sends Ctrl+C to the focused window. Throws <see cref="TextSourceException"/>.</summary>
    void SendCopy();
}

/// <summary>Source "selection": the focused element's selected text.</summary>
public sealed class SelectionTextSource(ISelectionReader reader) : ITextSource
{
    public string Name => "selection";

    public Task<TextSourceResult> ReadAsync(TextTarget target, CancellationToken cancellationToken)
    {
        string? text = reader.ReadSelection();
        return Task.FromResult(text is null ? TextSourceResult.Empty("the focused element exposes no selection")
            : string.IsNullOrWhiteSpace(text) ? TextSourceResult.Empty("nothing is selected")
            : TextSourceResult.Answered(text));
    }
}

/// <summary>Source "clipboard": the clipboard's current text, read only when the user asked for it.</summary>
public sealed class ClipboardTextSource(IClipboardAccess clipboard) : ITextSource
{
    public string Name => "clipboard";

    public Task<TextSourceResult> ReadAsync(TextTarget target, CancellationToken cancellationToken)
    {
        if (!target.IncludeClipboard) return Task.FromResult(TextSourceResult.Skipped("not asked for"));
        var content = clipboard.ReadText();
        return Task.FromResult(content.MarkedSensitive ? TextSourceResult.Skipped("the application that put it there marked it as sensitive")
            : string.IsNullOrWhiteSpace(content.Text) ? TextSourceResult.Empty("the clipboard holds no text")
            : TextSourceResult.Answered(content.Text));
    }
}

/// <summary>
/// Source "copy": saves the clipboard, sends Ctrl+C, waits for the clipboard to change, reads the text and restores the
/// saved content. The restore always runs once the clipboard was saved, also after a timeout or a failure. Never sends
/// Ctrl+C to a console or terminal window, where it would interrupt the running program, nor to a window it cannot
/// identify.
/// </summary>
public sealed class ExplicitCopyTextSource(IClipboardAccess clipboard, IKeyboardInput keyboard, CaptureSettings settings, TimeProvider? time = null)
    : ITextSource
{
    /// <summary>How often the clipboard sequence number is checked while waiting.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    private readonly TimeProvider time = time ?? TimeProvider.System;

    public string Name => "copy";

    public async Task<TextSourceResult> ReadAsync(TextTarget target, CancellationToken cancellationToken)
    {
        if (!settings.ExplicitCopy) return TextSourceResult.Skipped("switched off in the settings");
        if (target.WindowClass is null) return TextSourceResult.Skipped("the focused window could not be identified, so Ctrl+C was not sent");
        if (settings.IsTerminalWindowClass(target.WindowClass))
            return TextSourceResult.Skipped("not sent to a console or terminal window, where Ctrl+C interrupts the running program");

        IClipboardSnapshot snapshot;
        try { snapshot = clipboard.Save(); }
        catch (TextSourceException ex) { return TextSourceResult.Failed("the clipboard could not be saved, so Ctrl+C was not sent: " + ex.Message); }

        TextSourceResult result;
        string? restoreFailure = null;
        try { result = await CopyAsync(cancellationToken).ConfigureAwait(false); }
        catch (TextSourceException ex) { result = TextSourceResult.Failed(ex.Message); }
        catch (OperationCanceledException) { result = TextSourceResult.Failed("cancelled"); }
        finally
        {
            // Runs on every path once the clipboard was saved; a failed restore is reported below.
            try { clipboard.Restore(snapshot); }
            catch (TextSourceException ex) { restoreFailure = ex.Message; }
        }
        if (restoreFailure is not null)
            result = result with { Detail = result.Detail + "; the clipboard could not be restored: " + restoreFailure };
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private async Task<TextSourceResult> CopyAsync(CancellationToken cancellationToken)
    {
        uint before = clipboard.SequenceNumber;
        long started = time.GetTimestamp();
        keyboard.SendCopy();
        bool changed = false;
        while (time.GetElapsedTime(started) < settings.CopyTimeout)
        {
            if (clipboard.SequenceNumber != before)
            {
                changed = true;
                // Some applications empty the clipboard first and add the text a moment later.
                var content = clipboard.ReadText();
                if (content.MarkedSensitive) return TextSourceResult.Skipped("the application marked the copied content as sensitive");
                if (!string.IsNullOrEmpty(content.Text)) return TextSourceResult.Answered(content.Text);
            }
            await Task.Delay(PollInterval, time, cancellationToken).ConfigureAwait(false);
        }
        return changed
            ? TextSourceResult.Empty("the copied content holds no text")
            : TextSourceResult.TimedOut($"the clipboard did not change within {settings.CopyTimeoutMs} ms");
    }
}
