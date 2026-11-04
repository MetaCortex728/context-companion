using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ContextCompanion.Core.Shell;

namespace ContextCompanion.Platform.Windows.Triggers;

#pragma warning disable SYSLIB1054 // LibraryImport needs unsafe code, which the repository does not allow.

/// <summary>
/// The horizontal-wheel gesture through a low-level mouse hook on a thread of its own. The hook procedure only
/// classifies the event and hands the action to the thread pool, so it never blocks input. Windows silently removes a
/// low-level hook whose procedure is too slow (for example while the PC resumes from sleep), so a watchdog reinstalls
/// the hook every <see cref="WatchdogInterval"/>; reinstalling is cheap and needs no detection.
/// Only horizontal-wheel events are read; every other mouse event is passed on untouched and not recorded.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WheelGestureHook : IDisposable
{
    public static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(30);

    private const int WH_MOUSE_LL = 14;
    private const uint WM_QUIT = 0x0012, WM_APP_REINSTALL = 0x8001;

    private readonly Thread thread;
    private readonly HookProc callback;
    private readonly WheelGesture gesture = new();
    private readonly Action<TriggerAction, int, int> triggered;
    private readonly ManualResetEventSlim started = new();
    private Timer? watchdog;
    private IntPtr hook;
    private uint threadId;
    private volatile bool stopping;

    /// <summary>Installs the hook. <paramref name="triggered"/> receives the action and the pointer position.</summary>
    public WheelGestureHook(Action<TriggerAction, int, int> triggered)
    {
        this.triggered = triggered ?? throw new ArgumentNullException(nameof(triggered));
        callback = OnMouse;
        thread = new Thread(Loop) { IsBackground = true, Name = "context-companion wheel gesture" };
        thread.Start();
        started.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>True while the hook is installed.</summary>
    public bool Installed => hook != IntPtr.Zero;

    /// <summary>How many times the watchdog has reinstalled the hook.</summary>
    public int Reinstalls { get; private set; }

    private void Loop()
    {
        threadId = GetCurrentThreadId();
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        Install();
        watchdog = new Timer(_ => PostThreadMessage(threadId, WM_APP_REINSTALL, IntPtr.Zero, IntPtr.Zero), null, WatchdogInterval, WatchdogInterval);
        started.Set();
        try
        {
            while (!stopping && GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                if (message.hwnd == IntPtr.Zero && message.message == WM_APP_REINSTALL)
                {
                    Uninstall();
                    Install();
                    Reinstalls++;
                    continue;
                }
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        finally { Uninstall(); }
    }

    private void Install() => hook = SetWindowsHookEx(WH_MOUSE_LL, callback, GetModuleHandle(null), 0);

    private void Uninstall()
    {
        if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
    }

    private IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && !stopping && (int)wParam == WheelGesture.WM_MOUSEHWHEEL)
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var action = gesture.Accept((int)wParam, data.mouseData, data.flags, Environment.TickCount64, out bool swallow);
            if (action is { } a)
            {
                int x = data.pt.X, y = data.pt.Y;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { triggered(a, x, y); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { /* the app reports its own failures */ }
                });
            }
            if (swallow) return new IntPtr(1);
        }
        return CallNextHookEx(hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        stopping = true;
        watchdog?.Dispose();
        if (threadId != 0) PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        if (Thread.CurrentThread != thread) thread.Join(TimeSpan.FromSeconds(2));
        started.Dispose();
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")] private static extern int GetMessage(out MSG message, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out MSG message, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(ref MSG message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern IntPtr DispatchMessage(ref MSG message);
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
