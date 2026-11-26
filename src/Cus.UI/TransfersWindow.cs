using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Cus.Core.Abstractions;
using Cus.Core.Node;

namespace Cus.UI;

/// <summary>
/// Copie dei propri file ancora in corso: a chi vanno, a che punto sono, con la possibilità di
/// annullarle. Si apre dal pulsante dei trasferimenti dell'indicatore o dalla scheda della sessione.
/// Con <c>incoming</c> mostra invece i file di altri PC che questo PC sta ricevendo. Una sola per tipo alla volta.
/// </summary>
public sealed class TransfersWindow : Window
{
    private static TransfersWindow? s_openOutgoing;
    private static TransfersWindow? s_openIncoming;

    private readonly CusNode _node;
    private readonly bool _incoming;
    private readonly StackPanel _rows = new() { Spacing = 6 };
    private readonly DispatcherTimer _timer;

    private TransfersWindow(CusNode node, bool incoming)
    {
        _node = node;
        _incoming = incoming;
        Topmost = true;
        DialogShell.Configure(this, Loc.T(incoming ? "transfersWindow.cusIncoming" : "transfersWindow.cusTransfers"), 460, WindowStartupLocation.CenterScreen);

        var close = Skin.Pill(Loc.T("common.close"));
        close.IsCancel = true;
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);

        var heading = Skin.LcdText(incoming
            ? Loc.T("transfersWindow.incomingFiles", Icons.Download)
            : Loc.T("transfersWindow.transfersOfYourFiles", Icons.Upload), 13, bold: true, dark: true);
        heading.TextWrapping = TextWrapping.NoWrap;
        heading.VerticalAlignment = VerticalAlignment.Center;

        var header = new DockPanel
        {
            Background = Brushes.Transparent,
            Children =
            {
                close,
                heading,
            },
        };
        DialogShell.DragsWindow(header, this);

        Content = DialogShell.Frame(
            [header, Skin.Lcd(new ScrollViewer { MaxHeight = 360, Content = _rows }, dark: true)],
            seed: 8, cornerRadius: 14, padding: new Thickness(6), spacing: 10);

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Refresh());
        Opened += (_, _) =>
        {
            Refresh();
            _timer.Start();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            if (_incoming)
                s_openIncoming = null;
            else
                s_openOutgoing = null;
        };
    }

    public static void ShowFor(CusNode node, bool incoming = false)
    {
        if ((incoming ? s_openIncoming : s_openOutgoing) is { } open)
        {
            open.Activate();
            return;
        }

        var window = new TransfersWindow(node, incoming);
        if (incoming)
            s_openIncoming = window;
        else
            s_openOutgoing = window;
        window.Show();
    }

    private void Refresh()
    {
        _rows.Children.Clear();
        if (_incoming)
        {
            var incoming = _node.IncomingTransfers;
            if (incoming.Count == 0)
                _rows.Children.Add(Skin.LcdText(Loc.T("transfersWindow.noIncomingFiles"), 12, dark: true));
            foreach (var transfer in incoming)
                _rows.Children.Add(IncomingRow(transfer));
            return;
        }

        var transfers = _node.Transfers;
        if (transfers.Count == 0)
        {
            _rows.Children.Add(Skin.LcdText(Loc.T("transfersWindow.noCopiesOfYourFilesIn"), 12, dark: true));
            return;
        }

        foreach (var transfer in transfers)
            _rows.Children.Add(Row(transfer));
    }

    private Control Row(TransferView transfer) => Row(
        transfer.ItemName, $"{Icons.ArrowRight} {DisplayText.Sanitize(transfer.RequesterName, 64)}",
        transfer.BytesDone, transfer.TotalBytes, transfer.Progress, transfer.State,
        () => _node.CancelTransfer(transfer.ItemId, transfer.RequesterId));

    private Control IncomingRow(IncomingTransferView transfer) => Row(
        transfer.ItemName, $"{Icons.ArrowLeft} {DisplayText.Sanitize(transfer.OriginName, 64)}",
        transfer.BytesDone, transfer.TotalBytes, transfer.Progress, transfer.State,
        () => _node.CancelIncomingTransfer(transfer.ItemId));

    /// <param name="peer">Verso chi (in uscita) o da chi (in arrivo), con la freccia davanti.</param>
    private Control Row(
        string itemName, string peer, long bytesDone, long totalBytes, double progress, TransferState transferState, Action cancelTransfer)
    {
        var state = transferState switch
        {
            TransferState.Active => $"{progress:P0}",
            TransferState.Completed => Loc.T("transfersWindow.completed"),
            TransferState.Cancelled => Loc.T("transfersWindow.cancelled"),
            _ => Loc.T("transfersWindow.stopped"),
        };
        var name = Skin.LcdText($"{DisplayText.MiddleEllipsis(DisplayText.Sanitize(itemName, 260), 34)}", 12, bold: true, dark: true);
        name.TextWrapping = TextWrapping.NoWrap;
        var detail = Skin.LcdText(
            $"{peer} · {DisplayText.FormatSize(bytesDone)} {Loc.T("transfersWindow.of")} {DisplayText.FormatSize(totalBytes)} · {state}",
            11, dark: true);

        var bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 1,
            Value = progress,
            Height = 8,
            MinHeight = 8,
            Foreground = transferState == TransferState.Cancelled ? Skin.AlertBrush : Skin.PillBrush,
            Background = new SolidColorBrush(Color.Parse("#0B1406")),
            CornerRadius = new CornerRadius(3),
        };

        var row = new DockPanel { Margin = new Thickness(0, 2) };
        if (transferState == TransferState.Active)
        {
            var cancel = Skin.Pill(Loc.T("common.cancel"));
            cancel.Margin = new Thickness(10, 0, 0, 0);
            cancel.VerticalAlignment = VerticalAlignment.Center;
            cancel.Click += (_, _) =>
            {
                cancelTransfer();
                Refresh();
            };
            DockPanel.SetDock(cancel, Dock.Right);
            row.Children.Add(cancel);
        }

        row.Children.Add(new StackPanel { Spacing = 3, Children = { name, detail, bar } });
        return row;
    }
}
