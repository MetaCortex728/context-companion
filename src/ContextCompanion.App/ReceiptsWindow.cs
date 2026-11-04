using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ContextCompanion.Core.Delivery;

namespace ContextCompanion.App;

/// <summary>
/// The delivery receipts: every packet in the outbox with its state, when it was delivered and whether a consumer has
/// acknowledged it. It shows ids, states and times only, never content. Acknowledged means a consumer took the packet,
/// not that it was read or answered.
/// </summary>
internal sealed class ReceiptsWindow : Window
{
    private readonly CompanionHost host;
    private readonly DataGrid grid;
    private readonly TextBlock status;

    public ReceiptsWindow(CompanionHost host)
    {
        this.host = host;
        Title = "context-companion: deliveries";
        Width = 820;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        grid = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column };
        foreach (var (header, path) in new[]
        {
            ("Packet", "Id"), ("Created", "Created"), ("State", "State"), ("Delivered", "Delivered"), ("Acknowledged by", "Consumer"), ("At", "Consumed"),
        })
            grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new System.Windows.Data.Binding(path) });

        var refresh = new Button { Content = "_Refresh", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        refresh.Click += (_, _) => Reload();
        var open = new Button { Content = "_Open inbox folder", MinWidth = 140 };
        open.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{host.Inbox}\"") { UseShellExecute = true });
        status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        bar.Children.Add(refresh);
        bar.Children.Add(open);
        bar.Children.Add(status);

        var layout = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(bar, Dock.Bottom);
        layout.Children.Add(bar);
        layout.Children.Add(grid);
        Content = layout;
        Loaded += (_, _) => Reload();
    }

    private void Reload()
    {
        try
        {
            var rows = host.Receipts().Select(r => new
            {
                r.Id,
                Created = Local(r.Created),
                State = DeliveryTransitions.Name(r.State),
                Delivered = r.Delivered is { } d ? Local(d) : "",
                Consumer = r.Consumer ?? "",
                Consumed = r.Consumed is { } c ? Local(c) : "",
            }).ToList();
            grid.ItemsSource = rows;
            status.Text = $"{rows.Count} packet(s). Inbox: {host.Inbox}";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            status.Text = "Could not read the outbox: " + ex.Message;
        }
    }

    private static string Local(DateTimeOffset time) => time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
}
