using System.Runtime.InteropServices;
using ContextCompanion.Core.Shell;
using ContextCompanion.Platform.Windows.Triggers;

namespace ContextCompanion.Platform.Windows.Tests;

public class TriggerTests
{
    [DesktopFact]
    public void ARegisteredHotkeyFiresItsActionAndIsReleasedOnDispose()
    {
        // An unusual chord, so it neither collides with the user's hotkeys nor does anything if it leaks.
        var chord = HotkeyChord.Parse("Ctrl+Alt+Shift+F24");
        using var fired = new ManualResetEventSlim();
        TriggerAction? seen = null;
        using (var hotkeys = new GlobalHotkeys([(TriggerAction.Region, chord)], action => { seen = action; fired.Set(); }))
        {
            Assert.Empty(hotkeys.Failed);
            Press(0x11, 0x12, 0x10, 0x87);
            Assert.True(fired.Wait(TimeSpan.FromSeconds(3)), "The hotkey did not fire.");
            Assert.Equal(TriggerAction.Region, seen);
        }
        // Released: a second registration of the same chord succeeds.
        using var again = new GlobalHotkeys([(TriggerAction.Window, chord)], _ => { });
        Assert.Empty(again.Failed);
    }

    [DesktopFact]
    public void AChordTakenByAnotherRegistrationIsReportedAsFailed()
    {
        var chord = HotkeyChord.Parse("Ctrl+Alt+Shift+F23");
        using var first = new GlobalHotkeys([(TriggerAction.Window, chord)], _ => { });
        using var second = new GlobalHotkeys([(TriggerAction.Text, chord)], _ => { });
        Assert.Empty(first.Failed);
        Assert.Equal(["Ctrl+Alt+Shift+F23 (text)"], second.Failed);
    }

    [DesktopFact]
    public void TheWheelGestureHookInstallsAndUninstalls()
    {
        using var hook = new WheelGestureHook((_, _, _) => { });
        Assert.True(hook.Installed);
    }

    [DesktopFact]
    public void ThePointerIsOnOneOfTheMonitors()
    {
        var (x, y) = Pointer.Position();
        var monitor = Pointer.MonitorAt(x, y);
        Assert.Contains(monitor, Pointer.Monitors());
        Assert.True(Pointer.WaitForModifiersReleased(TimeSpan.FromSeconds(2)));
    }

    private static void Press(params byte[] keys)
    {
        foreach (byte key in keys) keybd_event(key, 0, 0, UIntPtr.Zero);
        foreach (byte key in keys.Reverse()) keybd_event(key, 0, 2, UIntPtr.Zero);
    }

#pragma warning disable SYSLIB1054
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
#pragma warning restore SYSLIB1054
}
