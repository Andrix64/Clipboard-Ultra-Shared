using Avalonia.Controls;
using Avalonia.Layout;
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
        var fingerprint = DisplayText.Sanitize(request.RequesterFingerprint, 128, Loc.T("consent.fingerprintUnavailable"));
        // Un nome uguale a quello di un amico non lo rende un amico: lo si dice prima di tutto il resto.
        var warning = request.LooksLikeFriend is { } imitated
            ? Loc.T("consent.warningThisIsNotYourFriend", DisplayText.Sanitize(imitated, MaxNameLength))
            : "";
        return Show(
            Loc.T("consent.cusJoinRequest"),
            request.LooksLikeFriend is null
                ? Loc.T("consent.joinRequest")
                : Loc.T("consent.joinRequestAFriendSName", Icons.Warning),
            Loc.T("consent.isAskingToJoinYourShare", name),
            warning + Loc.T("consent.whoeverJoinsReceivesWhatIsCopied"),
            user,
            fingerprint,
            [(Icons.Xmark, Loc.T("consent.reject"), ConsentDecision.Reject), (Icons.ArrowDown, Loc.T("common.readOnly"), ConsentDecision.AcceptReadOnly), (Icons.Bidirectional, Loc.T("common.twoWay"), ConsentDecision.Accept)],
            ConsentDecision.Reject,
            cancellationToken);
    }

    public Task<bool> AskAgreementAsync(AgreementRequest request, CancellationToken cancellationToken)
    {
        var name = DisplayText.Sanitize(request.HostName, MaxNameLength);
        var fingerprint = DisplayText.Sanitize(request.HostFingerprint, 128, Loc.T("consent.fingerprintUnavailable"));
        var what = request.Level == AgreementLevel.Anytime
            ? Loc.T("consent.youCanConnectToEachOther")
            : Loc.T("consent.youCanJoinEachOtherS");
        return Show(
            Loc.T("consent.cusPactProposal"),
            Loc.T("consent.pact", CusNode.DescribeAgreement(request.Level).ToUpperInvariant()),
            Loc.T("consent.isProposingAPact", name),
            Loc.T("consent.ifYouAcceptThePactWorks", what),
            "",
            fingerprint,
            [(Icons.Xmark, Loc.T("consent.reject"), false), (Icons.Check, Loc.T("consent.accept"), true)],
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
        var window = new Window { Topmost = true };
        DialogShell.Configure(window, title, 480, WindowStartupLocation.CenterScreen);

        var buttons = new List<Button>();
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 34 };
        foreach (var (glyph, label, value) in choices)
        {
            var choice = new Button { FontSize = 18, IsEnabled = false };
            Skin.SetContent(choice, glyph);
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
            lines.Children.Add(Skin.LcdText(Loc.T("consent.user", user), 11));
        lines.Children.Add(Skin.LcdText(Loc.T("consent.fingerprintCompareItWithTheOne"), 11));
        lines.Children.Add(Skin.LcdText(fingerprint, 15, bold: true));
        window.Content = DialogShell.Frame([DialogShell.LogoHeader(window), DialogShell.TextLcd(lines), row], seed: 9);

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
