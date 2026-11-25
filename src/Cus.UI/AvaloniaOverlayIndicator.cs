using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Cus.Core.Abstractions;

namespace Cus.UI;

/// <summary>
/// Indicatore sempre in primo piano, una finestrella per monitor in alto a destra. Mostra cosa c'è
/// nella clipboard condivisa (chi l'ha copiato a sinistra, quanto pesa a destra, il nome sotto) e i
/// partecipanti, con un'icona per il ruolo e questo PC evidenziato. Non ruba il focus (la piattaforma
/// lo rende non attivabile tramite <paramref name="configureWindow"/>) e occupa solo la propria area.
/// Il doppio clic chiede di uscire dalla sessione: un clic singolo (per esempio mirando alla barra di
/// una finestra sotto) non fa nulla. I pulsanti (trasferimenti, sola lettura) rispondono al clic singolo.
/// </summary>
public sealed class AvaloniaOverlayIndicator(Func<Screens?> screens, Action<Window>? configureWindow = null) : IOverlayIndicator
{
    private const int Margin = 12;
    private const double Width = 380;

    /// <summary>Caratteri del nome di un file nel display (font a larghezza fissa).</summary>
    private const int TitleChars = 40;

    private readonly List<Window> _windows = [];
    private SessionIndicatorState? _state;
    private Screens? _subscribedScreens;

    public event EventHandler? LeaveRequested;

    public event EventHandler? TransfersRequested;

    public event EventHandler? DirectionToggleRequested;

    public void Show(SessionIndicatorState state) => Dispatcher.UIThread.Post(() =>
    {
        var hadWindows = _windows.Count > 0;
        _state = state;
        if (hadWindows)
        {
            // Aggiornamento sul posto: chiudere e riaprire le finestre a ogni copia lampeggerebbe.
            foreach (var window in _windows)
                window.Content = BuildContent(state);
        }
        else
        {
            Rebuild();
        }
    });

    public void Hide() => Dispatcher.UIThread.Post(() =>
    {
        _state = null;
        CloseAll();
    });

    public void Dispose() => Hide();

    private void Rebuild()
    {
        CloseAll();
        if (_state is null || screens() is not { } all)
            return;

        if (!ReferenceEquals(_subscribedScreens, all))
        {
            all.Changed += (_, _) => Dispatcher.UIThread.Post(Rebuild);
            _subscribedScreens = all;
        }

        foreach (var screen in all.All)
        {
            var window = CreateWindow(_state);
            _windows.Add(window);
            // La dimensione definitiva arriva dopo il layout (SizeToContent): si riallinea a destra
            // a ogni cambio di dimensione.
            void Place(double width)
            {
                var scaling = screen.Scaling;
                var area = screen.WorkingArea;
                window.Position = new PixelPoint(
                    area.Right - (int)Math.Ceiling(width * scaling) - (int)(Margin * scaling),
                    area.Y + (int)(Margin * scaling));
            }

            window.SizeChanged += (_, e) => Place(e.NewSize.Width);
            Place(Width + 20);
            window.Show();
            configureWindow?.Invoke(window);
        }
    }

    private void CloseAll()
    {
        foreach (var window in _windows)
            window.Close();
        _windows.Clear();
    }

    private Window CreateWindow(SessionIndicatorState state) => new()
    {
        Title = "CUS — clipboard condivisa",
        Content = BuildContent(state),
        SizeToContent = SizeToContent.WidthAndHeight,
        Topmost = true,
        ShowInTaskbar = false,
        ShowActivated = false,
        CanResize = false,
        WindowDecorations = WindowDecorations.None,
        Background = Brushes.Transparent,
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
        HorizontalContentAlignment = HorizontalAlignment.Right,
        WindowStartupLocation = WindowStartupLocation.Manual,
    };

