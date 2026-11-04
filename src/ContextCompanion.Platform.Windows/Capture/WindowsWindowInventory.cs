using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ContextCompanion.Core.Capturing;
using ContextCompanion.Core.Privacy;
using static ContextCompanion.Platform.Windows.Interop.NativeMethods;

namespace ContextCompanion.Platform.Windows.Capture;

/// <summary>
/// Top-level windows as Windows reports them: frame bounds from <c>DWMWA_EXTENDED_FRAME_BOUNDS</c>, process name and
/// executable path from the owning process, class, title, minimised state, display affinity and monitor scale.
/// Visible windows are listed in z-order, topmost first, skipping hidden, minimised and cloaked windows (for example
/// windows on other virtual desktops and suspended UWP windows, <c>DWMWA_CLOAKED</c>).
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsWindowInventory : IWindowInventory
{
    /// <summary>At most this many top-level windows are examined; desktops rarely have more than a few hundred.</summary>
    public const int MaxWindows = 4096;

    public nint ForegroundWindow() => GetForegroundWindow();

    public WindowInfo? Describe(nint window)
    {
        if (window == 0 || !IsWindow(window) || GetAncestor(window, GA_ROOT) != window) return null;
        if (Displays.FrameBounds(window) is not { } bounds) return null;
        GetWindowThreadProcessId(window, out uint processId);
        string? path = ExecutablePathOf(processId);
        return new WindowInfo
        {
            Handle = window,
            Bounds = bounds,
            ProcessName = path is not null ? Path.GetFileName(path) : ProcessNameOf(processId),
            ExecutablePath = path,
            WindowClass = ClassOf(window),
            Title = TitleOf(window),
            IsMinimized = IsIconic(window),
            IsCaptureProtected = GetWindowDisplayAffinity(window, out uint affinity) && affinity != WDA_NONE,
            Scale = Displays.ScaleOf(MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST)),
        };
    }

    public IReadOnlyList<WindowInfo> VisibleWindows(ScreenRect area)
    {
        var result = new List<WindowInfo>();
        IntPtr window = GetTopWindow(IntPtr.Zero);
        for (int seen = 0; window != IntPtr.Zero && seen < MaxWindows; seen++, window = GetWindow(window, GW_HWNDNEXT))
        {
            if (!IsWindowVisible(window) || IsIconic(window) || IsCloaked(window)) continue;
            if (Displays.FrameBounds(window) is not { } bounds || bounds.Intersect(area).IsEmpty) continue;
            if (Describe(window) is { } info) result.Add(info);
        }
        return result;
    }

    private static bool IsCloaked(IntPtr window) =>
        DwmGetWindowAttribute(window, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static string? ClassOf(IntPtr window)
    {
        var buffer = new char[257];
        int length = GetClassName(window, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : null;
    }

    private static string? TitleOf(IntPtr window)
    {
        int length = GetWindowTextLength(window);
        if (length <= 0) return null;
        var buffer = new char[Math.Min(length, 4096) + 1];
        int read = GetWindowText(window, buffer, buffer.Length);
        return read > 0 ? new string(buffer, 0, read) : null;
    }

    /// <summary>
    /// The process name from the system's process list, for processes this one may not open (for example elevated
    /// ones), so that denylist rules by process name still apply to them.
    /// </summary>
    private static string? ProcessNameOf(uint processId)
    {
        if (processId == 0) return null;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            return process.ProcessName + ".exe";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    private static string? ExecutablePathOf(uint processId)
    {
        if (processId == 0) return null;
        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == IntPtr.Zero) return null; // for example an elevated process seen from a normal one
        try
        {
            var buffer = new char[32768];
            int size = buffer.Length;
            return QueryFullProcessImageName(process, 0, buffer, ref size) && size > 0 ? new string(buffer, 0, size) : null;
        }
        finally { CloseHandle(process); }
    }
}
