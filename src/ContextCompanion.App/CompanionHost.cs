using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ContextCompanion.Core.Capturing;
using ContextCompanion.Core.Delivery;
using ContextCompanion.Core.Packets;
using ContextCompanion.Core.Privacy;
using ContextCompanion.Core.Shell;
using ContextCompanion.Platform.Windows;
using ContextCompanion.Platform.Windows.Capture;
using ContextCompanion.Platform.Windows.Text;
using ContextCompanion.Platform.Windows.Triggers;
using TriggerAction = ContextCompanion.Core.Shell.TriggerAction;

namespace ContextCompanion.App;

/// <summary>
/// Everything behind the tray icon: settings, the privacy pipeline, the outbox, the inbox and the delivery loop.
/// Captures run one at a time on a background thread; the tray, the region selector and the preview run on the UI
/// thread. The same pieces as <c>ccomp capture</c>, so the command line and the app write identical packets.
/// </summary>
internal sealed class CompanionHost : IDisposable
{
    private readonly SemaphoreSlim oneAtATime = new(1, 1);
    private readonly CancellationTokenSource stop = new();
    private readonly Task deliveryLoop;

    private CompanionHost(string settingsPath, PrivacySettings settings)
    {
        SettingsPath = settingsPath;
        Settings = settings;
        Inbox = Path.GetFullPath(Environment.ExpandEnvironmentVariables(settings.App.Inbox ?? DefaultInbox));
        Directory.CreateDirectory(Inbox);
        Audit = new AuditLog(InboxRetention.DefaultAuditPath(Inbox));
        Outbox = new Outbox(DefaultOutbox, audit: Audit);
        Destination = new InboxDestination(Inbox, retention: settings.Retention.ToLimits(), audit: Audit);
        IPayloadProtector? protector = settings.Encryption.Enabled ? new DpapiCurrentUserProtector() : null;
        Pipeline = new PrivacyPipeline(settings, Outbox, Destination.Name, protector);
        Service = new CaptureService(Pipeline, new WindowsWindowInventory(), new WindowsScreenCapturer(),
            TextAcquisition.Create(new UiaSelectionReader(), new WindowsClipboard(), new WindowsKeyboard(), settings.Capture));
        Queue = new DeliveryQueue(Outbox, [Destination]);
        deliveryLoop = Task.Run(DeliverAsync);
    }

    /// <summary>
    /// The delivery loop. <see cref="DeliveryQueue.RunAsync"/> returns after an I/O failure (for example the inbox on a
    /// drive that went away); it is restarted after a pause so delivery resumes on its own.
    /// </summary>
    private async Task DeliverAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            await Queue.RunAsync(stop.Token).ConfigureAwait(false);
            try { await Task.Delay(TimeSpan.FromSeconds(5), stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary><c>%APPDATA%\context-companion\settings.json</c>; without it the documented defaults apply.</summary>
    public static string DefaultSettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "context-companion", "settings.json");

    /// <summary>The same outbox <c>ccomp capture</c> uses by default.</summary>
    public static string DefaultOutbox =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "context-companion", "outbox");

    public static string DefaultInbox =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ContextCompanion", "inbox");

    /// <summary>Loads the settings (or the defaults) and starts delivery. Throws <see cref="PrivacySettingsException"/>.</summary>
    public static CompanionHost Start(string? settingsPath = null)
    {
        string path = settingsPath ?? DefaultSettingsPath;
        return new CompanionHost(path, PrivacySettings.LoadOrDefault(path));
    }

    public string SettingsPath { get; }
    public PrivacySettings Settings { get; }
    public string Inbox { get; }
    public AuditLog Audit { get; }
    public Outbox Outbox { get; }
    public InboxDestination Destination { get; }
    public PrivacyPipeline Pipeline { get; }
    public CaptureService Service { get; }
    public DeliveryQueue Queue { get; }

    /// <summary>
    /// Runs one capture. Returns null when another capture is still running (the trigger is ignored rather than
    /// queued, so a burst of hotkeys never produces a burst of captures).
    /// </summary>
    public async Task<CaptureOutcome?> CaptureAsync(TriggerAction action, Func<Task<ScreenRect?>> selectRegion, (int X, int Y)? pointer = null)
    {
        if (!await oneAtATime.WaitAsync(0).ConfigureAwait(false)) return null;
        try
        {
            CaptureOutcome outcome;
            switch (action)
            {
                case TriggerAction.Window:
                    outcome = await Task.Run(Service.CaptureForegroundWindow).ConfigureAwait(false);
                    break;
                case TriggerAction.PointerRegion:
                {
                    var (x, y) = pointer ?? Pointer.Position();
                    var region = PointerRegion.Around(Pointer.MonitorAt(x, y), x, y, Settings.App.PointerRegionFraction);
                    outcome = await Task.Run(() => Service.CaptureRegion(region)).ConfigureAwait(false);
                    break;
                }
                case TriggerAction.Region:
                {
                    if (await selectRegion().ConfigureAwait(false) is not { } region)
                        return new CaptureOutcome { Status = CaptureOutcomeStatus.Discarded, Mode = "region", Message = "Region selection cancelled; nothing was captured." };
                    outcome = await Task.Run(() => Service.CaptureRegion(region)).ConfigureAwait(false);
                    break;
                }
                default:
                    // A hotkey such as Ctrl+Alt+T is still held when it fires; the copy keystroke must not combine with it.
                    if (!Pointer.WaitForModifiersReleased(TimeSpan.FromSeconds(2)))
                        return new CaptureOutcome { Status = CaptureOutcomeStatus.NoText, Mode = "text", Message = "No text captured: Ctrl, Alt, Shift or Win was still held." };
                    outcome = await Task.Run(() => Service.CaptureTextAsync(includeClipboard: false)).ConfigureAwait(false);
                    break;
            }
            if (outcome.Status is CaptureOutcomeStatus.Queued) Queue.Wake();
            return outcome;
        }
        catch (OutboxFullException ex)
        {
            return new CaptureOutcome { Status = CaptureOutcomeStatus.Failed, Message = ex.Message };
        }
        finally { oneAtATime.Release(); }
    }

    /// <summary>The outbox records, newest first, after reading any new acknowledgements from the inbox.</summary>
    public IReadOnlyList<OutboxRecord> Receipts()
    {
        Outbox.RefreshAcknowledgements([Destination]);
        return Outbox.Snapshot().OrderByDescending(r => r.Created).ToArray();
    }

    public void Dispose()
    {
        stop.Cancel();
        try { deliveryLoop.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
        Queue.Dispose();
        stop.Dispose();
        oneAtATime.Dispose();
    }
}
