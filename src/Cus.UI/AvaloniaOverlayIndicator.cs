using Avalonia;
using Avalonia.Animation;
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
/// Si sposta trascinandolo. Il doppio clic chiede di uscire dalla sessione: un clic singolo (per
/// esempio mirando alla barra di una finestra sotto) non fa nulla. I pulsanti (occhio, file in uscita
/// e in arrivo, sola lettura) rispondono al clic singolo. L'occhio nasconde l'indicatore: al suo posto
/// resta solo l'occhio, invisibile finché il mouse non ci passa sopra, che lo fa ricomparire.
/// </summary>
public sealed class AvaloniaOverlayIndicator(Func<Screens?> screens, Action<Window>? configureWindow = null) : IOverlayIndicator
{
    private const int Margin = 12;
    private const double Width = 380;

    /// <summary>Caratteri del nome di un file nel display (font a larghezza fissa).</summary>
    private const int TitleChars = 40;

    /// <summary>Area sensibile attorno all'occhio nascosto: basta avvicinarsi per farlo comparire.</summary>
    private const double EyeHoverPadding = 6;

    /// <summary>Spostamento minimo (in DIP) perché la pressione diventi un trascinamento e non un clic.</summary>
    private const double DragThreshold = 4;

    private readonly Action<Window>? _configureWindow = configureWindow;
    private readonly List<ScreenOverlay> _overlays = [];

    /// <summary>
    /// Posizione scelta trascinando e indicatore nascosto, per monitor (chiave: area del monitor).
    /// La posizione resta per tutta l'esecuzione; l'indicatore nascosto torna visibile alla sessione dopo.
    /// </summary>
    private readonly Dictionary<PixelRect, Placement> _placements = [];

    private SessionIndicatorState? _state;
    private Screens? _subscribedScreens;
    private bool _languageHooked;

    public event EventHandler? LeaveRequested;

    public event EventHandler? TransfersRequested;

    public event EventHandler? IncomingTransfersRequested;

    public event EventHandler? DirectionToggleRequested;

    public void Show(SessionIndicatorState state) => Dispatcher.UIThread.Post(() =>
    {
        if (!_languageHooked)
        {
            // Cambio di lingua: si ricostruisce tutto con i testi nuovi (posizioni e occhi restano).
            _languageHooked = true;
            Loc.Changed += () => Dispatcher.UIThread.Post(() =>
            {
                if (_state is not null)
                    Rebuild();
            });
        }

        var hadWindows = _overlays.Count > 0;
        if (!hadWindows)
        {
            // Sessione nuova: chi l'aveva nascosto durante quella prima deve vedere che si condivide di nuovo.
            foreach (var placement in _placements.Values)
                placement.Hidden = false;
        }

        _state = state;
        if (hadWindows)
        {
            // Aggiornamento sul posto: chiudere e riaprire le finestre a ogni copia lampeggerebbe.
            foreach (var overlay in _overlays)
                overlay.Update(state);
        }
        else
        {
            Rebuild();
            OnHiddenChanged();
        }
    });

    public void Hide() => Dispatcher.UIThread.Post(() =>
    {
        _state = null;
        CloseAll();
        OnHiddenChanged();
    });

    /// <summary>
    /// È cambiato ciò che decide i comandi dell'icona nell'area di notifica: indicatore comparso o sparito
    /// (sessione condivisa o no), nascosto o rimostrato. Sul thread della UI.
    /// </summary>
    public event EventHandler? IndicatorStateChanged;

    /// <summary>C'è una condivisione attiva, quindi un indicatore da nascondere o mostrare. Sul thread della UI.</summary>
    public bool IsActive => _state is not null && _overlays.Count > 0;

    /// <summary>Su tutti i monitor è rimasto solo l'occhio. Sul thread della UI.</summary>
    public bool IsHidden => IsActive && _overlays.All(o => o.IsHidden);

    /// <summary>Nasconde (resta solo l'occhio in alto a destra) o rimostra l'indicatore su tutti i monitor.</summary>
    public void SetHidden(bool hidden) => Dispatcher.UIThread.Post(() =>
    {
        foreach (var overlay in _overlays)
            overlay.SetHidden(hidden);
    });

