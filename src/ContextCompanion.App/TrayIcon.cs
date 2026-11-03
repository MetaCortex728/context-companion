using System;
using System.Drawing;
using Forms = System.Windows.Forms;

namespace ContextCompanion.App;

/// <summary>The notification-area icon and its menu. Always visible while the app runs.</summary>
internal sealed class TrayIcon : IDisposable
{
    private const string IconResource = "ContextCompanion.App.Assets.companion.ico";
    private readonly Forms.NotifyIcon notifyIcon;
    private readonly Forms.ContextMenuStrip menu;
    private readonly Icon icon;

    public TrayIcon(Action exit)
    {
        icon = LoadIcon();
        menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add("Exit context-companion", null, (_, _) => exit());
        notifyIcon = new Forms.NotifyIcon
        {
            Icon = icon,
            Text = "context-companion",
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

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
