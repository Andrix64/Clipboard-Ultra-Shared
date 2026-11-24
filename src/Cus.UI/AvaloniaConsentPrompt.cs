using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Cus.Core.Abstractions;

namespace Cus.UI;

/// <summary>
/// Finestra "X richiede l'accesso alla tua clipboard, consentire?": rifiuta, consenti e, per un invito
/// bidirezionale, sola lettura. La finestra
/// compare all'improvviso sopra le altre: i pulsanti restano disattivati per un attimo (un clic
/// o un Invio già in corso non devono diventare un consenso) e il focus parte da "rifiuta".
/// </summary>
public sealed class AvaloniaConsentPrompt : IConsentPrompt
{
    /// <summary>Tempo minimo prima che le scelte diventino cliccabili.</summary>
    private static readonly TimeSpan ArmDelay = TimeSpan.FromSeconds(1);

    private const int MaxNameLength = 64;

    public Task<ConsentDecision> AskAsync(ConsentRequest request, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<ConsentDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            Window? window = null;
            try
            {
                window = Build(request, tcs);
                var shown = window;
                var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(() =>
                {
                    tcs.TrySetCanceled(cancellationToken);
                    shown.Close();
                }));
                window.Closed += (_, _) =>
                {
                    registration.Dispose();
                    tcs.TrySetResult(ConsentDecision.Reject);
                };
                window.Show();
                window.Activate();
            }
            catch (Exception ex)
            {
                // Se la finestra non si può mostrare la richiesta si rifiuta: mai un'attesa infinita.
                System.Diagnostics.Trace.WriteLine($"CUS: finestra di consenso non disponibile: {ex}");
                tcs.TrySetResult(ConsentDecision.Reject);
                try
                {
                    window?.Close();
                }
                catch (Exception closeError)
                {
                    System.Diagnostics.Trace.WriteLine($"CUS: chiusura della finestra di consenso fallita: {closeError.Message}");
                }
            }
        });
        return tcs.Task;
    }

    private static Window Build(ConsentRequest request, TaskCompletionSource<ConsentDecision> tcs)
    {
        // Nomi e impronta arrivano dal peer: niente caratteri di controllo o bidi che falsino il testo.
        var hostName = DisplayText.Sanitize(request.HostName, MaxNameLength);
        var hostUser = DisplayText.Sanitize(request.HostUserName, MaxNameLength);
        var fingerprint = DisplayText.Sanitize(request.HostFingerprint, 128, "(impronta non disponibile)");

        var window = new Window
        {
            Title = "CUS — richiesta di accesso",
            Icon = Skin.AppIcon,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            Topmost = true,
            WindowDecorations = WindowDecorations.None,
            Background = Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };

        var buttons = new List<Button>();

        Control Choice(string glyph, string label, ConsentDecision decision, out Button button)
        {
            var choice = new Button { Content = glyph, FontSize = 18, IsEnabled = false };
            choice.Classes.Add("round");
            Avalonia.Automation.AutomationProperties.SetName(choice, label);
            choice.Click += (_, _) =>
            {
                tcs.TrySetResult(decision);
                window.Close();
            };
            buttons.Add(choice);
            button = choice;
            return new StackPanel { Spacing = 8, Children = { choice, Skin.Engraved(label) } };
        }

        // La modalità la sceglie chi invita: un invito bidirezionale si può ridurre a sola lettura,
        // uno in sola lettura si accetta o si rifiuta.
        var readOnlyInvite = request.RequestedDirection == ShareDirection.ReadOnly;
        var rejectChoice = Choice("✕", "rifiuta", ConsentDecision.Reject, out var reject);
        var acceptChoice = Choice("✓", "consenti", ConsentDecision.Accept, out _);
        var choices = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 34,
            Children = { rejectChoice },
        };
        if (!readOnlyInvite)
            choices.Children.Add(Choice("◐", "sola lettura", ConsentDecision.AcceptReadOnly, out _));
        choices.Children.Add(acceptChoice);
        // Invio ed Esc rifiutano: accettare richiede sempre un clic deliberato.
        reject.IsDefault = true;
        reject.IsCancel = true;

        var fingerprintText = Skin.LcdText(fingerprint, 15, bold: true);

        var lcd = Skin.Lcd(new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Skin.LcdText(readOnlyInvite ? "RICHIESTA DI ACCESSO · SOLA LETTURA" : "RICHIESTA DI ACCESSO", 11, bold: true),
                Skin.LcdText(readOnlyInvite
                    ? $"{hostName} vuole condividere la sua clipboard con te in sola lettura. Consentire?"
                    : $"{hostName} chiede l'accesso alla tua clipboard. Consentire?", 17, bold: true),
                Skin.LcdText((readOnlyInvite
                        ? "Se accetti, ciò che copiano gli altri arriva a te; ciò che copi tu resta su questo PC. "
                        : "Se accetti, ciò che copi arriva agli altri e ciò che copiano loro arriva a te. "
                            + "In sola lettura ricevi le loro copie ma le tue restano su questo PC. ")
                    + "Un indicatore in alto a destra resta visibile finché la condivisione è attiva.", 11),
                Skin.LcdText($"UTENTE {hostUser}", 11),
                Skin.LcdText("IMPRONTA (confrontala con quella mostrata sull'altro PC)", 11),
                fingerprintText,
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
                        choices,
                    },
                }, cornerRadius: 18),
            },
        };

        // Le scelte si attivano solo dopo che la finestra è comparsa da un po'.
        window.Opened += (_, _) => DispatcherTimer.RunOnce(() =>
        {
            foreach (var button in buttons)
                button.IsEnabled = true;
            reject.Focus();
        }, ArmDelay);

        return window;
    }
}
