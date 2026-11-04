using System.Runtime.Versioning;
using ContextCompanion.Core.Privacy;
using ContextCompanion.Platform.Windows.Interop;
using static ContextCompanion.Platform.Windows.Interop.NativeMethods;

namespace ContextCompanion.Platform.Windows.Capture;

/// <summary>DPI awareness and monitor geometry. All rectangles are physical pixels.</summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public static class Displays
{
    /// <summary>
    /// Makes the process Per-Monitor V2 DPI aware, so every rectangle it sees and captures is in physical pixels.
    /// The app declares this in its manifest; <c>ccomp capture</c> calls this before it touches any window. Returns
    /// true when the process is Per-Monitor V2 aware afterwards (including when it already was).
    /// </summary>
    public static bool EnsurePerMonitorV2()
    {
        if (SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) return true;
        // Already set (by a manifest or an earlier call): report whether it is Per-Monitor V2.
        return AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    }

    /// <summary>The effective scale of a monitor (1.0 at 96 DPI), or null if it cannot be read.</summary>
    internal static double? ScaleOf(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero) return null;
        return GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96.0 : null;
    }

    /// <summary>The monitor that holds most of the rectangle.</summary>
    internal static IntPtr NearestMonitor(ScreenRect rect)
    {
        var r = ToRect(rect);
        return MonitorFromRect(ref r, MONITOR_DEFAULTTONEAREST);
    }

    /// <summary>The monitor's full rectangle, or null.</summary>
    internal static ScreenRect? BoundsOf(IntPtr monitor)
    {
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info) ? FromRect(info.rcMonitor) : null;
    }

    /// <summary>
    /// The single monitor that contains the whole rectangle, or 0 when the rectangle spans monitors or lies partly
    /// off-screen.
    /// </summary>
    internal static IntPtr ContainingMonitor(ScreenRect rect)
    {
        IntPtr monitor = NearestMonitor(rect);
        return BoundsOf(monitor) is { } bounds && bounds.Intersect(rect) == rect ? monitor : IntPtr.Zero;
    }

    /// <summary>
    /// The window's visible frame: the DWM extended frame bounds, which leave out the invisible resize borders and the
    /// shadow; <c>GetWindowRect</c> when DWM cannot say. Null when the window has no area.
    /// </summary>
    internal static ScreenRect? FrameBounds(IntPtr window)
    {
        if (DwmGetWindowAttribute(window, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT frame, System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) == 0 &&
            frame.Width > 0 && frame.Height > 0)
            return FromRect(frame);
        return GetWindowRect(window, out var rect) && rect.Width > 0 && rect.Height > 0 ? FromRect(rect) : null;
    }

    internal static RECT ToRect(ScreenRect r) => new() { Left = r.X, Top = r.Y, Right = (int)r.Right, Bottom = (int)r.Bottom };
    internal static ScreenRect FromRect(RECT r) => new(r.Left, r.Top, r.Width, r.Height);

    /// <summary>A monitor handle's full rectangle, or null.</summary>
    public static ScreenRect? BoundsOfMonitor(IntPtr monitor) => BoundsOf(monitor);

    /// <summary>Every monitor's full rectangle, in the order Windows lists them.</summary>
    public static IReadOnlyList<ScreenRect> AllMonitors()
    {
        var monitors = new List<ScreenRect>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (handle, _, _, _) =>
        {
            if (BoundsOf(handle) is { } bounds) monitors.Add(bounds);
            return true;
        }, IntPtr.Zero);
        return monitors;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);

#pragma warning disable SYSLIB1054 // LibraryImport needs unsafe code, which the repository does not allow.
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
#pragma warning restore SYSLIB1054
}
