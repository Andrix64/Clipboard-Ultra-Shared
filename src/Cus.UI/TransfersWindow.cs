using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Cus.Core.Abstractions;
using Cus.Core.Node;

namespace Cus.UI;

/// <summary>
/// Copie dei propri file ancora in corso: a chi vanno, a che punto sono, con la possibilità di
/// annullarle. Si apre dal pulsante ⇅ dell'indicatore o dalla scheda della sessione; una sola alla volta.
/// </summary>
public sealed class TransfersWindow : Window
{
    private static TransfersWindow? s_open;

    private readonly CusNode _node;
    private readonly StackPanel _rows = new() { Spacing = 6 };
    private readonly DispatcherTimer _timer;

    private TransfersWindow(CusNode node)
    {
        _node = node;
        Title = "CUS — trasferimenti";
        Icon = Skin.AppIcon;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        Topmost = true;
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var close = Skin.Pill("CHIUDI");
        close.IsCancel = true;
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);

        var header = new DockPanel
        {
            Background = Brushes.Transparent,
            Children =
            {
                close,
                new TextBlock { Text = "⇅ TRASFERIMENTI DEI TUOI FILE", FontFamily = Skin.LcdFont, FontSize = 13, FontWeight = FontWeight.Bold, Foreground = Skin.LimeTextBrush, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        header.PointerPressed += (_, e) => BeginMoveDrag(e);

        Content = new Panel
        {
            Margin = new Thickness(12),
            Children =
            {
                Skin.Chassis(new StackPanel
                {
                    Margin = new Thickness(6),
                    Spacing = 10,
                    Children =
                    {
                        header,
                        Skin.Lcd(new ScrollViewer { MaxHeight = 360, Content = _rows }, dark: true),
                    },
                }, cornerRadius: 14),
            },
        };

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Refresh());
        Opened += (_, _) =>
        {
            Refresh();
            _timer.Start();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            s_open = null;
        };
    }

    public static void ShowFor(CusNode node)
    {
        if (s_open is { } open)
        {
            open.Activate();
            return;
        }

        s_open = new TransfersWindow(node);
        s_open.Show();
    }

    private void Refresh()
    {
        var transfers = _node.Transfers;
        _rows.Children.Clear();
        if (transfers.Count == 0)
        {
            _rows.Children.Add(Skin.LcdText("Nessuna copia dei tuoi file in corso.", 12, dark: true));
            return;
        }

        foreach (var transfer in transfers)
            _rows.Children.Add(Row(transfer));
    }

    private Control Row(TransferView transfer)
    {
        var state = transfer.State switch
        {
            TransferState.Active => $"{transfer.Progress:P0}",
            TransferState.Completed => "COMPLETATO",
            TransferState.Cancelled => "ANNULLATO",
            _ => "FERMO",
        };
        var name = Skin.LcdText($"{DisplayText.MiddleEllipsis(DisplayText.Sanitize(transfer.ItemName, 260), 34)}", 12, bold: true, dark: true);
        name.TextWrapping = TextWrapping.NoWrap;
        var detail = Skin.LcdText(
            $"→ {DisplayText.Sanitize(transfer.RequesterName, 64)} · {DisplayText.FormatSize(transfer.BytesDone)} di {DisplayText.FormatSize(transfer.TotalBytes)} · {state}",
            11, dark: true);

        var bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 1,
            Value = transfer.Progress,
            Height = 8,
            MinHeight = 8,
            Foreground = transfer.State == TransferState.Cancelled ? Skin.AlertBrush : Skin.PillBrush,
            Background = new SolidColorBrush(Color.Parse("#0B1406")),
            CornerRadius = new CornerRadius(3),
        };

        var row = new DockPanel { Margin = new Thickness(0, 2) };
        if (transfer.State == TransferState.Active)
        {
            var cancel = Skin.Pill("ANNULLA");
            cancel.Margin = new Thickness(10, 0, 0, 0);
            cancel.VerticalAlignment = VerticalAlignment.Center;
            cancel.Click += (_, _) =>
            {
                _node.CancelTransfer(transfer.ItemId, transfer.RequesterId);
                Refresh();
            };
            DockPanel.SetDock(cancel, Dock.Right);
            row.Children.Add(cancel);
        }

        row.Children.Add(new StackPanel { Spacing = 3, Children = { name, detail, bar } });
        return row;
    }
}
