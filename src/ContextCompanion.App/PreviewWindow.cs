using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ContextCompanion.Core.Capturing;
using ContextCompanion.Core.Privacy;

namespace ContextCompanion.App;

/// <summary>
/// Shows a capture after the privacy rules have run (blackouts applied, secrets redacted, only enabled source
/// details) and before anything is packaged or queued. Send queues it with the optional note; Discard drops it, and
/// nothing about it is written. What is shown here is exactly what the packet will contain.
/// </summary>
internal sealed class PreviewWindow : Window
{
    private readonly TextBox note;
    private bool send;

    private PreviewWindow(PreparedCapture capture, string mode)
    {
        Title = "context-companion: send this capture?";
        Width = 720;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowInTaskbar = true;

        var layout = new DockPanel { Margin = new Thickness(12) };

        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        summary.Inlines.Add(new System.Windows.Documents.Run($"Mode: {mode}") { FontWeight = FontWeights.SemiBold });
        string redactions = capture.Redactions.Count == 0 ? "none"
            : string.Join(", ", capture.Redactions.Select(r => $"{r.Kind} {r.Rule ?? r.Pattern ?? ""} x{r.Count ?? 1}".Replace("  ", " ")));
        summary.Inlines.Add(new System.Windows.Documents.Run($"   Redactions: {redactions}"));
        if (capture.Source?.ProcessName is { } process) summary.Inlines.Add(new System.Windows.Documents.Run($"   From: {process}"));
        DockPanel.SetDock(summary, Dock.Top);
        layout.Children.Add(summary);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var discard = new Button { Content = "_Discard", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var deliver = new Button { Content = "_Send to inbox", MinWidth = 120 };
        discard.Click += (_, _) => { send = false; Close(); };
        deliver.Click += (_, _) => { send = true; Close(); };
        buttons.Children.Add(discard);
        buttons.Children.Add(deliver);
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(buttons);

        note = new TextBox { AcceptsReturn = true, Height = 60, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var notePanel = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var noteLabel = new Label { Content = "_Note (optional, also checked for secrets):", Target = note, Padding = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(noteLabel, Dock.Top);
        notePanel.Children.Add(noteLabel);
        notePanel.Children.Add(note);
        DockPanel.SetDock(notePanel, Dock.Bottom);
        layout.Children.Add(notePanel);

        UIElement body;
        if (capture.Image is { } image)
        {
            var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Pixels, image.Stride);
            bitmap.Freeze();
            body = new Border
            {
                BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
                Child = new Image { Source = bitmap, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly },
            };
        }
        else
        {
            body = new TextBox
            {
                Text = capture.Text ?? "", IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
        }
        layout.Children.Add(body);

        Content = layout;
        Loaded += (_, _) => { Activate(); deliver.Focus(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { send = true; Close(); e.Handled = true; }
        };
    }

    /// <summary>Shows the preview modally on the UI thread and returns the decision.</summary>
    public static ReviewDecision Ask(PreparedCapture capture, string mode)
    {
        var window = new PreviewWindow(capture, mode);
        window.ShowDialog();
        return window.send ? new ReviewDecision(true, string.IsNullOrWhiteSpace(window.note.Text) ? null : window.note.Text) : ReviewDecision.Discard;
    }
}