    private Control BuildContent(SessionIndicatorState state)
    {
        var header = new DockPanel { LastChildFill = true };
        if (state.Transfers > 0)
        {
            var transfers = SmallButton(
                state.ActiveTransfers > 0 ? $"{Icons.Transfers} {state.ActiveTransfers}" : Icons.Transfers,
                state.ActiveTransfers > 0
                    ? $"{state.ActiveTransfers} copie dei tuoi file in corso: chi le scarica, a che punto sono, annulla"
                    : "Copie dei tuoi file appena concluse");
            transfers.Click += (_, _) => TransfersRequested?.Invoke(this, EventArgs.Empty);
            DockPanel.SetDock(transfers, Dock.Right);
            header.Children.Add(transfers);
        }

        if (state.CanToggleDirection)
        {
            var readOnly = state.MyDirection == ShareDirection.ReadOnly;
            var toggle = SmallButton(readOnly ? $"{Icons.ArrowDown} SOLA LETTURA" : $"{Icons.Bidirectional} BIDIREZ.",
                readOnly ? "Sei in sola lettura: clic per tornare bidirezionale" : "Clic per passare in sola lettura: le tue copie resteranno qui");
            toggle.Click += (_, _) => DirectionToggleRequested?.Invoke(this, EventArgs.Empty);
            DockPanel.SetDock(toggle, Dock.Right);
            header.Children.Add(toggle);
        }

        var title = new TextBlock
        {
            FontFamily = Skin.LcdFont,
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Foreground = Skin.LimeTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Skin.SetRich(title, $"{Icons.Clipboard} CLIPBOARD CONDIVISA");
        header.Children.Add(title);

        var content = Skin.Chassis(new StackPanel
        {
            Width = Width,
            Spacing = 6,
            Children =
            {
                header,
                Skin.Lcd(CurrentPanel(state.Current, state.Participants)),
                Participants(state.Participants),
                Skin.Engraved("doppio clic per uscire dalla sessione"),
            },
        }, cornerRadius: 12);
        content.Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(content, "Doppio clic per uscire dalla sessione");
        content.PointerPressed += (_, e) =>
        {
            // Solo un doppio clic sinistro deliberato: i clic accidentali sull'angolo in alto a destra
            // (dove stanno i pulsanti delle finestre) non devono chiudere la condivisione. I pulsanti
            // gestiscono da sé il proprio clic e non arrivano qui.
            if (e.ClickCount >= 2 && e.GetCurrentPoint(content).Properties.IsLeftButtonPressed)
            {
                e.Handled = true;
                LeaveRequested?.Invoke(this, EventArgs.Empty);
            }
        };
        return content;
    }

    /// <summary>Chi ha copiato a sinistra, quanto pesa a destra, cosa è sotto.</summary>
    private static Control CurrentPanel(ClipboardSummary? current, IReadOnlyList<SessionParticipant> participants)
    {
        if (current is null)
            return Skin.LcdText("nessuna copia ancora", 13);

        var self = participants.FirstOrDefault(p => p.IsSelf);
        var origin = DisplayText.Sanitize(current.OriginName, 64) + (self is not null && current.OriginDeviceId == self.DeviceId ? " (tu)" : "");
        var size = current.SizeBytes is { } bytes ? DisplayText.FormatSize(bytes) : "—";
        var title = DisplayText.Sanitize(current.Title, 260, "");
        if (current.Kind == ClipboardContentKind.Files && !current.IsSensitive)
            title = DisplayText.MiddleEllipsis(title, TitleChars);
        if (!current.IsShared)
            title += " · non condiviso";

        var originText = Skin.LcdText(origin, 12, bold: true);
        originText.TextWrapping = TextWrapping.NoWrap;
        originText.TextTrimming = TextTrimming.CharacterEllipsis;
        var sizeText = Skin.LcdText(size, 12, bold: true);
        sizeText.TextWrapping = TextWrapping.NoWrap;
        sizeText.Margin = new Thickness(10, 0, 0, 0);
        DockPanel.SetDock(sizeText, Dock.Right);

        var titleText = Skin.LcdText((current.IsSensitive ? "••• " : "") + title, 14, bold: true);
        titleText.TextWrapping = TextWrapping.NoWrap;
        titleText.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTip.SetTip(titleText, DisplayText.Sanitize(current.Title, 260, ""));

        return new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new DockPanel { Children = { sizeText, originText } },
                titleText,
            },
        };
    }

    /// <summary>Partecipanti in piccolo: icona del ruolo al posto delle parole, questo PC evidenziato.</summary>
    private static Control Participants(IReadOnlyList<SessionParticipant> participants)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var participant in participants)
        {
            var (glyph, role) = Glyph(participant);
            var label = new TextBlock
            {
                FontFamily = Skin.LcdFont,
                FontSize = 10.5,
                FontWeight = participant.IsSelf ? FontWeight.Bold : FontWeight.Normal,
                Foreground = participant.IsSelf ? Skin.InkBrush : Skin.LimeTextBrush,
            };
            Skin.SetRich(label, $"{glyph} {DisplayText.Sanitize(participant.DisplayName, 32)}");
            var chip = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1),
                Margin = new Thickness(0, 0, 4, 4),
                Background = participant.IsSelf ? Skin.PillBrush : Skin.DarkLcdBrush,
                BorderBrush = participant.IsSelf ? Skin.LimeTextBrush : new SolidColorBrush(Color.Parse("#2F4A14")),
                BorderThickness = new Thickness(1),
                Child = label,
            };
            ToolTip.SetTip(chip, participant.IsSelf ? $"{role} · sei tu" : role);
            panel.Children.Add(chip);
        }

        return panel;
    }

    /// <summary>Stella per l'host, frecce per bidirezionale e sola lettura (riceve soltanto).</summary>
    public static (string Glyph, string Role) Glyph(SessionParticipant participant) =>
        participant.IsHost ? (Icons.Star, "host")
        : participant.Direction == ShareDirection.ReadOnly ? (Icons.ArrowDown, participant.Granted == ShareDirection.ReadOnly ? "sola lettura" : "sola lettura (scelta sua)")
        : (Icons.Bidirectional, "bidirezionale");

    private static Button SmallButton(string text, string tip)
    {
        var button = Skin.Pill(text);
        button.FontSize = 9.5;
        button.Margin = new Thickness(6, 0, 0, 0);
        button.Padding = new Thickness(0);
        ToolTip.SetTip(button, tip);
        return button;
    }
}
