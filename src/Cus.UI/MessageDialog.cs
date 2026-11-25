using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;

namespace Cus.UI;

/// <summary>
/// Finestra modale che mostra per intero un testo che nella console è troncato con i puntini.
/// Il testo è selezionabile; Invio ed Esc chiudono.
/// </summary>
public static class MessageDialog
{
    public static async Task ShowAsync(Window owner, string title, string message)
    {
        var window = new Window
        {
            Title = $"CUS — {title}",
            Icon = Skin.AppIcon,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowDecorations = WindowDecorations.None,
            Background = Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var close = Skin.Pill("CHIUDI");
        close.IsDefault = true;
        close.IsCancel = true;
        close.Click += (_, _) => window.Close();

        var text = new SelectableTextBlock
        {
            Text = message,
            FontFamily = Skin.LcdFont,
            FontSize = 12,
            Foreground = Skin.InkBrush,
            TextWrapping = TextWrapping.Wrap,
        };
        var lcd = Skin.Lcd(new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Skin.LcdText(title.ToUpperInvariant(), 11, bold: true),
                new ScrollViewer { MaxHeight = 320, Content = text },
            },
        });
        lcd.Padding = new Thickness(14, 10);

        var header = new DockPanel
        {
            Background = Brushes.Transparent,
            Children =
            {
                new TextBlock { Text = "CUS", FontSize = 20, FontWeight = FontWeight.Black, FontStyle = FontStyle.Italic, Padding = new Thickness(0, 0, 5, 0), Foreground = Skin.LimeTextBrush },
            },
        };
        header.PointerPressed += (_, e) => window.BeginMoveDrag(e);

        window.Content = new Panel
        {
            Margin = new Thickness(12),
            Children =
            {
                Skin.Chassis(new StackPanel
                {
                    Margin = new Thickness(8, 4, 8, 8),
                    Spacing = 14,
                    Children =
                    {
                        header,
                        lcd,
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Children = { close },
                        },
                    },
                }, cornerRadius: 18),
            },
        };
        window.Opened += (_, _) => close.Focus();

        await window.ShowDialog(owner);
    }

    /// <summary>
    /// Un clic sul testo, se è troncato (o se <paramref name="fullText"/> dice di più di quello che si vede),
    /// apre il messaggio completo. Il cursore a manina segnala quando c'è altro da leggere.
    /// </summary>
    public static void ExpandOnClick(TextBlock block, string title, Func<string?>? fullText = null)
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
            await ShowAsync(owner, title, full);
        };
    }
}
