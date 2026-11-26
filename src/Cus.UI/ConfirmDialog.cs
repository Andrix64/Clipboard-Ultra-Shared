using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;

namespace Cus.UI;

/// <summary>
/// Finestra modale di conferma per le azioni irreversibili (per esempio sciogliere un patto). Invio ed Esc
/// annullano: confermare richiede un clic sul pulsante dell'azione.
/// </summary>
public static class ConfirmDialog
{
    /// <returns>True solo se l'utente ha premuto il pulsante di conferma.</returns>
    public static async Task<bool> ShowAsync(Window owner, string title, string message, string confirmLabel)
    {
        var confirmed = false;
        var window = new Window
        {
            Title = $"CUS — {title}",
            Icon = Skin.AppIcon,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowDecorations = WindowDecorations.None,
            Background = Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var cancel = Skin.Pill(Loc.T("common.cancel"));
        cancel.IsDefault = true;
        cancel.IsCancel = true;
        cancel.Click += (_, _) => window.Close();

        var confirm = Skin.Pill(confirmLabel);
        confirm.Click += (_, _) =>
        {
            confirmed = true;
            window.Close();
        };

        var text = Skin.LcdText(message, 12);
        var lcd = Skin.Lcd(new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Skin.LcdText(title.ToUpperInvariant(), 11, bold: true),
                text,
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
                            Spacing = 12,
                            Children = { cancel, confirm },
                        },
                    },
                }, cornerRadius: 18),
            },
        };
        window.Opened += (_, _) => cancel.Focus();

        await window.ShowDialog(owner);
        return confirmed;
    }
}
