using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ContextCompanion.Core.Shell;

namespace ContextCompanion.Platform.Windows.Triggers;

#pragma warning disable SYSLIB1054 // LibraryImport needs unsafe code, which the repository does not allow.

/// <summary>
/// System-wide hotkeys through <c>RegisterHotKey</c>, on a thread of their own with its own message loop. Windows
/// refuses a hotkey that another application already holds; <see cref="Failed"/> lists those, so the app can say so.
/// The callback runs on the hotkey thread and must return quickly.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class GlobalHotkeys : IDisposable
{
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    private const uint WM_HOTKEY = 0x0312, WM_QUIT = 0x0012;

    private readonly Thread thread;
    private readonly ManualResetEventSlim started = new();
    private readonly Action<TriggerAction> triggered;
    private readonly Dictionary<int, TriggerAction> ids = [];
    private readonly List<string> failed = [];
    private uint threadId;

    /// <summary>Registers each (action, chord) pair. Returns once registration has been attempted for all of them.</summary>
    public GlobalHotkeys(IEnumerable<(TriggerAction Action, HotkeyChord Chord)> hotkeys, Action<TriggerAction> triggered)
    {
        ArgumentNullException.ThrowIfNull(hotkeys);
        this.triggered = triggered ?? throw new ArgumentNullException(nameof(triggered));
        var list = hotkeys.ToList();
        thread = new Thread(() => Loop(list)) { IsBackground = true, Name = "context-companion hotkeys" };
        thread.Start();
        started.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Hotkeys Windows refused, as text such as <c>Ctrl+Alt+W (window)</c>.</summary>
    public IReadOnlyList<string> Failed { get { lock (failed) return failed.ToArray(); } }

    private void Loop(List<(TriggerAction Action, HotkeyChord Chord)> hotkeys)
    {
        threadId = GetCurrentThreadId();
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // creates the thread's message queue before anyone posts to it
        int next = 1;
        foreach (var (action, chord) in hotkeys)
        {
            int id = next++;
            if (RegisterHotKey(IntPtr.Zero, id, Modifiers(chord.Modifiers) | MOD_NOREPEAT, (uint)chord.VirtualKey)) ids[id] = action;
            else lock (failed) failed.Add($"{chord} ({action.ToString().ToLowerInvariant()})");
        }
        started.Set();
        try
        {
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                if (message.message == WM_HOTKEY && ids.TryGetValue((int)message.wParam, out var action))
                {
                    try { triggered(action); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { /* the app reports its own failures */ }
                }
            }
        }
        finally
        {
            foreach (int id in ids.Keys) UnregisterHotKey(IntPtr.Zero, id);
        }
    }

    private static uint Modifiers(HotkeyModifiers m) =>
        (m.HasFlag(HotkeyModifiers.Alt) ? MOD_ALT : 0) | (m.HasFlag(HotkeyModifiers.Ctrl) ? MOD_CONTROL : 0) |
        (m.HasFlag(HotkeyModifiers.Shift) ? MOD_SHIFT : 0) | (m.HasFlag(HotkeyModifiers.Win) ? MOD_WIN : 0);

    public void Dispose()
    {
        if (threadId != 0) PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        if (Thread.CurrentThread != thread) thread.Join(TimeSpan.FromSeconds(2));
        started.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")] private static extern int GetMessage(out MSG message, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out MSG message, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
