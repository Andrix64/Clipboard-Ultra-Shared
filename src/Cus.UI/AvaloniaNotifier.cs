using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Cus.Core.Abstractions;

namespace Cus.UI;

/// <summary>Notifiche: un avviso temporaneo in basso a destra, più l'evento per il registro della finestra.</summary>
public sealed class AvaloniaNotifier(Func<Screens?> screens, string title, Action<Window>? configureWindow = null) : IUserNotifier
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(6);

    public event Action<string, bool>? Notified;

    public void Info(string message) => Show(message, isError: false);

    public void Error(string message) => Show(message, isError: true);

    private void Show(string message, bool isError) => Dispatcher.UIThread.Post(() =>
    {
        Notified?.Invoke(message, isError);

        var window = new Window
        {
            Title = $"{title} — notifica",
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
            }, cornerRadius: 10),
        };
        window.SizeChanged += (_, e) =>
        {
            if (screens()?.Primary is { } screen)
            {
                var area = screen.WorkingArea;
                var size = e.NewSize * screen.Scaling;
                window.Position = new PixelPoint(area.Right - (int)size.Width - 16, area.Bottom - (int)size.Height - 16);
            }
        };
        window.PointerPressed += (_, _) => window.Close();
        window.Show();
        configureWindow?.Invoke(window);
        DispatcherTimer.RunOnce(window.Close, Duration);
    });
}
