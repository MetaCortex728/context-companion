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

