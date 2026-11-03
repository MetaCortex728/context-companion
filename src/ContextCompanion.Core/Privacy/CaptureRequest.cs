namespace ContextCompanion.Core.Privacy;

/// <summary>A rectangle in physical screen pixels.</summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public long Right => (long)X + Width;
    public long Bottom => (long)Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>The overlap of two rectangles, or an empty rectangle when they do not overlap.</summary>
    public ScreenRect Intersect(ScreenRect other)
    {
        long left = Math.Max(X, other.X), top = Math.Max(Y, other.Y);
        long right = Math.Min(Right, other.Right), bottom = Math.Min(Bottom, other.Bottom);
        return right <= left || bottom <= top
            ? default
            : new ScreenRect((int)left, (int)top, (int)(right - left), (int)(bottom - top));
    }
}

/// <summary>
/// Raw captured pixels: 32-bit BGRA, top row first, <see cref="Stride"/> bytes per row
/// (at least <c>Width * 4</c>). The pipeline never changes the caller's buffer; it works on a copy.
/// </summary>
public sealed class BgraImage
{
    public const int MaxDimension = 16384;

    public BgraImage(int width, int height, int stride, byte[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width is < 1 or > MaxDimension || height is < 1 or > MaxDimension)
            throw new ArgumentException($"Image width and height must be 1 to {MaxDimension} pixels.");
        if (stride < width * 4)
            throw new ArgumentException("Image stride must be at least width * 4 bytes.", nameof(stride));
        if (pixels.LongLength < (long)stride * (height - 1) + width * 4L)
            throw new ArgumentException("The pixel buffer is smaller than stride * height.", nameof(pixels));
        Width = width; Height = height; Stride = stride; Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public byte[] Pixels { get; }

    public BgraImage Clone() => new(Width, Height, Stride, Pixels.ToArray());
}

/// <summary>
/// Where a capture came from, as reported by the platform. Everything here is input to the privacy
/// pipeline; only the fields switched on in <see cref="MetadataSettings"/> can reach a packet.
/// </summary>
public sealed record CaptureSource
{
    /// <summary>Executable name of the captured application, for example <c>notepad.exe</c>.</summary>
    public string? ProcessName { get; init; }
    public string? WindowClass { get; init; }
    public string? WindowTitle { get; init; }
    public string? ExecutablePath { get; init; }
    /// <summary>How it was captured: a lower-case token such as <c>window</c>, <c>region</c> or <c>text</c>.</summary>
    public required string CaptureMode { get; init; }
    /// <summary>The captured rectangle in physical screen pixels. Required when the request has an image.</summary>
    public ScreenRect? Region { get; init; }
    /// <summary>Display scale factor at capture time (1.5 = 150 %).</summary>
    public double? Scale { get; init; }
}

/// <summary>
/// A window the operating system reports as visible inside the captured region, used only to apply the
/// denylist. None of these fields is ever written to a packet or the audit log.
/// </summary>
public sealed record VisibleWindow
{
    /// <summary>The window's rectangle in physical screen pixels.</summary>
    public required ScreenRect Bounds { get; init; }
    public string? ProcessName { get; init; }
    public string? WindowClass { get; init; }
    /// <summary>Optional; lets title-pattern rules apply to windows inside a region.</summary>
    public string? Title { get; init; }
}

/// <summary>
/// A platform-neutral capture request: what one trigger produced, before any privacy rule has run.
/// At least one of <see cref="Image"/>, <see cref="Text"/> and <see cref="Note"/> must be present.
/// </summary>
public sealed record CaptureRequest
{
    public BgraImage? Image { get; init; }
    /// <summary>Text captured from an application, for example the selection.</summary>
    public string? Text { get; init; }
    /// <summary>Text written by the user.</summary>
    public string? Note { get; init; }
    public required CaptureSource Source { get; init; }
    /// <summary>Windows visible inside <see cref="CaptureSource.Region"/>, topmost first or in any order.</summary>
    public IReadOnlyList<VisibleWindow> VisibleWindows { get; init; } = [];
}
