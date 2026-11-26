using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Cus.UI;

/// <summary>
/// Finestra modale che mostra per intero un testo che nella console è troncato con i puntini.
/// Il testo è selezionabile; Invio ed Esc chiudono.
/// </summary>
public static class MessageDialog
{
    public static async Task ShowAsync(Window owner, string title, string message)
    {
        var text = new SelectableTextBlock
        {
            Text = message,
            FontFamily = Skin.LcdFont,
            FontSize = 12,
            Foreground = Skin.InkBrush,
            TextWrapping = TextWrapping.Wrap,
        };
        await DialogShell.AskAsync(owner, title, 480, new ScrollViewer { MaxHeight = 320, Content = text }, [Loc.T("common.close")], seed: 7);
    }

    /// <summary>
    /// Un clic sul testo, se è troncato (o se <paramref name="fullText"/> dice di più di quello che si vede),
    /// apre il messaggio completo. Il cursore a manina segnala quando c'è altro da leggere.
    /// </summary>
    /// <param name="title">Letto al clic: segue la lingua corrente.</param>
    public static void ExpandOnClick(TextBlock block, Func<string> title, Func<string?>? fullText = null)
    {
        string? Hidden()
        {
            // Il messaggio per intero è solo testo: le icone (che il blocco disegna in Font Awesome) non vi compaiono.
            var shown = Skin.PlainText(block);
            var full = fullText?.Invoke() ?? shown;
            if (string.IsNullOrEmpty(full))
                return null;
            var trimmed = block.TextLayout.TextLines.Any(l => l.HasCollapsed);
            return trimmed || full != shown ? full : null;
        }

        block.PointerEntered += (_, _) =>
            block.Cursor = Hidden() is null ? Cursor.Default : new Cursor(StandardCursorType.Hand);
        block.PointerReleased += async (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left
                || Hidden() is not { } full
                || TopLevel.GetTopLevel(block) is not Window owner)
                return;
            e.Handled = true;
            await ShowAsync(owner, title(), full);
        };
    }
}
