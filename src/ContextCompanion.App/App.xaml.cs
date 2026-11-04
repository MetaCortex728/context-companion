using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ContextCompanion.Core.Capturing;
using ContextCompanion.Core.Privacy;
using ContextCompanion.Core.Shell;
using ContextCompanion.Platform.Windows.Triggers;
using TriggerAction = ContextCompanion.Core.Shell.TriggerAction;

namespace ContextCompanion.App;

/// <summary>
/// The tray app: a visible tray icon, global hotkeys, the optional horizontal-wheel gesture, a region selector, a
/// preview before sending and a view of delivery receipts. Every capture goes through the privacy pipeline to the
/// local inbox. Nothing is captured without a trigger.
/// </summary>
public partial class App : Application
{
    private Mutex? single;
    private CompanionHost? host;
    private TrayIcon? tray;
    private GlobalHotkeys? hotkeys;
    private WheelGestureHook? gesture;
    private ReceiptsWindow? receipts;
    private volatile bool preview;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        single = new Mutex(true, @"Local\context-companion", out bool first);
        if (!first)
        {
            MessageBox.Show("context-companion is already running. Its icon is in the notification area.", "context-companion");
            Shutdown();
            return;
        }

        // ContextCompanion.exe [--settings <file>]: another settings file, for example a second inbox or a test setup.
        int at = Array.IndexOf(e.Args, "--settings");
        string? settingsPath = at >= 0 && at + 1 < e.Args.Length ? Path.GetFullPath(e.Args[at + 1]) : null;
        try { host = CompanionHost.Start(settingsPath); }
        catch (Exception ex) when (ex is PrivacySettingsException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"context-companion could not start: {ex.Message}\n\nSettings file: {CompanionHost.DefaultSettingsPath}",
                "context-companion", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var app = host.Settings.App;
        preview = app.Preview;
        host.Service.Reviewer = (capture, mode) => preview ? Dispatcher.Invoke(() => PreviewWindow.Ask(capture, mode)) : new ReviewDecision(true);

        var chords = new List<(TriggerAction Action, HotkeyChord Chord)>();
        foreach (var (action, text) in new[] { (TriggerAction.Window, app.Hotkeys.Window), (TriggerAction.Region, app.Hotkeys.Region), (TriggerAction.Text, app.Hotkeys.Text) })
            if (text.Length > 0) chords.Add((action, HotkeyChord.Parse(text)));
        hotkeys = new GlobalHotkeys(chords, action => Dispatcher.BeginInvoke(() => Trigger(action)));

        tray = new TrayIcon(new TrayActions
        {
            WindowHotkey = app.Hotkeys.Window, RegionHotkey = app.Hotkeys.Region, TextHotkey = app.Hotkeys.Text,
            CaptureWindowSoon = () => Later(TriggerAction.Window),
            CaptureRegion = () => Trigger(TriggerAction.Region),
            CaptureTextSoon = () => Later(TriggerAction.Text),
            PreviewOn = () => preview,
            SetPreview = on => preview = on,
            GestureOn = () => gesture is not null,
            SetGesture = SetGesture,
            ShowReceipts = ShowReceipts,
            OpenInbox = () => Open(host.Inbox),
            OpenSettings = () => Open(Path.GetDirectoryName(host.SettingsPath)!),
            Exit = Shutdown,
        });
        if (app.WheelGesture) SetGesture(true);

        if (hotkeys.Failed.Count > 0)
            tray.Notify("context-companion", $"Some hotkeys are taken by another application: {string.Join(", ", hotkeys.Failed)}. Change them in settings.json.", warning: true);
        else
            tray.Notify("context-companion", $"Running. Hotkeys: {Describe(chords)}. Inbox: {host.Inbox}");
    }

    private static string Describe(List<(TriggerAction Action, HotkeyChord Chord)> chords) =>
        chords.Count == 0 ? "none" : string.Join(", ", chords.Select(c => $"{c.Chord} {c.Action.ToString().ToLowerInvariant()}"));

    private bool SetGesture(bool on)
    {
        if (!on) { gesture?.Dispose(); gesture = null; return false; }
        if (gesture is not null) return true;
        gesture = new WheelGestureHook((action, x, y) => Dispatcher.BeginInvoke(() => Trigger(action, (x, y))));
        if (gesture.Installed) return true;
        gesture.Dispose();
        gesture = null;
        tray?.Notify("context-companion", "The wheel gesture could not be enabled: Windows refused the mouse hook.", warning: true);
        return false;
    }

    /// <summary>From the tray menu the menu itself has the focus, so window and text captures wait 3 s for the user to go back.</summary>
    private void Later(TriggerAction action) =>
        Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => Dispatcher.BeginInvoke(() => Trigger(action)), TaskScheduler.Default);

    private async void Trigger(TriggerAction action, (int X, int Y)? pointer = null)
    {
        if (host is null || tray is null) return;
        CaptureOutcome? outcome;
        try
        {
            outcome = await host.CaptureAsync(action, () => new RegionSelector().SelectAsync(), pointer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            tray.Notify("Capture failed", ex.Message, warning: true);
            return;
        }
        if (outcome is null) return; // another capture is still running
        switch (outcome.Status)
        {
            case CaptureOutcomeStatus.Queued:
                string redactions = outcome.Redactions.Count == 0 ? "no redactions" : $"{outcome.Redactions.Count} redaction(s)";
                tray.Notify("Sent to the inbox", $"Packet {outcome.PacketId} ({outcome.Mode}), {redactions}.");
                break;
            case CaptureOutcomeStatus.Duplicate:
                tray.Notify("Not sent again", outcome.Message);
                break;
            case CaptureOutcomeStatus.Discarded:
                break;
            default:
                tray.Notify("Nothing captured", outcome.Message, warning: true);
                break;
        }
    }

    private void ShowReceipts()
    {
        if (host is null) return;
        if (receipts is { IsLoaded: true }) { receipts.Activate(); return; }
        receipts = new ReceiptsWindow(host);
        receipts.Closed += (_, _) => receipts = null;
        receipts.Show();
        receipts.Activate();
    }

    private static void Open(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        gesture?.Dispose();
        hotkeys?.Dispose();
        tray?.Dispose();
        host?.Dispose();
        single?.Dispose();
        base.OnExit(e);
    }
}
