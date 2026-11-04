using System.Windows;

namespace ContextCompanion.App;

/// <summary>
/// Tray-app skeleton: shows a visible tray icon and exits from the tray menu.
/// It captures nothing; capture, triggers and delivery arrive in later Stage 1 steps.
/// </summary>
public partial class App : Application
{
    private TrayIcon? tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        tray = new TrayIcon(exit: Shutdown);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        tray?.Dispose();
        tray = null;
        base.OnExit(e);
    }
}