    private void OnHiddenChanged() => IndicatorStateChanged?.Invoke(this, EventArgs.Empty);

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
            if (!_placements.TryGetValue(screen.Bounds, out var placement))
            {
                var margin = (int)(Margin * screen.Scaling);
                _placements[screen.Bounds] = placement = new Placement { RightGap = margin, TopGap = margin };
            }

            var overlay = new ScreenOverlay(this, screen, placement);
            _overlays.Add(overlay);
            overlay.Open(_state);
        }
    }

    private void RequestLeave() => LeaveRequested?.Invoke(this, EventArgs.Empty);

    private void CloseAll()
    {
        foreach (var overlay in _overlays)
            overlay.Close();
        _overlays.Clear();
    }

    private static string WindowTitle => Loc.T("overlay.cusSharedClipboard");

    private Window CreateWindow(Control content, SizeToContent sizeToContent) => new()
    {
        Title = WindowTitle,
        Content = content,
        SizeToContent = sizeToContent,
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

    /// <param name="hide">Clic sull'occhio: nasconde l'indicatore di questo monitor.</param>
    private Control BuildContent(SessionIndicatorState state, Action hide)
    {
        var header = new DockPanel { LastChildFill = true };

        // Occhio per nascondere: quadrato, alto quanto gli altri pulsanti dell'intestazione.
        var eye = EyeButton(Icons.EyeSlash, Loc.T("overlay.hideIndicator"));
        eye.Click += (_, _) => hide();
        var eyeBox = new SquareBox { Child = eye, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(eyeBox, Dock.Right);
        header.Children.Add(eyeBox);

        if (state.Transfers > 0)
        {
            var transfers = SmallButton(
                state.ActiveTransfers > 0 ? $"{Icons.Upload} {state.ActiveTransfers}" : Icons.Upload,
                state.ActiveTransfers > 0
                    ? Loc.T("overlay.copiesOfYourFilesInProgress", state.ActiveTransfers)
                    : Loc.T("overlay.copiesOfYourFilesJustFinished"));
            transfers.Click += (_, _) => TransfersRequested?.Invoke(this, EventArgs.Empty);
            DockPanel.SetDock(transfers, Dock.Right);
            header.Children.Add(transfers);
        }

        if (state.IncomingTransfers > 0)
        {
            var incoming = SmallButton(
                state.ActiveIncomingTransfers > 0 ? $"{Icons.Download} {state.ActiveIncomingTransfers}" : Icons.Download,
                state.ActiveIncomingTransfers > 0
                    ? Loc.T("overlay.incomingFilesInProgress", state.ActiveIncomingTransfers)
                    : Loc.T("overlay.incomingFilesJustFinished"));
            incoming.Click += (_, _) => IncomingTransfersRequested?.Invoke(this, EventArgs.Empty);
            DockPanel.SetDock(incoming, Dock.Right);
            header.Children.Add(incoming);
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
                body,
                new LedStrip { HorizontalAlignment = HorizontalAlignment.Left, Width = 8, VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(0, 34, 0, 34) },
                new LedStrip { HorizontalAlignment = HorizontalAlignment.Right, Width = 8, VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(0, 34, 0, 34) },
            },
        }, cornerRadius: 12, seed: 5);
        content.Cursor = new Cursor(StandardCursorType.SizeAll);
        ToolTip.SetTip(content, Loc.T("overlay.doubleClickToLeaveTheSessionTip"));
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

    /// <summary>Occhio: pulsante piccolo come gli altri dell'intestazione, da mettere in un <see cref="SquareBox"/>.</summary>
    private static Button EyeButton(string glyph, string tip)
    {
        var button = SmallButton(glyph, tip);
        button.Classes.Add("square");
        button.Margin = new Thickness(0);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.VerticalAlignment = VerticalAlignment.Stretch;
        return button;
    }

    /// <summary>
    /// Contenitore quadrato: il lato è l'altezza naturale del contenuto. Per l'occhio è l'altezza degli
    /// altri pulsanti piccoli (stesso stile, stesso font), aperto o chiuso che sia.
    /// </summary>
    private sealed class SquareBox : Decorator
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            if (Child is not { } child)
                return default;
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            var side = child.DesiredSize.Height;
            child.Measure(new Size(side, side));
            return new Size(side, side);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var side = Math.Min(finalSize.Width, finalSize.Height);
            Child?.Arrange(new Rect((finalSize.Width - side) / 2, (finalSize.Height - side) / 2, side, side));
            return finalSize;
        }
    }

    /// <summary>Dove sta l'indicatore su un monitor, in pixel fisici rispetto all'area di lavoro.</summary>
    private sealed class Placement
    {
        /// <summary>Dal bordo destro dell'indicatore al bordo destro dell'area di lavoro.</summary>
        public int RightGap { get; set; }

        /// <summary>Dal bordo superiore dell'area di lavoro a quello dell'indicatore.</summary>
        public int TopGap { get; set; }

        public bool Hidden { get; set; }
    }

    /// <summary>L'indicatore di un monitor: la scocca intera, o solo l'occhio se è nascosto.</summary>
    private sealed class ScreenOverlay(AvaloniaOverlayIndicator owner, Screen screen, Placement placement)
    {
        private Window? _window;
        private bool _showsPanel;

        // Trascinamento in corso: punto di partenza del puntatore e della finestra, in pixel dello schermo.
        private PixelPoint? _dragStart;
        private PixelPoint _dragWindowStart;
        private bool _dragging;

        private PixelRect Area => screen.WorkingArea;

        private double Scaling => screen.Scaling;

        public void Open(SessionIndicatorState state)
        {
            if (placement.Hidden)
                OpenEye();
            else
                OpenPanel(state);
        }

        public bool IsHidden => placement.Hidden;

        public void Update(SessionIndicatorState state)
        {
            if (_showsPanel && _window is { } window)
                window.Content = owner.BuildContent(state, HidePanel);
        }

        /// <summary>Dal menu dell'icona nell'area di notifica: nasconde o mostra l'indicatore di questo monitor.</summary>
        public void SetHidden(bool hidden)
        {
            if (hidden)
                HidePanel();
            else if (placement.Hidden)
                ShowPanel();
        }

        public void Close()
        {
            _window?.Close();
            _window = null;
        }

        private void OpenPanel(SessionIndicatorState state)
        {
            Close();
            var window = owner.CreateWindow(owner.BuildContent(state, HidePanel), SizeToContent.WidthAndHeight);
            _window = window;
            _showsPanel = true;

            // La dimensione definitiva arriva dopo il layout (SizeToContent): si riallinea a destra
            // a ogni cambio di dimensione, alla distanza scelta trascinando.
            void Place(double width)
            {
                var widthPx = (int)Math.Ceiling(width * Scaling);
                window.Position = Clamp(
                    new PixelPoint(Area.Right - widthPx - placement.RightGap, Area.Y + placement.TopGap),
                    widthPx,
                    (int)Math.Ceiling(window.ClientSize.Height * Scaling));
            }

            window.SizeChanged += (_, e) =>
            {
                if (_dragStart is null)
                    Place(e.NewSize.Width);
            };
            HookDrag(window);
            Place(Width + 20);
            window.Show();
            owner._configureWindow?.Invoke(window);
        }

        /// <summary>
        /// Al posto della scocca resta l'occhio, sempre nell'angolo in alto a destra del monitor (anche se
        /// l'indicatore era stato spostato), visibile solo al passaggio del mouse.
        /// </summary>
        private void OpenEye()
        {
            Close();
            var eye = EyeButton(Icons.Eye, Loc.T("overlay.showIndicator"));
            // La finestra si chiude fuori dal gestore del clic che la riguarda.
            eye.Click += (_, _) => Dispatcher.UIThread.Post(ShowPanel);

            // Stessa scocca metallica dell'indicatore aperto, in piccolo attorno all'occhio.
            var frame = Skin.Chassis(new SquareBox { Child = eye }, cornerRadius: 8, seed: 5, screws: ScrewSide.None);
            frame.Opacity = 0;
            frame.Transitions = [new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(150) }];

            // Sfondo quasi trasparente ma non vuoto: l'area attorno all'occhio deve ricevere il mouse.
            var zone = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
                Padding = new Thickness(EyeHoverPadding),
                Child = frame,
            };
            zone.PointerEntered += (_, _) => frame.Opacity = 1;
            zone.PointerExited += (_, _) => frame.Opacity = 0;

            var window = owner.CreateWindow(zone, SizeToContent.WidthAndHeight);
            _window = window;
            _showsPanel = false;

            // La scocca dell'occhio sta a Margin dall'angolo, come quella dell'indicatore; la finestra
            // la supera dell'area sensibile.
            var gap = (int)Math.Round((Margin - EyeHoverPadding) * Scaling);
            void Place(Size size)
            {
                var widthPx = (int)Math.Ceiling(size.Width * Scaling);
                window.Position = Clamp(
                    new PixelPoint(Area.Right - widthPx - gap, Area.Y + gap),
                    widthPx,
                    (int)Math.Ceiling(size.Height * Scaling));
            }

            window.SizeChanged += (_, e) => Place(e.NewSize);
            Place(default);
            window.Show();
            owner._configureWindow?.Invoke(window);
        }

        private void HidePanel()
        {
            if (placement.Hidden)
                return;

            placement.Hidden = true;
            // La finestra si chiude fuori dal gestore del clic che la riguarda.
            Dispatcher.UIThread.Post(() =>
            {
                if (_window is not null)
                    OpenEye();
            });
            owner.OnHiddenChanged();
        }

        private void ShowPanel()
        {
            placement.Hidden = false;
            if (owner._state is { } state && _window is not null)
                OpenPanel(state);
            owner.OnHiddenChanged();
        }

        /// <summary>
        /// Premere e trascinare sposta l'indicatore; il doppio clic chiede di uscire dalla sessione.
        /// I gestori stanno sulla finestra, non sul contenuto, che si ricostruisce a ogni aggiornamento.
        /// I pulsanti gestiscono da sé il proprio clic e non arrivano qui.
        /// </summary>
        private void HookDrag(Window window)
        {
            window.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(window).Properties.IsLeftButtonPressed)
                    return;

                // Solo un doppio clic sinistro deliberato: i clic accidentali sull'angolo in alto a destra
                // (dove stanno i pulsanti delle finestre) non devono chiudere la condivisione.
                if (e.ClickCount >= 2)
                {
                    _dragStart = null;
                    e.Handled = true;
                    owner.RequestLeave();
                    return;
                }

                _dragStart = window.PointToScreen(e.GetPosition(window));
                _dragWindowStart = window.Position;
                _dragging = false;
                e.Pointer.Capture(window);
            };

            window.PointerMoved += (_, e) =>
            {
                if (_dragStart is not { } start)
                    return;

                var now = window.PointToScreen(e.GetPosition(window));
                var (dx, dy) = (now.X - start.X, now.Y - start.Y);
                var threshold = DragThreshold * Scaling;
                if (!_dragging && Math.Abs(dx) < threshold && Math.Abs(dy) < threshold)
                    return;

                _dragging = true;
                window.Position = Clamp(
                    new PixelPoint(_dragWindowStart.X + dx, _dragWindowStart.Y + dy),
                    (int)Math.Ceiling(window.ClientSize.Width * Scaling),
                    (int)Math.Ceiling(window.ClientSize.Height * Scaling));
            };

            window.PointerReleased += (_, e) => EndDrag(window, e.Pointer);
            window.PointerCaptureLost += (_, _) => EndDrag(window, null);
        }

        private void EndDrag(Window window, IPointer? pointer)
        {
            if (_dragStart is null)
                return;

            _dragStart = null;
            pointer?.Capture(null);
            if (!_dragging)
                return;

            _dragging = false;
            // Da qui in poi l'indicatore resta a questa distanza dall'angolo in alto a destra, anche
            // quando cambia dimensione.
            var widthPx = (int)Math.Ceiling(window.ClientSize.Width * Scaling);
            placement.RightGap = Area.Right - (window.Position.X + widthPx);
            placement.TopGap = window.Position.Y - Area.Y;
        }

        /// <summary>Dentro l'area di lavoro del monitor: non si può perdere fuori dallo schermo.</summary>
        private PixelPoint Clamp(PixelPoint position, int widthPx, int heightPx) => new(
            Math.Clamp(position.X, Area.X, Math.Max(Area.X, Area.Right - widthPx)),
            Math.Clamp(position.Y, Area.Y, Math.Max(Area.Y, Area.Bottom - heightPx)));
    }
}
