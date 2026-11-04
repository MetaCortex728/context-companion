using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ContextCompanion.Platform.Windows.Capture;

namespace ContextCompanion.Platform.Windows.Tests;

/// <summary>An opaque colour, as the test expects to find it in captured BGRA pixels.</summary>
internal readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>The Win32 COLORREF (0x00BBGGRR).</summary>
    public uint ColorRef => (uint)(R | (G << 8) | (B << 16));
}

/// <summary>
/// A borderless, topmost test window on its own thread with a Win32 message loop: four solid colour blocks (red,
/// green, blue, yellow), each 200 x 150 pixels, and optionally an EDIT control holding "alpha beta gamma" with
/// "beta" selected. Plain Win32 (window classes with a solid background brush and DefWindowProc), so the test project
/// needs no desktop framework and still starts, and skips, on Linux.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class TestWindow : IDisposable
{
    public const int Width = 400, Height = 300;
    public const string BoxText = "alpha beta gamma";
    public const string ClassPrefix = "ccompTest";
    public static readonly Rgb[] Blocks = [new(255, 0, 0), new(0, 255, 0), new(0, 0, 255), new(255, 255, 0)];

    private const uint WM_APP = 0x8000, WM_CLOSE_TEST = WM_APP + 1, WM_FOCUS_BOX = WM_APP + 2;
    private const uint WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000, WS_CHILD = 0x40000000, WS_BORDER = 0x00800000;
    private const uint WS_EX_TOPMOST = 0x00000008, WS_EX_TOOLWINDOW = 0x00000080;
    private const uint EM_SETSEL = 0x00B1;
    private static readonly object RegistrationGate = new();
    private static bool registered;

    private readonly Thread thread;
    private uint threadId;
    private IntPtr box;

    public TestWindow(bool withTextBox = false, bool toolWindow = true)
    {
        Displays.EnsurePerMonitorV2();
        Title = "ccomp-test-window-" + Guid.NewGuid().ToString("N");
        using var shown = new ManualResetEventSlim();
        Exception? failure = null;
        thread = new Thread(() =>
        {
            try
            {
                threadId = GetCurrentThreadId();
                RegisterClasses();
                Handle = CreateWindowEx(WS_EX_TOPMOST | (toolWindow ? WS_EX_TOOLWINDOW : 0), ClassPrefix + "Frame", Title, WS_POPUP | WS_VISIBLE,
                    120, 120, Width, Height, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (Handle == IntPtr.Zero) throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastWin32Error()}).");
                for (int i = 0; i < 4; i++)
                    CreateWindowEx(0, ClassPrefix + "Block" + i, null, WS_CHILD | WS_VISIBLE, i % 2 * 200, i / 2 * 150, 200, 150,
                        Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (withTextBox)
                {
                    // RichEdit 4.1 implements UI Automation's TextPattern natively, as editors such as Notepad
                    // and browsers do; a plain EDIT without comctl32 v6 is served by the MSAA proxy, which has no TextPattern.
                    if (LoadLibrary("msftedit.dll") == IntPtr.Zero) throw new InvalidOperationException("msftedit.dll could not be loaded.");
                    box = CreateWindowEx(0, "RICHEDIT50W", BoxText, WS_CHILD | WS_VISIBLE | WS_BORDER, 20, 20, 360, 30, Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    FocusBox();
                }
                UpdateWindow(Handle);
                shown.Set();
                while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
                {
                    if (message.hwnd == IntPtr.Zero && message.message == WM_CLOSE_TEST) break;
                    if (message.hwnd == IntPtr.Zero && message.message == WM_FOCUS_BOX) { FocusBox(); continue; }
                    TranslateMessage(ref message);
                    DispatchMessage(ref message);
                }
                DestroyWindow(Handle);
            }
            catch (Exception ex) { failure = ex; shown.Set(); }
        });
        thread.IsBackground = true;
        thread.Start();
        if (!shown.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("The test window did not appear.");
        if (failure is not null) throw new InvalidOperationException("The test window could not be created.", failure);
        Thread.Sleep(500); // let the compositor present the first frames
    }

    public string Title { get; }
    public IntPtr Handle { get; private set; }

    /// <summary>Brings the window to the foreground and focuses the text box. Fails the test if Windows refuses.</summary>
    public void Activate()
    {
        // SetForegroundWindow is refused to a background process while someone else holds the foreground.
        // Attaching to the foreground window's input queue and a synthetic Alt press are the usual ways
        // a process the user started is allowed to take it; retry briefly because activation is asynchronous.
        // Another window can take the foreground back while the box is being focused, so a round
        // counts only if the window still holds the foreground after the focus has settled.
        for (int round = 0; round < 5; round++)
        {
            TakeForeground();
            PostThreadMessage(threadId, WM_FOCUS_BOX, IntPtr.Zero, IntPtr.Zero);
            Thread.Sleep(300);
            if (GetForegroundWindow() == Handle) return;
        }
        Assert.Fail("Windows did not let the test window take the foreground.");
    }

    private void TakeForeground()
    {
        for (int attempt = 0; attempt < 15 && GetForegroundWindow() != Handle; attempt++)
        {
            uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
            uint current = GetCurrentThreadId();
            bool attached = foregroundThread != 0 && foregroundThread != current && AttachThreadInput(current, foregroundThread, true);
            try
            {
                keybd_event(0x12, 0, 0, UIntPtr.Zero);
                keybd_event(0x12, 0, 2, UIntPtr.Zero);
                ShowWindow(Handle, 9 /* SW_RESTORE */);
                BringWindowToTop(Handle);
                SetForegroundWindow(Handle);
            }
            finally
            {
                if (attached) AttachThreadInput(current, foregroundThread, false);
            }
            Thread.Sleep(200);
        }
    }

    public void Dispose()
    {
        PostThreadMessage(threadId, WM_CLOSE_TEST, IntPtr.Zero, IntPtr.Zero);
        thread.Join(TimeSpan.FromSeconds(5));
    }

    /// <summary>Puts plain text on the clipboard the way an ordinary application does: no exclusion formats.</summary>
    public static void SetClipboardText(string text)
    {
        IntPtr owner = CreateWindowEx(0, "STATIC", null, 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        try
        {
            for (int attempt = 0; !OpenClipboard(owner); attempt++)
            {
                if (attempt > 50) throw new InvalidOperationException("The clipboard stayed busy.");
                Thread.Sleep(10);
            }
            try
            {
                EmptyClipboard();
                var bytes = new byte[(text.Length + 1) * 2];
                System.Text.Encoding.Unicode.GetBytes(text, 0, text.Length, bytes, 0);
                IntPtr memory = GlobalAlloc(0x0002, (UIntPtr)(ulong)bytes.Length);
                Marshal.Copy(bytes, 0, GlobalLock(memory), bytes.Length);
                GlobalUnlock(memory);
                if (SetClipboardData(13, memory) == IntPtr.Zero) throw new InvalidOperationException("SetClipboardData failed.");
            }
            finally { CloseClipboard(); }
        }
        finally { DestroyWindow(owner); }
    }

    private void FocusBox()
    {
        if (box == IntPtr.Zero) return;
        SetFocus(box);
        SendMessage(box, EM_SETSEL, new IntPtr(6), new IntPtr(10)); // "beta"
    }

    private static void RegisterClasses()
    {
        lock (RegistrationGate)
        {
            if (registered) return;
            IntPtr defWindowProc = GetProcAddress(GetModuleHandle("user32.dll"), "DefWindowProcW");
            IntPtr instance = GetModuleHandle(null);
            Register(ClassPrefix + "Frame", new Rgb(255, 255, 255));
            for (int i = 0; i < 4; i++) Register(ClassPrefix + "Block" + i, Blocks[i]);
            registered = true;

            void Register(string name, Rgb colour)
            {
                var wc = new WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                    lpfnWndProc = defWindowProc,
                    hInstance = instance,
                    hbrBackground = CreateSolidBrush(colour.ColorRef),
                    lpszClassName = name,
                };
                if (RegisterClassEx(ref wc) == 0) throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastWin32Error()}).");
            }
        }
    }

#pragma warning disable SYSLIB1054, IDE1006
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public int ptX, ptY;
        public uint lPrivate;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClassExW", SetLastError = true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW", SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string? windowName, uint style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateWindow(IntPtr hWnd);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")] private static extern int GetMessage(out MSG message, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(ref MSG message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern IntPtr DispatchMessage(ref MSG message);
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadLibraryW", SetLastError = true)] private static extern IntPtr LoadLibrary(string name);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool doAttach);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr data);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint colorRef);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, BestFitMapping = false)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalUnlock(IntPtr memory);
#pragma warning restore SYSLIB1054, IDE1006
}
