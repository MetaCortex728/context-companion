using System;
using System.Drawing;
using Forms = System.Windows.Forms;

namespace ContextCompanion.App;

/// <summary>
/// The notification-area icon and its menu. Always visible while the app runs: the tool never hides that it is
/// running. Outcomes of captures are shown as notifications from this icon.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const string IconResource = "ContextCompanion.App.Assets.companion.ico";
    private readonly Forms.NotifyIcon notifyIcon;
    private readonly Forms.ContextMenuStrip menu;
    private readonly Icon icon;

    public TrayIcon(TrayActions actions)
    {
        icon = LoadIcon();
        menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add(Item("Capture window in 3 s", actions.WindowHotkey, actions.CaptureWindowSoon));
        menu.Items.Add(Item("Capture region...", actions.RegionHotkey, actions.CaptureRegion));
        menu.Items.Add(Item("Capture selected text in 3 s", actions.TextHotkey, actions.CaptureTextSoon));
        menu.Items.Add(new Forms.ToolStripSeparator());
        var preview = new Forms.ToolStripMenuItem("Preview before sending") { CheckOnClick = true, Checked = actions.PreviewOn() };
        preview.CheckedChanged += (_, _) => actions.SetPreview(preview.Checked);
        menu.Items.Add(preview);
        var gesture = new Forms.ToolStripMenuItem("Horizontal-wheel gesture") { CheckOnClick = true, Checked = actions.GestureOn() };
        gesture.Click += (_, _) => gesture.Checked = actions.SetGesture(gesture.Checked);
        menu.Items.Add(gesture);
        menu.Opening += (_, _) => { preview.Checked = actions.PreviewOn(); gesture.Checked = actions.GestureOn(); };
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Item("Deliveries...", "", actions.ShowReceipts));
        menu.Items.Add(Item("Open inbox folder", "", actions.OpenInbox));
        menu.Items.Add(Item("Open settings folder", "", actions.OpenSettings));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Item("Exit context-companion", "", actions.Exit));
        notifyIcon = new Forms.NotifyIcon
        {
            Icon = icon,
            Text = "context-companion",
            ContextMenuStrip = menu,
            Visible = true,
        };
        notifyIcon.DoubleClick += (_, _) => actions.ShowReceipts();
    }

    private static Forms.ToolStripMenuItem Item(string text, string shortcut, Action action)
    {
        var item = new Forms.ToolStripMenuItem(text) { ShortcutKeyDisplayString = shortcut, ShowShortcutKeys = shortcut.Length > 0 };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>Shows a short notification. Messages name modes, rules and counts, never content or titles.</summary>
    public void Notify(string title, string text, bool warning = false) =>
        notifyIcon.ShowBalloonTip(4000, title, text.Length > 250 ? text[..250] + "..." : text,
            warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);

    private static Icon LoadIcon()
    {
        using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream(IconResource)
            ?? throw new InvalidOperationException("The companion icon resource is missing.");
        using var loaded = new Icon(stream, Forms.SystemInformation.SmallIconSize);
        return (Icon)loaded.Clone();
    }

    public void Dispose()
    {
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        menu.Dispose();
        icon.Dispose();
    }
}

/// <summary>What the tray menu does; supplied by the app.</summary>
internal sealed record TrayActions
{
    public required string WindowHotkey { get; init; }
    public required string RegionHotkey { get; init; }
    public required string TextHotkey { get; init; }
    public required Action CaptureWindowSoon { get; init; }
    public required Action CaptureRegion { get; init; }
    public required Action CaptureTextSoon { get; init; }
    public required Func<bool> PreviewOn { get; init; }
    public required Action<bool> SetPreview { get; init; }
    public required Func<bool> GestureOn { get; init; }
    /// <summary>Returns whether the gesture is on afterwards (installing the hook can fail).</summary>
    public required Func<bool, bool> SetGesture { get; init; }
    public required Action ShowReceipts { get; init; }
    public required Action OpenInbox { get; init; }
    public required Action OpenSettings { get; init; }
    public required Action Exit { get; init; }
}
