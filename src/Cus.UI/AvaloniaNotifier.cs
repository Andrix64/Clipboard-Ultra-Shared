using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Cus.Core.Abstractions;

namespace Cus.UI;

/// <summary>Notifiche: un avviso temporaneo in basso a destra, più l'evento per il registro della finestra.</summary>
public sealed class AvaloniaNotifier(Func<Screens?> screens, string title, Action<Window>? configureWindow = null) : IUserNotifier
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(6);

    /// <summary>Notifiche visibili, dalla più vecchia. Usata solo sul thread della UI.</summary>
    private readonly List<Window> _open = [];

    public event Action<string, bool>? Notified;

    public void Info(string message) => Show(message, isError: false);

    public void Error(string message) => Show(message, isError: true);

    private void Show(string message, bool isError) => Dispatcher.UIThread.Post(() =>
    {
        Notified?.Invoke(message, isError);

        var window = new Window
        {
            Title = Loc.T("notifier.notification", title),
            SizeToContent = SizeToContent.WidthAndHeight,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            CanResize = false,
            WindowDecorations = WindowDecorations.None,
            Background = Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            Content = Skin.Chassis(new StackPanel
            {
                // Spazio per le viti negli angoli, che il display altrimenti coprirebbe.
                Margin = new Thickness(6),
                Spacing = 4,
                Children =
                {
                    Skin.Engraved(title),
                    Skin.Lcd(new TextBlock
                    {
                        Text = message,
                        FontFamily = Skin.LcdFont,
                        FontSize = 12,
                        MaxWidth = 360,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = isError ? Skin.AlertBrush : Skin.LimeTextBrush,
                    }, dark: true),
                },
            }, cornerRadius: 10, seed: 10),
        };
        // Più notifiche ravvicinate si impilano verso l'alto invece di coprirsi a vicenda.
        _open.Add(window);
        window.SizeChanged += (_, _) => Arrange();
        window.Closed += (_, _) =>
        {
            _open.Remove(window);
            Arrange();
        };
        window.PointerPressed += (_, _) => window.Close();
        window.Show();
        configureWindow?.Invoke(window);
        DispatcherTimer.RunOnce(window.Close, Duration);
    });

    /// <summary>La più recente in basso a destra, le precedenti sopra. Sul thread della UI.</summary>
    private void Arrange()
    {
        if (screens()?.Primary is not { } screen)
            return;

        const int gap = 8;
        var area = screen.WorkingArea;
        var bottom = area.Bottom - 16;
        for (var i = _open.Count - 1; i >= 0; i--)
        {
            var window = _open[i];
            var size = window.ClientSize * screen.Scaling;
            window.Position = new PixelPoint(area.Right - (int)size.Width - 16, bottom - (int)size.Height);
            bottom -= (int)size.Height + gap;
        }
    }
}
