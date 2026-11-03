using Forms = System.Windows.Forms;

namespace DomoCompanion;

internal static class TrayControls
{
    internal static bool HasMenuModifiers(Forms.Keys keys) =>
        (keys & (Forms.Keys.Control | Forms.Keys.Alt)) == (Forms.Keys.Control | Forms.Keys.Alt);

    internal static Forms.ContextMenuStrip CreateMenu(Action open, Action toggle, Action exit, Func<bool>? allowOpen = null)
    {
        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
        menu.Opening += (_, e) => e.Cancel = !(allowOpen?.Invoke() ?? HasMenuModifiers(Forms.Control.ModifierKeys));
        menu.Items.Add("Open controls", null, (_, _) => open());
        menu.Items.Add("Show / hide overlay", null, (_, _) => toggle());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit companion", null, (_, _) => exit());
        return menu;
    }
}
