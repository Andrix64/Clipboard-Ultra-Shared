using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Cus.Core.Abstractions;
using Cus.Core.Hosting;
using Cus.Core.Node;
using Cus.Core.Storage;

namespace Cus.UI;

public partial class MainWindow : Window
{
    private const int MaxActivityLines = 200;

    private readonly MainWindowModel _model;
    private readonly List<HistoryEntry> _history = [];
    private readonly List<(Button Button, Control Page)> _leftPages = [];
    private readonly List<(Button Button, Control Page)> _rightPages = [];

    /// <summary>Testi completi delle righe del display, che lì possono comparire accorciati.</summary>
    private string _lastActivityFull = "";
    private string _currentFull = "";

    /// <summary>Firma dell'ultimo elenco dei peer disegnato: si ridisegna solo se cambia.</summary>
    private string _peersSignature = "";

    /// <summary>Arresto del nodo avviato (un secondo clic su chiudi non lo ripete).</summary>
    private bool _shutdownStarted;

    /// <summary>Arresto concluso: la prossima chiusura è quella vera.</summary>
    private bool _closeAllowed;

    /// <summary>Uscita chiesta dall'area di notifica (o dal sistema): niente riduzione in background.</summary>
    private bool _exitRequested;

    /// <summary>La lingua è cambiata: i testi scritti dal codice si riscrivono (quelli dello XAML li aggiorna <see cref="Tr"/>).</summary>
    private event Action? LanguageChanged;

    /// <summary>Usato solo dal designer di Avalonia.</summary>
    public MainWindow()
        : this(MainWindowModel.Empty)
    {
    }

    public MainWindow(MainWindowModel model)
    {
        _model = model;
        InitializeComponent();
        Title = model.Title;
        Skin.SetContent(MinimizeButton, Icons.Minus);
        Skin.SetContent(CloseButton, Icons.Xmark);
        void StaticTexts()
        {
            Skin.SetContent(OfferButton, Loc.T("main.offerAsHostIcon", Icons.Eye));
            Skin.SetRich(FriendsHint, Loc.T("main.joinsWithoutConfirmationConnectsWheneverThey", Icons.SignIn, Icons.Infinity, Icons.Key));
            InfoText.Text = string.Join(Environment.NewLine, model.InfoLines());
        }

        StaticTexts();
        LanguageChanged += StaticTexts;
        Loc.Changed += () => Post(() => LanguageChanged?.Invoke());
        WireChrome();
        WireExpandableTexts();

        if (model.StartupError is { } error)
        {
            LcdState.Text = Loc.T("main.error");
            LcdParticipants.Text = Loc.T("main.startupFailed");
            StartupErrorPanel.IsVisible = true;
            StartupErrorText.Text = error;
            LeftSide.IsEnabled = false;
            RightSide.IsEnabled = false;
            return;
        }

        if (model.Node is not { } node)
            return;

        node.Activity += line => Post(() => AddActivity(line));
        node.SessionChanged += (_, _) => Post(RefreshSession);
        node.PeersChanged += (_, _) => Post(() =>
        {
            RefreshPeers();
            RefreshFriends();
        });
        node.JoinRequestsChanged += (_, _) => Post(RefreshRequests);
        node.CurrentClipboardChanged += (_, _) => Post(RefreshCurrent);
        node.TransfersChanged += (_, _) => Post(RefreshTransfersButton);
        node.TransfersRequested += (_, _) => Post(() => TransfersWindow.ShowFor(node));
        node.Friends.Changed += (_, _) => Post(() =>
        {
            RefreshFriends();
            RefreshParticipants();
        });
        if (model.Notifier is { } notifier)
            notifier.Notified += (message, isError) => AddActivity((isError ? "! " : "") + message);

        void ShowIdentity()
        {
            Skin.SetRich(LcdDevice, $"{Icons.Desktop} {node.Identity.DisplayName}");
            IdentityText.Text = $"{node.Identity.DisplayName} ({node.Identity.UserName})\n{Loc.T("main.fingerprint")} {node.Identity.Info.Fingerprint}";
            LcdPort.Text = Loc.T("main.port", node.Port);
        }

        ShowIdentity();
        NameInput.Text = node.Identity.DisplayName;
        node.IdentityChanged += (_, _) => Post(ShowIdentity);
        LanguageChanged += ShowIdentity;

        WireNetworkPage(node);
        WireSessionPage(node);
        WireHistoryPage(node);
        WireSettingsPage(node);
        WireSimulationPage(model.Simulation);

        // Gli host annunciati in rete scadono: l'elenco si ricontrolla periodicamente (e si ridisegna
        // solo se cambia, così i pulsanti non spariscono sotto il mouse).
        DispatcherTimer.Run(() =>
        {
            RefreshPeers();
            return true;
        }, TimeSpan.FromSeconds(3));

        RefreshSession();
        RefreshPeers();
        RefreshFriends();
        RefreshCurrent();
        LanguageChanged += () =>
        {
            _peersSignature = "";
            RefreshSession();
            RefreshPeers();
            RefreshFriends();
            RefreshCurrent();
            if (HistoryPage.IsVisible)
                _ = RefreshHistoryAsync();
        };
    }

