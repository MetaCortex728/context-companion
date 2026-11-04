using System.Runtime.InteropServices;
using ContextCompanion.Platform.Windows.Capture;

// Desktop tests share the foreground window, the focus and the clipboard, so they must not run in parallel.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace ContextCompanion.Platform.Windows.Tests;

/// <summary>Why desktop tests cannot run in this session, or null when they can.</summary>
internal static class Desktop
{
    private const uint DESKTOP_READOBJECTS = 0x0001;

    public static string? Unavailable()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            return "Needs Windows 10 version 2004 or later with a desktop; the windows-latest CI job runs this test.";
        if (!Environment.UserInteractive)
            return "No interactive desktop: the process is not running in an interactive session.";
        IntPtr desktop = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (desktop == IntPtr.Zero)
            return "No interactive desktop: the input desktop cannot be opened (locked workstation, service session or no console).";
        CloseDesktop(desktop);
        return null;
    }

    public static string? WgcUnavailable()
    {
        if (Unavailable() is { } reason) return reason;
        return WindowsScreenCapturer.IsWgcSupported ? null
            : "Windows.Graphics.Capture is not supported in this session: GraphicsCaptureSession.IsSupported() returned false.";
    }

    private static readonly Lazy<string?> WindowItemRefusal = new(ProbeWindowItem);

    /// <summary>
    /// Why Windows.Graphics.Capture cannot capture windows here, or null. Some sessions accept monitor items but refuse
    /// every window item with E_INVALIDARG: the windows-latest runner (Windows Server 2025) does so for the test
    /// window. The app then falls back to GDI, which other tests cover. Only that exact refusal skips; any other
    /// failure lets the test run and fail.
    /// </summary>
    public static string? WgcWindowUnavailable() => WgcUnavailable() ?? WindowItemRefusal.Value;

    private static string? ProbeWindowItem()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) return null;
        try
        {
            using var window = new TestWindow(toolWindow: false);
            new WindowsScreenCapturer(CaptureMethods.WgcOnly).CaptureWindow(window.Handle, includeCursor: false);
            return null;
        }
        catch (ContextCompanion.Core.Capturing.CaptureFailedException ex) when (ex.InnerException?.Message.Contains("(0x80070057)") == true)
        {
            return $"Windows.Graphics.Capture refuses window items in this session (CreateForWindow returned E_INVALIDARG for a visible top-level window) on {Environment.OSVersion.VersionString}; monitor capture and the GDI fallback are tested separately.";
        }
        catch (Exception) { return null; }
    }

#pragma warning disable SYSLIB1054
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);
#pragma warning restore SYSLIB1054
}

/// <summary>A fact that needs an interactive Windows desktop; skipped with the reason otherwise.</summary>
public sealed class DesktopFactAttribute : FactAttribute
{
    public DesktopFactAttribute()
    {
        if (Desktop.Unavailable() is { } reason) Skip = reason;
    }
}

/// <summary>A fact that needs Windows.Graphics.Capture on an interactive desktop; skipped with the reason otherwise.</summary>
public sealed class WgcFactAttribute : FactAttribute
{
    public WgcFactAttribute()
    {
        if (Desktop.WgcUnavailable() is { } reason) Skip = reason;
    }
}

/// <summary>A fact that needs Windows.Graphics.Capture window items; skipped with the reason where the session refuses them.</summary>
public sealed class WgcWindowFactAttribute : FactAttribute
{
    public WgcWindowFactAttribute()
    {
        if (Desktop.WgcWindowUnavailable() is { } reason) Skip = reason;
    }
}

