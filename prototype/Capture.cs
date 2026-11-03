using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DomoCompanion;

public sealed record CaptureRequest(string Mode, long Window, uint ProcessId, int X, int Y);
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

public static class Capture
{
    public const int MaxPngBytes = 16 * 1024 * 1024;
    public const int MaxPixels = 12 * 1024 * 1024;
    public static PixelRect Crop(PixelRect monitor, int x, int y)
    {
        if (monitor.Width < 1 || monitor.Height < 1) throw new ArgumentException("Invalid monitor bounds.");
        int width = Math.Max(1, (int)Math.Round(monitor.Width * 0.4));
        int height = Math.Max(1, (int)Math.Round(monitor.Height * 0.4));
        return new(Math.Clamp(x - width / 2, monitor.X, monitor.X + monitor.Width - width),
            Math.Clamp(y - height / 2, monitor.Y, monitor.Y + monitor.Height - height), width, height);
    }
    public static bool Fits(PixelRect rectangle) => rectangle.Width > 0 && rectangle.Height > 0 &&
        (long)rectangle.Width * rectangle.Height <= MaxPixels;

    public static int Worker()
    {
        try
        {
            SetThreadDpiAwarenessContext(new IntPtr(-4));
            string line = Console.ReadLine() ?? "";
            if (line.Length > 1024) return 2;
            var request = JsonSerializer.Deserialize<CaptureRequest>(line);
            if (request is null || request.Mode is not ("window" or "cursor")) return 2;
            PixelRect bounds;
            IntPtr window = new(request.Window);
            if (request.Mode == "window")
            {
                if (!IsWindow(window) || !IsWindowVisible(window) || IsIconic(window) || window == GetDesktopWindow() || window == GetShellWindow()) return 2;
                GetWindowThreadProcessId(window, out uint pid);
                if (pid != request.ProcessId || !GetWindowRect(window, out var rect)) return 2;
                if (GetWindowDisplayAffinity(window, out uint affinity) && affinity != 0) return 2;
                bounds = new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            }
            else
            {
                var monitor = MonitorFromPoint(new Point(request.X, request.Y), 2);
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfo(monitor, ref info)) return 2;
                bounds = Crop(new(info.Bounds.Left, info.Bounds.Top, info.Bounds.Right - info.Bounds.Left,
                    info.Bounds.Bottom - info.Bounds.Top), request.X, request.Y);
            }
            if (!Fits(bounds)) return 2;
            using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Black);
                if (request.Mode == "window")
                {
                    var dc = graphics.GetHdc();
                    bool success;
                    try { success = PrintWindow(window, dc, 2); }
                    finally { graphics.ReleaseHdc(dc); }
                    GetWindowThreadProcessId(window, out uint currentPid);
                    if (!success || currentPid != request.ProcessId || !IsWindow(window)) return 3;
                }
                else graphics.CopyFromScreen(bounds.X, bounds.Y, 0, 0, new Size(bounds.Width, bounds.Height), CopyPixelOperation.SourceCopy);
            }
            // Some GPU/protected windows report success but return no useful pixels.
            bool varied = false;
            var first = bitmap.GetPixel(0, 0).ToArgb();
            for (int y = 0; y < bitmap.Height && !varied; y += Math.Max(1, bitmap.Height / 32))
                for (int x = 0; x < bitmap.Width && !varied; x += Math.Max(1, bitmap.Width / 32))
                    varied = bitmap.GetPixel(x, y).ToArgb() != first;
            if (!varied) return 3;
            using var png = new MemoryStream();
            bitmap.Save(png, ImageFormat.Png);
            if (png.Length > MaxPngBytes) return 4;
            png.Position = 0;
            using var output = Console.OpenStandardOutput();
            png.CopyTo(output); return 0;
        }
        catch { return 5; }
    }
    public static async Task<byte[]> Run(CaptureRequest request, CancellationToken stop)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
        {
            Arguments = "--capture-worker", UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        }) ?? throw new IOException("Capture worker could not start.");
        try
        {
            await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), deadline.Token);
            child.StandardInput.Close();
            using var bytes = new MemoryStream();
            var buffer = new byte[65536];
            int count;
            while ((count = await child.StandardOutput.BaseStream.ReadAsync(buffer, deadline.Token)) != 0)
            {
                if (bytes.Length + count > MaxPngBytes) throw new IOException("Capture exceeds the image limit.");
                bytes.Write(buffer, 0, count);
            }
            await child.WaitForExitAsync(deadline.Token);
            if (child.ExitCode != 0 || bytes.Length < 24) throw new IOException("Window unavailable, blank or unsupported; no image sent.");
            return bytes.ToArray();
        }
        finally
        {
            try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Bounds, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
}

public sealed class CaptureMouse : IDisposable
{
    private readonly Thread thread;
    private readonly Hook callback;
    private readonly Action<CaptureRequest> captured;
    private readonly Action<bool> ready;
    private uint threadId;
    private IntPtr hook;
    private volatile bool stopping;
    private long lastTrigger;
    public CaptureMouse(Action<CaptureRequest> captured, Action<bool> ready)
    {
        this.captured = captured; this.ready = ready; callback = OnMouse;
        thread = new Thread(Loop) { IsBackground = true, Name = "Domo mouse trigger" };
        thread.Start();
    }
    internal static string? Mode(int message, uint data, uint flags) => message == 0x20e && (flags & 3) == 0
        ? unchecked((short)(data >> 16)) switch { < 0 => "window", > 0 => "cursor", _ => null } : null;
    private void Loop()
    {
        threadId = GetCurrentThreadId();
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        try
        {
            hook = SetWindowsHookEx(14, callback, GetModuleHandle(null), 0);
            ready(hook != IntPtr.Zero);
            if (hook == IntPtr.Zero) return;
            while (!stopping && GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            { TranslateMessage(ref message); DispatchMessage(ref message); }
        }
        finally { if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook); }
    }
    private IntPtr OnMouse(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0 && !stopping && w.ToInt64() == 0x20e)
        {
            var data = Marshal.PtrToStructure<MousePacket>(l);
            string? mode = Mode((int)w, data.MouseData, data.Flags);
            if (mode is not null)
            {
                long now = Environment.TickCount64;
                if (now - lastTrigger >= 700)
                {
                    lastTrigger = now;
                    var foreground = Capture.GetForegroundWindow();
                    Capture.GetWindowThreadProcessId(foreground, out uint pid);
                    try { captured(new(mode, foreground.ToInt64(), pid, data.Position.X, data.Position.Y)); }
                    catch { /* Never let a managed callback unwind through the Windows input hook. */ }
                }
                return new IntPtr(1);
            }
        }
        return CallNextHookEx(hook, code, w, l);
    }
    public void Dispose()
    {
        stopping = true;
        if (threadId != 0) PostThreadMessage(threadId, 0x12, IntPtr.Zero, IntPtr.Zero);
        if (Thread.CurrentThread != thread) thread.Join(1500);
    }
    private delegate IntPtr Hook(int code, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] private struct MousePacket { public Point Position; public uint MouseData, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public IntPtr Hwnd; public uint Id; public UIntPtr W; public IntPtr L; public uint Time; public Point Position; public uint Private; }
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, Hook callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint message, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
