using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Cus.Core.Abstractions;

namespace Cus.UI;

/// <summary>Finestra "X richiede l'accesso alla tua clipboard, consentire?" con tre scelte.</summary>
public sealed class AvaloniaConsentPrompt : IConsentPrompt
{
    public Task<ConsentDecision> AskAsync(ConsentRequest request, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<ConsentDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            var window = Build(request, tcs);
            var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(() =>
            {
                tcs.TrySetCanceled(cancellationToken);
                window.Close();
            }));
            window.Closed += (_, _) =>
            {
                registration.Dispose();
                tcs.TrySetResult(ConsentDecision.Reject);
            };
            window.Show();
            window.Activate();
        });
        return tcs.Task;
    }

    private static Window Build(ConsentRequest request, TaskCompletionSource<ConsentDecision> tcs)
    {
        var window = new Window
        {
            Title = "CUS — richiesta di accesso",
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            Topmost = true,
            WindowDecorations = WindowDecorations.None,
            Background = Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };

        Control Choice(string glyph, string label, ConsentDecision decision)
        {
            var button = new Button { Content = glyph, FontSize = 18 };
            button.Classes.Add("round");
            Avalonia.Automation.AutomationProperties.SetName(button, label);
            button.Click += (_, _) =>
            {
                tcs.TrySetResult(decision);
                window.Close();
            };
            return new StackPanel { Spacing = 8, Children = { button, Skin.Engraved(label) } };
        }

        var lcd = Skin.Lcd(new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Skin.LcdText("RICHIESTA DI ACCESSO", 11, bold: true),
                Skin.LcdText($"{request.HostName} chiede l'accesso alla tua clipboard. Consentire?", 17, bold: true),
                Skin.LcdText("Se accetti, ciò che copi arriva agli altri e ciò che copiano loro arriva a te. "
                    + "In sola lettura invii le tue copie ma non ricevi le loro. Un indicatore in alto a destra resta visibile finché la condivisione è attiva.", 11),
                Skin.LcdText($"UTENTE {request.HostUserName} · IMPRONTA {request.HostFingerprint}", 11),
            },
        });
        lcd.Padding = new Thickness(14, 10);

        var header = new DockPanel
        {
            Background = Brushes.Transparent,
            Children =
            {
                new TextBlock { Text = "CUS", FontSize = 20, FontWeight = FontWeight.Black, FontStyle = FontStyle.Italic, Foreground = Skin.LimeTextBrush },
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
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Spacing = 34,
                            Children =
                            {
                                Choice("✕", "rifiuta", ConsentDecision.Reject),
                                Choice("◐", "sola lettura", ConsentDecision.AcceptReadOnly),
                                Choice("✓", "consenti", ConsentDecision.Accept),
                            },
                        },
                    },
                }, cornerRadius: 18),
            },
        };
        return window;
    }
}
