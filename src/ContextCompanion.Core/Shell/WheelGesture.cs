using ContextCompanion.Core.Privacy;

namespace ContextCompanion.Core.Shell;

/// <summary>What a trigger asks for.</summary>
public enum TriggerAction
{
    /// <summary>The foreground window.</summary>
    Window,
    /// <summary>A region the user drags out.</summary>
    Region,
    /// <summary>A region of fixed size centred on the pointer.</summary>
    PointerRegion,
    /// <summary>The selected text of the focused application.</summary>
    Text,
}

/// <summary>
/// The horizontal-wheel gesture, kept from the prototype: tilting left captures the foreground window, tilting right a
/// region around the pointer. Injected events are ignored, and triggers closer together than the debounce interval
/// are dropped, because one tilt of a wheel produces a burst of events.
/// </summary>
public sealed class WheelGesture(TimeSpan? debounce = null)
{
    public const int WM_MOUSEHWHEEL = 0x020E;
    private const uint LLMHF_INJECTED = 0x01, LLMHF_LOWER_IL_INJECTED = 0x02;

    private readonly long debounceMs = (long)(debounce ?? TimeSpan.FromMilliseconds(700)).TotalMilliseconds;
    private long lastTrigger = long.MinValue;

    /// <summary>The action for one low-level mouse event, or null when it is not a gesture.</summary>
    public static TriggerAction? Classify(int message, uint mouseData, uint flags)
    {
        if (message != WM_MOUSEHWHEEL || (flags & (LLMHF_INJECTED | LLMHF_LOWER_IL_INJECTED)) != 0) return null;
        short delta = unchecked((short)(mouseData >> 16));
        return delta switch
        {
            < 0 => TriggerAction.Window,
            > 0 => TriggerAction.PointerRegion,
            _ => null,
        };
    }

    /// <summary>
    /// Classifies an event and applies the debounce. Returns the action to run, or null. <paramref name="swallow"/> is
    /// true for every gesture event, including debounced ones, so the tilt does not also scroll the window under it.
    /// </summary>
    public TriggerAction? Accept(int message, uint mouseData, uint flags, long nowMs, out bool swallow)
    {
        var action = Classify(message, mouseData, flags);
        swallow = action is not null;
        if (action is null) return null;
        if (lastTrigger != long.MinValue && nowMs - lastTrigger < debounceMs) return null;
        lastTrigger = nowMs;
        return action;
    }
}

public static class PointerRegion
{
    /// <summary>
    /// A region covering <paramref name="fraction"/> of the monitor's width and height, centred on the pointer and
    /// moved inside the monitor when the pointer is near an edge.
    /// </summary>
    public static ScreenRect Around(ScreenRect monitor, int x, int y, double fraction = 0.4)
    {
        if (monitor.IsEmpty) throw new ArgumentException("The monitor has no area.", nameof(monitor));
        if (!(fraction > 0 && fraction <= 1)) throw new ArgumentOutOfRangeException(nameof(fraction));
        int width = Math.Max(1, (int)Math.Round(monitor.Width * fraction));
        int height = Math.Max(1, (int)Math.Round(monitor.Height * fraction));
        return new ScreenRect(
            Math.Clamp(x - width / 2, monitor.X, monitor.X + monitor.Width - width),
            Math.Clamp(y - height / 2, monitor.Y, monitor.Y + monitor.Height - height),
            width, height);
    }

    /// <summary>The rectangle spanned by two corner points, inclusive of both, in physical pixels.</summary>
    public static ScreenRect FromCorners(int x1, int y1, int x2, int y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1) + 1, Math.Abs(y2 - y1) + 1);
}
