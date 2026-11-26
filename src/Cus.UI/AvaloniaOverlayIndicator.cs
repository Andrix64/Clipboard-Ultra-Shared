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

    private bool _languageHooked;

    public void Show(SessionIndicatorState state) => Dispatcher.UIThread.Post(() =>
    {
        if (!_languageHooked)
        {
            // Cambio di lingua: si ridisegna sul posto con i testi nuovi.
            _languageHooked = true;
            Loc.Changed += () => Dispatcher.UIThread.Post(() =>
            {
                if (_state is not { } current)
                    return;
                foreach (var window in _windows)
                {
                    window.Title = WindowTitle;
                    window.Content = BuildContent(current);
                }
            });
        }

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

    private static string WindowTitle => Loc.T("overlay.cusSharedClipboard");

    private Window CreateWindow(SessionIndicatorState state) => new()
    {
        Title = WindowTitle,
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
                    ? Loc.T("overlay.copiesOfYourFilesInProgress", state.ActiveTransfers)
                    : Loc.T("overlay.copiesOfYourFilesJustFinished"));
            transfers.Click += (_, _) => TransfersRequested?.Invoke(this, EventArgs.Empty);
            DockPanel.SetDock(transfers, Dock.Right);
            header.Children.Add(transfers);
        }

        if (state.CanToggleDirection)
        {
            var readOnly = state.MyDirection == ShareDirection.ReadOnly;
            var toggle = SmallButton(readOnly ? Loc.T("overlay.readOnly", Icons.ArrowDown) : Loc.T("overlay.twoWay", Icons.Bidirectional),
                readOnly
                    ? Loc.T("overlay.youAreReadOnlyClickTo")
                    : Loc.T("overlay.clickToSwitchToReadOnly"));
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
        Skin.SetRich(title, Loc.T("overlay.sharedClipboard", Icons.Clipboard));

        // Spia rossa che pulsa, come sulla finestra principale: la condivisione è attiva.
        var lamp = new Lamp
        {
            Width = 16,
            Height = 16,
            Color = Color.Parse("#FF3B2B"),
            Pulse = true,
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        header.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { lamp, title },
        });

        // Vetro del monitor CRT sopra il display: righe di scansione, bordi scuri e riflesso.
        var screen = new Grid
        {
            Children =
            {
                CurrentPanel(state.Current, state.Participants),
                new CrtOverlay { Margin = new Thickness(-10, -6), CornerRadius = 8, Strength = 0.8 },
            },
        };

        var body = new StackPanel
        {
            Width = Width,
            Spacing = 6,
            Margin = new Thickness(14, 2, 14, 2),
            Children =
            {
                header,
                Skin.Lcd(screen),
                Participants(state.Participants),
                Skin.Engraved(Loc.T("overlay.doubleClickToLeaveTheSession")),
            },
        };

        // Scocca come la finestra principale: metallo spazzolato con graffi e viti, e due colonne di LED
        // ai lati del display.
        var content = Skin.Chassis(new Panel
        {
            Children =
            {
                new ChassisDecor { Margin = new Thickness(-8), Screws = ScrewSide.Both, Seed = 5, CornerRadius = 10 },
                body,
                new LedStrip { HorizontalAlignment = HorizontalAlignment.Left, Width = 8, VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(0, 34, 0, 34) },
                new LedStrip { HorizontalAlignment = HorizontalAlignment.Right, Width = 8, VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(0, 34, 0, 34) },
            },
        }, cornerRadius: 12);
        content.Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(content, Loc.T("overlay.doubleClickToLeaveTheSession2"));
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
            return Skin.LcdText(Loc.T("overlay.nothingCopiedYet"), 13);

        var self = participants.FirstOrDefault(p => p.IsSelf);
        // Il nome di chi ha copiato è quello di oggi (se ha cambiato nome dopo la copia), non quello di allora.
        var originName = participants.FirstOrDefault(p => p.DeviceId == current.OriginDeviceId)?.DisplayName ?? current.OriginName;
        var origin = DisplayText.Sanitize(originName, 64) + (self is not null && current.OriginDeviceId == self.DeviceId ? Loc.T("overlay.you") : "");
        var size = current.SizeBytes is { } bytes ? DisplayText.FormatSize(bytes) : "—";
        var title = DisplayText.Sanitize(current.Title, 260, "");
        if (current.Kind == ClipboardContentKind.Files && !current.IsSensitive)
            title = DisplayText.MiddleEllipsis(title, TitleChars);
        if (!current.IsShared)
            title += Loc.T("overlay.notShared");

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
            ToolTip.SetTip(chip, participant.IsSelf ? Loc.T("common.you", role) : role);
            panel.Children.Add(chip);
        }

        return panel;
    }

    /// <summary>Stella per l'host, frecce per bidirezionale e sola lettura (riceve soltanto).</summary>
    public static (string Glyph, string Role) Glyph(SessionParticipant participant) =>
        participant.IsHost ? (Icons.Star, "host")
        : participant.Direction == ShareDirection.ReadOnly ? (Icons.ArrowDown, participant.Granted == ShareDirection.ReadOnly
            ? Loc.T("common.readOnly")
            : Loc.T("overlay.readOnlyTheirChoice"))
        : (Icons.Bidirectional, Loc.T("common.twoWay"));

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
