using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Cus.UI;

/// <summary>
/// Parti comuni delle finestre secondarie (conferme, messaggi, consensi, trasferimenti): finestra senza
/// cornice di sistema, trasparente attorno al telaio, trascinabile dall'intestazione.
/// </summary>
internal static class DialogShell
{
    /// <summary>Margine trasparente attorno al telaio: lascia spazio all'ombra e agli angoli arrotondati.</summary>
    private static readonly Thickness OuterMargin = new(12);

    /// <summary>Margine interno di un display LCD con del testo.</summary>
    private static readonly Thickness LcdPadding = new(14, 10);

    /// <summary>Imposta su <paramref name="window"/> l'aspetto comune: niente cornice di sistema, sfondo trasparente.</summary>
    public static void Configure(Window window, string title, double width, WindowStartupLocation startupLocation)
    {
        window.Title = title;
        window.Icon = Skin.AppIcon;
        window.Width = width;
        window.SizeToContent = SizeToContent.Height;
        window.CanResize = false;
        window.WindowDecorations = WindowDecorations.None;
        window.Background = Brushes.Transparent;
        window.TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        window.WindowStartupLocation = startupLocation;
    }

    /// <summary>Senza cornice di sistema la finestra si sposta trascinando <paramref name="handle"/>.</summary>
    public static void DragsWindow(Control handle, Window window) =>
        handle.PointerPressed += (_, e) => window.BeginMoveDrag(e);

    /// <summary>Intestazione con il marchio "CUS", che trascina la finestra.</summary>
    public static DockPanel LogoHeader(Window window)
    {
        var header = new DockPanel
        {
            Background = Brushes.Transparent,
            Children =
            {
                new TextBlock { Text = "CUS", FontSize = 20, FontWeight = FontWeight.Black, FontStyle = FontStyle.Italic, Padding = new Thickness(0, 0, 5, 0), Foreground = Skin.LimeTextBrush },
            },
        };
        DragsWindow(header, window);
        return header;
    }

    /// <summary>Display LCD con il margine interno delle finestre secondarie.</summary>
    public static Border TextLcd(Control content)
    {
        var lcd = Skin.Lcd(content);
        lcd.Padding = LcdPadding;
        return lcd;
    }

    /// <summary>Il telaio metallico con le righe una sotto l'altra, dentro il margine trasparente.</summary>
    /// <param name="seed">Varia graffi e usura: ogni finestra ha il suo, per non sembrare tutte uguali.</param>
    /// <param name="padding">Null: quello delle finestre con il marchio in alto.</param>
    public static Panel Frame(IEnumerable<Control> rows, int seed, double cornerRadius = 18, Thickness? padding = null, double spacing = 14)
    {
        var stack = new StackPanel { Margin = padding ?? new Thickness(8, 4, 8, 8), Spacing = spacing };
        stack.Children.AddRange(rows);
        return new Panel { Margin = OuterMargin, Children = { Skin.Chassis(stack, cornerRadius, seed) } };
    }

    /// <summary>
    /// Finestra modale con titolo, contenuto e pulsanti a destra. Il primo pulsante è la scelta sicura
    /// (annulla, chiudi): ha il focus e risponde a Invio ed Esc, così un'azione vera richiede un clic.
    /// </summary>
    /// <returns>L'indice del pulsante premuto, null se la finestra è stata chiusa in altro modo.</returns>
    public static async Task<int?> AskAsync(Window owner, string title, double width, Control body, IReadOnlyList<string> buttonLabels, int seed)
    {
        int? chosen = null;
        var window = new Window { ShowInTaskbar = false };
        Configure(window, $"CUS — {title}", width, WindowStartupLocation.CenterOwner);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 12 };
        for (var i = 0; i < buttonLabels.Count; i++)
        {
            var index = i;
            var button = Skin.Pill(buttonLabels[i]);
            button.Click += (_, _) =>
            {
                chosen = index;
                window.Close();
            };
            buttons.Children.Add(button);
        }

        var safe = (Button)buttons.Children[0];
        safe.IsDefault = true;
        safe.IsCancel = true;

        var lcd = TextLcd(new StackPanel
        {
            Spacing = 6,
            Children = { Skin.LcdText(title.ToUpperInvariant(), 11, bold: true), body },
        });
        window.Content = Frame([LogoHeader(window), lcd, buttons], seed);
        window.Opened += (_, _) => safe.Focus();

        await window.ShowDialog(owner);
        return chosen;
    }
}
