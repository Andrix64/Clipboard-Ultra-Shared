using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Cus.Core.Abstractions;
using Cus.Core.Friends;
using Cus.Core.Node;

namespace Cus.UI;

/// <summary>
/// Finestre di scelta che compaiono all'improvviso: "X chiede di entrare" (per l'host: rifiuta, sola
/// lettura, bidirezionale) e "X propone un patto" (per il membro: rifiuta, accetta). I pulsanti restano
/// disattivati per un attimo (un clic o un Invio già in corso non devono diventare un consenso) e il
/// focus parte da "rifiuta"; Invio ed Esc rifiutano.
/// </summary>
public sealed class AvaloniaConsentPrompt : IConsentPrompt
{
    /// <summary>Tempo minimo prima che le scelte diventino cliccabili.</summary>
    private static readonly TimeSpan ArmDelay = TimeSpan.FromSeconds(1);

    private const int MaxNameLength = 64;

    public Task<ConsentDecision> AskAsync(ConsentRequest request, CancellationToken cancellationToken)
    {
        // Nomi e impronta arrivano dal peer: niente caratteri di controllo o bidi che falsino il testo.
        var name = DisplayText.Sanitize(request.RequesterName, MaxNameLength);
        var user = DisplayText.Sanitize(request.RequesterUserName, MaxNameLength);
        var fingerprint = DisplayText.Sanitize(request.RequesterFingerprint, 128, "(impronta non disponibile)");
        return Show(
            "CUS — richiesta di ingresso",
            "RICHIESTA DI INGRESSO",
            $"{name} chiede di entrare nella tua condivisione.",
            "Chi entra riceve ciò che si copia nella sessione. In bidirezionale anche ciò che copia lui arriva a tutti; "
                + "in sola lettura le sue copie restano sul suo PC. Potrai cambiarlo dall'elenco dei connessi.",
            user,
            fingerprint,
            [("✕", "rifiuta", ConsentDecision.Reject), ("↓", "sola lettura", ConsentDecision.AcceptReadOnly), ("⇄", "bidirezionale", ConsentDecision.Accept)],
            ConsentDecision.Reject,
            cancellationToken);
    }

    public Task<bool> AskAgreementAsync(AgreementRequest request, CancellationToken cancellationToken)
    {
        var name = DisplayText.Sanitize(request.HostName, MaxNameLength);
        var fingerprint = DisplayText.Sanitize(request.HostFingerprint, 128, "(impronta non disponibile)");
        var what = request.Level == AgreementLevel.Anytime
            ? "potrete collegarvi l'uno all'altro in qualsiasi momento, anche senza offrirvi come host, senza conferma"
            : "potrete entrare l'uno nella condivisione dell'altro senza conferma, quando l'altro si offre come host";
        return Show(
            "CUS — proposta di patto",
            $"PATTO · {CusNode.DescribeAgreement(request.Level).ToUpperInvariant()}",
            $"{name} ti propone un patto.",
            $"Se accetti, {what}. Il patto vale nei due sensi e cade per entrambi appena uno dei due lo scioglie (scheda AMICI).",
            "",
            fingerprint,
            [("✕", "rifiuta", false), ("✓", "accetta", true)],
            false,
            cancellationToken);
    }

    private static Task<T> Show<T>(
        string title, string kicker, string headline, string body, string user, string fingerprint,
        IReadOnlyList<(string Glyph, string Label, T Value)> choices, T rejectValue, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            Window? window = null;
            try
            {
                window = Build(title, kicker, headline, body, user, fingerprint, choices, tcs);
                var shown = window;
                var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(() =>
                {
                    tcs.TrySetCanceled(cancellationToken);
                    shown.Close();
                }));
                window.Closed += (_, _) =>
                {
                    registration.Dispose();
                    tcs.TrySetResult(rejectValue);
                };
                window.Show();
                window.Activate();
            }
            catch (Exception ex)
            {
                // Se la finestra non si può mostrare la richiesta si rifiuta: mai un'attesa infinita.
                System.Diagnostics.Trace.WriteLine($"CUS: finestra di consenso non disponibile: {ex}");
                tcs.TrySetResult(rejectValue);
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

    private static Window Build<T>(
        string title, string kicker, string headline, string body, string user, string fingerprint,
        IReadOnlyList<(string Glyph, string Label, T Value)> choices, TaskCompletionSource<T> tcs)
    {
        var window = new Window
        {
            Title = title,
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
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 34 };
        foreach (var (glyph, label, value) in choices)
        {
            var choice = new Button { Content = glyph, FontSize = 18, IsEnabled = false };
            choice.Classes.Add("round");
            Avalonia.Automation.AutomationProperties.SetName(choice, label);
            choice.Click += (_, _) =>
            {
                tcs.TrySetResult(value);
                window.Close();
            };
            buttons.Add(choice);
            row.Children.Add(new StackPanel { Spacing = 8, Children = { choice, Skin.Engraved(label) } });
        }

        // La prima scelta è sempre il rifiuto: Invio ed Esc rifiutano, accettare richiede un clic deliberato.
        var reject = buttons[0];
        reject.IsDefault = true;
        reject.IsCancel = true;

        var lines = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Skin.LcdText(kicker, 11, bold: true),
                Skin.LcdText(headline, 17, bold: true),
                Skin.LcdText(body, 11),
            },
        };
        if (user.Length > 0)
            lines.Children.Add(Skin.LcdText($"UTENTE {user}", 11));
        lines.Children.Add(Skin.LcdText("IMPRONTA (confrontala con quella mostrata sull'altro PC)", 11));
        lines.Children.Add(Skin.LcdText(fingerprint, 15, bold: true));
        var lcd = Skin.Lcd(lines);
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
                    Children = { header, lcd, row },
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
