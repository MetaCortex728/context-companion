using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ContextCompanion.Core.Capturing;
using ContextCompanion.Core.Privacy;
using ContextCompanion.Platform.Windows.Interop;
using static ContextCompanion.Platform.Windows.Interop.NativeMethods;

namespace ContextCompanion.Platform.Windows.Capture;

/// <summary>
/// The GDI fallback: <c>PrintWindow</c> with <c>PW_RENDERFULLCONTENT</c> for a window and a screen <c>BitBlt</c>
/// (with <c>CAPTUREBLT</c>, so layered windows are included) for a region. Used when Windows.Graphics.Capture is not
/// supported or fails, and for regions that span monitors.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal static class GdiCapture
{
    /// <summary>Copies the screen rectangle. Pixels come out opaque.</summary>
    public static BgraImage CaptureRegion(ScreenRect region, bool includeCursor)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) throw new CaptureFailedException("The screen could not be read (GetDC failed).");
        try
        {
            return Render(screen, region.Width, region.Height, includeCursor ? region : null,
                memory => BitBlt(memory, 0, 0, region.Width, region.Height, screen, region.X, region.Y, SRCCOPY | CAPTUREBLT),
                "BitBlt");
        }
        finally { ReleaseDC(IntPtr.Zero, screen); }
    }

    /// <summary>
    /// Renders the window with <c>PrintWindow</c> and crops the result to <paramref name="frame"/> (the visible frame,
    /// without the invisible resize borders that <c>GetWindowRect</c> includes).
    /// </summary>
    public static BgraImage CaptureWindow(IntPtr window, ScreenRect frame, bool includeCursor)
    {
        if (!GetWindowRect(window, out var rect) || rect.Width < 1 || rect.Height < 1)
            throw new CaptureFailedException("The window has no area to capture.");
        var outer = Displays.FromRect(rect);
        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) throw new CaptureFailedException("The screen could not be read (GetDC failed).");
        BgraImage full;
        try
        {
            full = Render(screen, outer.Width, outer.Height, includeCursor ? outer : null,
                memory => PrintWindow(window, memory, PW_RENDERFULLCONTENT), "PrintWindow");
        }
        finally { ReleaseDC(IntPtr.Zero, screen); }
        var inside = frame.Intersect(outer);
        if (inside.IsEmpty || inside == outer) return full;
        return ImageOps.Crop(full, new ScreenRect(inside.X - outer.X, inside.Y - outer.Y, inside.Width, inside.Height));
    }

    private static BgraImage Render(IntPtr screen, int width, int height, ScreenRect? cursorOrigin, Func<IntPtr, bool> draw, string method)
    {
        if (width < 1 || height < 1 || width > BgraImage.MaxDimension || height > BgraImage.MaxDimension)
            throw new CaptureFailedException($"The capture size must be 1 to {BgraImage.MaxDimension} pixels on each side.");
        IntPtr memory = CreateCompatibleDC(screen);
        IntPtr bitmap = CreateCompatibleBitmap(screen, width, height);
        try
        {
            if (memory == IntPtr.Zero || bitmap == IntPtr.Zero) throw new CaptureFailedException("Could not allocate a bitmap for the capture.");
            IntPtr previous = SelectObject(memory, bitmap);
            bool drawn;
            try
            {
                drawn = draw(memory);
                if (drawn && cursorOrigin is { } origin) DrawCursor(memory, origin);
            }
            finally { SelectObject(memory, previous); }
            if (!drawn) throw new CaptureFailedException($"{method} failed.");

            var header = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // top-down rows
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
            };
            var pixels = new byte[width * 4 * height];
            if (GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref header, DIB_RGB_COLORS) != height)
                throw new CaptureFailedException("The captured bitmap could not be read (GetDIBits failed).");
            // GDI leaves the fourth byte undefined; the screen is opaque.
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            return new BgraImage(width, height, width * 4, pixels);
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
        }
    }

    /// <summary>Draws the current pointer into the bitmap, which shows the screen from <paramref name="origin"/>.</summary>
    private static void DrawCursor(IntPtr memory, ScreenRect origin)
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info) || (info.flags & CURSOR_SHOWING) == 0 || info.hCursor == IntPtr.Zero) return;
        int hotX = 0, hotY = 0;
        if (GetIconInfo(info.hCursor, out var icon))
        {
            hotX = icon.xHotspot; hotY = icon.yHotspot;
            if (icon.hbmMask != IntPtr.Zero) DeleteObject(icon.hbmMask);
            if (icon.hbmColor != IntPtr.Zero) DeleteObject(icon.hbmColor);
        }
        DrawIconEx(memory, info.ptScreenPos.X - hotX - origin.X, info.ptScreenPos.Y - hotY - origin.Y, info.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
    }
}
