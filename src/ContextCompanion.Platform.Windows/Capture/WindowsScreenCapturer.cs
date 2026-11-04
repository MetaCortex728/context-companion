using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ContextCompanion.Core.Capturing;
using ContextCompanion.Core.Privacy;
using static ContextCompanion.Platform.Windows.Interop.NativeMethods;

namespace ContextCompanion.Platform.Windows.Capture;

/// <summary>Which capture methods <see cref="WindowsScreenCapturer"/> may use.</summary>
public enum CaptureMethods
{
    /// <summary>Windows.Graphics.Capture first, GDI when it is unsupported or fails.</summary>
    Automatic,
    /// <summary>Windows.Graphics.Capture only (used by tests).</summary>
    WgcOnly,
    /// <summary>GDI only (used by tests).</summary>
    GdiOnly,
}

/// <summary>
/// Window and region capture on Windows. A window is captured with Windows.Graphics.Capture
/// (<c>CreateForWindow</c>), falling back to <c>PrintWindow</c>; a region on one monitor with
/// Windows.Graphics.Capture (<c>CreateForMonitor</c>, then cropped), falling back to a screen <c>BitBlt</c>. A region
/// that spans monitors always uses <c>BitBlt</c>. The method used is reported as <c>wgc</c> or <c>gdi</c>. The
/// process must be Per-Monitor V2 DPI aware (see <see cref="Displays.EnsurePerMonitorV2"/>).
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsScreenCapturer(CaptureMethods methods = CaptureMethods.Automatic) : IScreenCapturer
{
    public const string Wgc = "wgc", Gdi = "gdi";

    /// <summary>Whether Windows.Graphics.Capture can be used in this session (<c>GraphicsCaptureSession.IsSupported()</c>).</summary>
    public static bool IsWgcSupported => WgcCapture.IsSupported;

    public CapturedFrame CaptureWindow(nint window, bool includeCursor)
    {
        if (!IsWindow(window)) throw new CaptureFailedException("The window no longer exists.");
        if (IsIconic(window)) throw new CaptureFailedException("The window is minimised.");
        var bounds = Displays.FrameBounds(window) ?? throw new CaptureFailedException("The window has no area to capture.");
        double? scale = Displays.ScaleOf(MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST));

        Exception? wgcFailure = null;
        if (methods != CaptureMethods.GdiOnly)
        {
            if (TryWgc(w => w.CaptureWindow(window, includeCursor), out var image, out wgcFailure))
                return new CapturedFrame(image!, bounds, Wgc, scale);
            if (methods == CaptureMethods.WgcOnly) throw Failure("window", wgcFailure);
        }
        return new CapturedFrame(GdiCapture.CaptureWindow(window, bounds, includeCursor), bounds, Gdi, scale);
    }

    public CapturedFrame CaptureRegion(ScreenRect region, bool includeCursor)
    {
        if (region.IsEmpty) throw new CaptureFailedException("The region is empty.");
        IntPtr monitor = Displays.ContainingMonitor(region);
        double? scale = Displays.ScaleOf(monitor != IntPtr.Zero ? monitor : Displays.NearestMonitor(region));

        Exception? wgcFailure = null;
        if (methods != CaptureMethods.GdiOnly && monitor != IntPtr.Zero && Displays.BoundsOf(monitor) is { } screen)
        {
            if (TryWgc(w => w.CaptureMonitor(monitor, includeCursor), out var frame, out wgcFailure))
            {
                // The frame shows the whole monitor; cut the region out of it.
                var inFrame = new ScreenRect(region.X - screen.X, region.Y - screen.Y, region.Width, region.Height);
                if (inFrame.X >= 0 && inFrame.Y >= 0 && inFrame.Right <= frame!.Width && inFrame.Bottom <= frame.Height)
                    return new CapturedFrame(ImageOps.Crop(frame, inFrame), region, Wgc, scale);
                wgcFailure = new CaptureFailedException("The monitor frame does not cover the region.");
            }
        }
        if (methods == CaptureMethods.WgcOnly)
            throw monitor == IntPtr.Zero ? new CaptureFailedException("The region spans monitors; only GDI can capture it.") : Failure("region", wgcFailure);
        return new CapturedFrame(GdiCapture.CaptureRegion(region, includeCursor), region, Gdi, scale);
    }

    private static bool TryWgc(Func<WgcCapture, BgraImage> capture, out BgraImage? image, out Exception? failure)
    {
        image = null; failure = null;
        if (!WgcCapture.IsSupported) { failure = new CaptureFailedException("Windows.Graphics.Capture is not supported in this session."); return false; }
        try
        {
            using var wgc = new WgcCapture();
            image = capture(wgc);
            return true;
        }
        catch (Exception ex) when (ex is CaptureFailedException or COMException or InvalidOperationException or ArgumentException or SharpGen.Runtime.SharpGenException)
        {
            failure = ex;
            return false;
        }
    }

    private static CaptureFailedException Failure(string what, Exception? inner) =>
        new($"Windows.Graphics.Capture could not capture the {what}.", inner);
}
