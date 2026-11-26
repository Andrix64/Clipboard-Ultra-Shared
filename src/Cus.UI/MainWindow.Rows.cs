using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Cus.Core.Abstractions;

namespace Cus.UI;

public partial class MainWindow : Window
{
    // ================================================================ testi troncati

    /// <summary>
    /// I testi della console finiscono con i puntini quando non ci stanno: un clic (doppio clic nella
    /// cronologia, dove il clic singolo sceglie la voce da copiare) apre il messaggio completo.
    /// </summary>
    private void WireExpandableTexts()
    {
        MessageDialog.ExpandOnClick(LcdDevice, () => Loc.T("main.thisPcTitle"));
        MessageDialog.ExpandOnClick(LcdParticipants, () => Loc.T("main.sessionTitle"));
        MessageDialog.ExpandOnClick(LcdCurrent, () => Loc.T("main.currentClipboard"), () => _currentFull);
        MessageDialog.ExpandOnClick(LcdLast, () => Loc.T("main.lastActivity"), () => _lastActivityFull);

        ActivityList.Tapped += async (_, e) =>
        {
            if (ItemText(e.Source) is { } line)
                await MessageDialog.ShowAsync(this, Loc.T("main.activity"), line);
        };
        HistoryList.DoubleTapped += async (_, e) =>
        {
            if (ItemText(e.Source) is null || HistoryList.SelectedIndex is not (var i and >= 0) || i >= _history.Count)
                return;
            var entry = _history[i];
            var text = $"{entry.CreatedUtc.ToLocalTime():dd/MM HH:mm} · {DisplayText.Sanitize(entry.OriginName, 64)}\n\n"
                + DisplayText.Sanitize(entry.Describe(), 4000, Loc.T("common.empty"));
            await MessageDialog.ShowAsync(this, Loc.T("main.historyTitle"), text);
        };
    }

    /// <summary>Testo della riga di elenco su cui si è cliccato (null se il clic non è su una riga).</summary>
    private static string? ItemText(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as string;

    // ================================================================ righe degli elenchi

    /// <summary>Riga di un elenco: titolo e dettaglio a sinistra, pulsanti a destra; questo PC evidenziato.</summary>
    private static Control Row(string title, string detail, bool highlight, IReadOnlyList<Control> buttons, string? tip = null)
    {
        var titleText = new TextBlock
        {
            FontFamily = Skin.LcdFont,
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Foreground = highlight ? Skin.InkBrush : Skin.LimeTextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Skin.SetRich(titleText, title);
        MessageDialog.ExpandOnClick(titleText, () => Loc.T("main.detail"));
        var detailText = new TextBlock
        {
            FontFamily = Skin.LcdFont,
            FontSize = 10.5,
            Foreground = highlight ? Skin.InkBrush : new SolidColorBrush(Color.Parse("#7FB83A")),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        Skin.SetRich(detailText, detail);
        MessageDialog.ExpandOnClick(detailText, () => Loc.T("main.detail"));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        foreach (var button in buttons)
            actions.Children.Add(button);
        DockPanel.SetDock(actions, Dock.Right);

        var row = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 4),
            Background = highlight ? Skin.PillBrush : new SolidColorBrush(Color.Parse("#1C2E10")),
            BorderBrush = highlight ? Skin.LimeTextBrush : new SolidColorBrush(Color.Parse("#2F4A14")),
            BorderThickness = new Thickness(1),
            Child = new DockPanel
            {
                Children =
                {
                    actions,
                    new StackPanel { Spacing = 1, Margin = new Thickness(0, 0, 6, 0), Children = { titleText, detailText } },
                },
            },
        };
        if (tip is not null)
            ToolTip.SetTip(row, tip);
        return row;
    }

    private static Button RowButton(string text, string tip)
    {
        var button = Skin.Pill(text);
        button.MinWidth = 30;
        ToolTip.SetTip(button, tip);
        return button;
    }

    private static TextBlock Hint(string text)
    {
        var block = new TextBlock
        {
            FontFamily = Skin.LcdFont,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#7FB83A")),
            TextWrapping = TextWrapping.Wrap,
        };
        Skin.SetRich(block, text);
        return block;
    }
}
