using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ContextCompanion.Core.Privacy;
using ContextCompanion.Platform.Windows.Capture;

namespace ContextCompanion.Platform.Windows.Triggers;

#pragma warning disable SYSLIB1054 // LibraryImport needs unsafe code, which the repository does not allow.

/// <summary>The pointer, the monitors and the keyboard modifiers, in physical pixels.</summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public static class Pointer
{
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>The pointer position.</summary>
    public static (int X, int Y) Position() => GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);

    /// <summary>The full rectangle of the monitor under a point (the nearest one when the point is off-screen).</summary>
    public static ScreenRect MonitorAt(int x, int y)
    {
        IntPtr monitor = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
        return Displays.BoundsOfMonitor(monitor) ?? throw new InvalidOperationException("The monitor under the pointer could not be read.");
    }

    /// <summary>Every monitor's full rectangle.</summary>
    public static IReadOnlyList<ScreenRect> Monitors() => Displays.AllMonitors();

    /// <summary>
    /// Waits until Ctrl, Alt, Shift and Win are all released, so that a copy keystroke sent after a hotkey such as
    /// Ctrl+Alt+T is not seen as Ctrl+Alt+C. Returns false when they are still held after <paramref name="timeout"/>.
    /// </summary>
    public static bool WaitForModifiersReleased(TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (true)
        {
            if (!Held(VK_CONTROL) && !Held(VK_MENU) && !Held(VK_SHIFT) && !Held(VK_LWIN) && !Held(VK_RWIN)) return true;
            if (Environment.TickCount64 >= deadline) return false;
            Thread.Sleep(15);
        }
    }

    private static bool Held(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT point, uint flags);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
}
