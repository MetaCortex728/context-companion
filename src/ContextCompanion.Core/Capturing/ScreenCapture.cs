using ContextCompanion.Core.Privacy;

namespace ContextCompanion.Core.Capturing;

/// <summary>
/// One captured frame: straight-alpha BGRA pixels, the screen rectangle they show (physical pixels), the method that
/// produced them (a lower-case token such as <c>wgc</c> or <c>gdi</c>) and the scale of the monitor they came from.
/// </summary>
public sealed record CapturedFrame(BgraImage Image, ScreenRect Bounds, string Method, double? Scale);

/// <summary>Takes one frame per call from a window or a screen region. Implemented per platform.</summary>
public interface IScreenCapturer
{
    /// <summary>
    /// Captures one frame of a top-level window. Throws <see cref="CaptureFailedException"/> when no method succeeded.
    /// Callers check first that the window is not minimised and not protected from capture.
    /// </summary>
    CapturedFrame CaptureWindow(nint window, bool includeCursor);

    /// <summary>Captures one frame of a screen rectangle in physical pixels. Throws <see cref="CaptureFailedException"/>.</summary>
    CapturedFrame CaptureRegion(ScreenRect region, bool includeCursor);
}

/// <summary>
/// A top-level window as the operating system reports it. Titles, classes and paths are used to apply the denylist
/// and are kept in a packet only when the settings allow it.
/// </summary>
public sealed record WindowInfo
{
    public required nint Handle { get; init; }
    /// <summary>The visible frame in physical screen pixels (on Windows the DWM extended frame bounds).</summary>
    public required ScreenRect Bounds { get; init; }
    public string? ProcessName { get; init; }
    public string? WindowClass { get; init; }
    public string? Title { get; init; }
    public string? ExecutablePath { get; init; }
    public bool IsMinimized { get; init; }
    /// <summary>The application asked the system to keep this window out of screen capture.</summary>
    public bool IsCaptureProtected { get; init; }
    /// <summary>Scale of the monitor the window is on (1.5 = 150 %).</summary>
    public double? Scale { get; init; }
}

/// <summary>Reports top-level windows. Implemented per platform.</summary>
public interface IWindowInventory
{
    /// <summary>The window with this handle, or null if it does not exist or is not a top-level window.</summary>
    WindowInfo? Describe(nint window);

    /// <summary>The foreground window, or 0 when there is none.</summary>
    nint ForegroundWindow();

    /// <summary>
    /// Visible, uncloaked top-level windows that intersect <paramref name="area"/>, topmost first. Includes windows
    /// of every process, including context-companion's own.
    /// </summary>
    IReadOnlyList<WindowInfo> VisibleWindows(ScreenRect area);
}

/// <summary>A capture method failed. The message never contains window titles or captured content.</summary>
public sealed class CaptureFailedException(string message, Exception? inner = null) : Exception(message, inner);