    /// <summary>Riapre la finestra dall'area di notifica.</summary>
    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Chiude davvero CUS (menu dell'area di notifica).</summary>
    public void ExitApplication()
    {
        _exitRequested = true;
        Close();
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _closeAllowed || _model.Shutdown is not { } shutdown)
            return;

        if (e.CloseReason == WindowCloseReason.OSShutdown)
        {
            // Disconnessione o spegnimento di Windows: non si trattiene il sistema. Si tenta un
            // arresto ordinato per al massimo due secondi, fuori dal thread della UI.
            _shutdownStarted = true;
            try
            {
                Task.Run(shutdown).Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"CUS: arresto durante lo spegnimento di Windows fallito: {ex}");
            }

            return;
        }

        // In background: la finestra si nasconde e CUS resta nell'area di notifica.
        if (!_exitRequested && !_shutdownStarted && _model.Node?.KeepRunningInBackground == true && _model.HasTrayIcon)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        // Si ferma il nodo (uscita dalla sessione inclusa) prima di chiudere davvero. Se l'arresto
        // è già in corso (secondo clic su chiudi) si aspetta che finisca: la finestra si chiuderà da sola.
        e.Cancel = true;
        if (_shutdownStarted)
            return;
        _shutdownStarted = true;
        LcdState.Text = Loc.T("main.closing");
        try
        {
            await shutdown();
        }
        catch (Exception ex)
        {
            // async void: un'eccezione qui chiuderebbe il processo senza spiegazioni.
            System.Diagnostics.Trace.WriteLine($"CUS: arresto non riuscito: {ex}");
        }

        _closeAllowed = true;
        try
        {
            Close();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"CUS: chiusura della finestra non riuscita: {ex}");
        }
    }

    // ================================================================ telaio e pagine

    private void WireChrome()
    {
        void Drag(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        }

        DragBar.PointerPressed += Drag;
        LcdPanel.PointerPressed += Drag;
        CloseButton.Click += (_, _) => Close();
        MinimizeButton.Click += (_, _) => WindowState = WindowState.Minimized;

        _leftPages.AddRange([(PageHistoryButton, HistoryPage), (PageSettingsButton, SettingsPage), (PageSimButton, SimPage)]);
        foreach (var (button, page) in _leftPages)
            button.Click += (_, _) => ShowPage(_leftPages, page);
        ShowPage(_leftPages, HistoryPage);

        _rightPages.AddRange([(PageNetworkButton, NetworkPage), (PageFriendsButton, FriendsPage)]);
        PageNetworkButton.Click += (_, _) => ShowNetworkOrSession();
        PageFriendsButton.Click += (_, _) =>
        {
            // La pagina sessione non è nella lista delle schede: va nascosta a mano, altrimenti si sovrappone.
            SessionPage.IsVisible = false;
            ShowPage(_rightPages, FriendsPage);
        };
        ShowPage(_rightPages, NetworkPage);
    }

    private void ShowPage(List<(Button Button, Control Page)> pages, Control page)
    {
        foreach (var (button, candidate) in pages)
        {
            candidate.IsVisible = candidate == page;
            button.Classes.Set("active", candidate == page);
        }

        if (page == HistoryPage)
            _ = RefreshHistoryAsync();
    }

    /// <summary>Prima scheda a destra: la ricerca in rete, o la sessione quando ci si offre o si è dentro.</summary>
    private void ShowNetworkOrSession()
    {
        var inSession = _model.Node?.Session is not null;
        NetworkPage.IsVisible = !inSession;
        SessionPage.IsVisible = inSession;
        FriendsPage.IsVisible = false;
        PageNetworkButton.Classes.Set("active", true);
        PageFriendsButton.Classes.Set("active", false);
    }

    private bool FriendsPageShown => FriendsPage.IsVisible;

    // ================================================================ utilità

    private PeerEndpoint? ParseAddress(string? text)
    {
        try
        {
            return PeerEndpoint.Parse(text ?? "");
        }
        catch (ArgumentException ex)
        {
            // Senza il " (Parameter 'value')" che .NET aggiunge al messaggio.
            _model.Notifier?.Error(ex.ParamName is { } name ? ex.Message.Replace($" (Parameter '{name}')", "") : ex.Message);
            return null;
        }
    }

    /// <summary>Esegue un'azione con il pulsante disattivato: niente secondo clic mentre è in corso.</summary>
    private async Task RunFrom(Button button, Func<Task> action)
    {
        if (!button.IsEnabled)
            return;
        button.IsEnabled = false;
        try
        {
            await Run(action);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async Task Run(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (CusOperationException ex)
        {
            _model.Notifier?.Error(ex.Message);
        }
        catch (Exception ex)
        {
            _model.Notifier?.Error(Loc.T("main.unexpectedError", ex.Message));
        }
    }

    private void AddActivity(string line)
    {
        var items = (ActivityList.ItemsSource as List<string>)?.ToList() ?? [];
        items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {line}");
        if (items.Count > MaxActivityLines)
            items.RemoveRange(MaxActivityLines, items.Count - MaxActivityLines);
        ActivityList.ItemsSource = items;
        Skin.SetRich(LcdLast, $"{Icons.AnglesRight} " + Shorten(line));
        _lastActivityFull = line;
    }

    /// <summary>Descrizione di una voce per le liste: testo ripulito (può arrivare da un altro PC) e accorciato.</summary>
    private static string Describe(HistoryEntry entry) => Shorten(DisplayText.Sanitize(entry.Describe(), 200, Loc.T("common.empty")));

    private static string Shorten(string text)
    {
        text = text.ReplaceLineEndings(" ");
        return text.Length <= 80 ? text : text[..77] + "...";
    }

    private static void Post(Action action) => Dispatcher.UIThread.Post(action);
}
