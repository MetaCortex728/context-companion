using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using ContextCompanion.Core.Privacy;
using ContextCompanion.Core.Shell;
using ContextCompanion.Platform.Windows.Triggers;

namespace ContextCompanion.App;

/// <summary>
/// Lets the user drag out a region: one dimmed, borderless window over each monitor, placed in physical pixels. The
/// drag stays on the monitor where it started. Esc or a right click cancels. The windows close, and the screen is
/// given a moment to repaint, before the region is returned, so they never appear in the capture.
/// </summary>
internal sealed class RegionSelector
{
    private const int MinimumSize = 4;
    private readonly List<Window> overlays = [];
    private TaskCompletionSource<ScreenRect?>? result;
    private (int X, int Y)? start;
    private ScreenRect startMonitor;

    /// <summary>Shows the overlays and waits for a selection. Must be called on the UI thread.</summary>
    public async Task<ScreenRect?> SelectAsync()
    {
        result = new TaskCompletionSource<ScreenRect?>();
        foreach (var monitor in Pointer.Monitors()) overlays.Add(CreateOverlay(monitor));
        foreach (var overlay in overlays) overlay.Show();
        overlays.FirstOrDefault(o => o.IsMouseOver)?.Activate();
        overlays.FirstOrDefault()?.Activate();
        var region = await result.Task;
        foreach (var overlay in overlays) overlay.Close();
        overlays.Clear();
        if (region is not null) await Task.Delay(200); // let the compositor remove the overlays before capture
        return region;
    }

    private Window CreateOverlay(ScreenRect monitor)
    {
        var canvas = new Canvas { Background = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)), Cursor = Cursors.Cross };
        var frame = new Rectangle
        {
            Stroke = Brushes.White, StrokeThickness = 1, StrokeDashArray = [4, 3],
            Fill = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), Visibility = Visibility.Collapsed,
        };
        var hint = new TextBlock
        {
            Text = "Drag to select a region. Esc cancels.", Foreground = Brushes.White, FontSize = 14,
            Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), Padding = new Thickness(8, 4, 8, 4),
        };
        Canvas.SetLeft(hint, 16);
        Canvas.SetTop(hint, 16);
        canvas.Children.Add(frame);
        canvas.Children.Add(hint);

        var window = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, ShowActivated = true,
            Title = "context-companion region", Content = canvas, Left = 0, Top = 0, Width = 1, Height = 1,
        };
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            SetWindowPos(handle, new IntPtr(-1) /* HWND_TOPMOST */, monitor.X, monitor.Y, monitor.Width, monitor.Height, 0x0040 /* SWP_SHOWWINDOW */);
        };

        window.KeyDown += (_, e) => { if (e.Key == Key.Escape) Finish(null); };
        canvas.MouseRightButtonDown += (_, _) => Finish(null);
        canvas.MouseLeftButtonDown += (_, e) =>
        {
            start = Pointer.Position();
            startMonitor = monitor;
            canvas.CaptureMouse();
            hint.Visibility = Visibility.Collapsed;
            e.Handled = true;
        };
        canvas.MouseMove += (_, _) =>
        {
            if (start is null || !canvas.IsMouseCaptured) return;
            var rect = Current();
            var topLeft = window.PointFromScreen(new Point(rect.X, rect.Y));
            var bottomRight = window.PointFromScreen(new Point(rect.Right, rect.Bottom));
            Canvas.SetLeft(frame, topLeft.X);
            Canvas.SetTop(frame, topLeft.Y);
            frame.Width = Math.Max(0, bottomRight.X - topLeft.X);
            frame.Height = Math.Max(0, bottomRight.Y - topLeft.Y);
            frame.Visibility = Visibility.Visible;
        };
        canvas.MouseLeftButtonUp += (_, _) =>
        {
            if (start is null) return;
            canvas.ReleaseMouseCapture();
            var rect = Current();
            Finish(rect.Width >= MinimumSize && rect.Height >= MinimumSize ? rect : null);
        };
        window.Deactivated += (_, _) =>
        {
            // Another application took the focus (for example a notification): give up rather than hang.
            if (!overlays.Any(o => o.IsActive) && start is null) window.Dispatcher.BeginInvoke(() =>
            {
                if (!overlays.Any(o => o.IsActive)) Finish(null);
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        };
        return window;
    }

    /// <summary>The rectangle from the drag start to the pointer, kept on the starting monitor.</summary>
    private ScreenRect Current()
    {
        var (x0, y0) = start!.Value;
        var (x1, y1) = Pointer.Position();
        x1 = Math.Clamp(x1, startMonitor.X, (int)startMonitor.Right - 1);
        y1 = Math.Clamp(y1, startMonitor.Y, (int)startMonitor.Bottom - 1);
        return PointerRegion.FromCorners(x0, y0, x1, y1);
    }

    private void Finish(ScreenRect? region)
    {
        start = null;
        result?.TrySetResult(region);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
