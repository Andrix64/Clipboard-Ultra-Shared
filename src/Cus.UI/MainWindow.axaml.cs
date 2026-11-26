using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cus.Core.Abstractions;
using Cus.Core.Friends;
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
            Skin.SetContent(OfferButton, Loc.T("mainWindow.offerAsHost", Icons.Eye));
            Skin.SetRich(FriendsHint, Loc.T("mainWindow.joinsWithoutConfirmationConnectsWheneverThey", Icons.SignIn, Icons.Infinity, Icons.Key));
            InfoText.Text = string.Join(Environment.NewLine, model.InfoLines());
        }

        StaticTexts();
        LanguageChanged += StaticTexts;
        Loc.Changed += () => Post(() => LanguageChanged?.Invoke());
        WireChrome();
        WireExpandableTexts();

        if (model.StartupError is { } error)
        {
            LcdState.Text = Loc.T("mainWindow.error");
            LcdParticipants.Text = Loc.T("mainWindow.startupFailed");
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
            IdentityText.Text = $"{node.Identity.DisplayName} ({node.Identity.UserName})\n{Loc.T("mainWindow.fingerprint")} {node.Identity.Info.Fingerprint}";
            LcdPort.Text = Loc.T("mainWindow.port", node.Port);
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
        LcdState.Text = Loc.T("CHIUSURA...", "CLOSING...");
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

    // ================================================================ rete

    private void WireNetworkPage(CusNode node)
    {
        OfferButton.Click += async (_, _) => await RunFrom(OfferButton, () => node.SetVisibleAsync(true));
        JoinAddressButton.Click += async (_, _) =>
        {
            if (ParseAddress(AddressInput.Text) is { } endpoint)
                await RunFrom(JoinAddressButton, () => Join(node, endpoint.Host, endpoint.Port, expectedDeviceId: null));
        };
        AddPeerButton.Click += (_, _) =>
        {
            if (ParseAddress(AddressInput.Text) is { } endpoint)
            {
                node.AddManualPeer(endpoint.ToString());
                AddressInput.Text = "";
            }
        };
    }

    private async Task Join(CusNode node, string host, int port, string? expectedDeviceId)
    {
        var result = await node.JoinAsync(host, port, expectedDeviceId);
        if (result.Outcome == JoinOutcome.Rejected)
            _model.Notifier?.Info(Loc.T("mainWindow.didNotLetYouIn", result.PeerName) + (result.Reason is null ? "." : $": {result.Reason}."));
    }

    private string _radarSignature = "";

    /// <summary>Radar: un pallino per ogni host visto in rete e uno, con la propria iniziale, per ogni persona collegata.</summary>
    private void RefreshRadar(CusNode node, IReadOnlyList<PeerView> peers)
    {
        var blips = new List<RadarBlip>();
        var seen = new HashSet<string>();
        if (node.Session is { } session)
        {
            foreach (var p in session.Participants.Where(p => !p.IsSelf))
                if (seen.Add(p.DeviceId))
                    blips.Add(new RadarBlip(p.DeviceId, Initial(p.DisplayName), Connected: true));
        }

        foreach (var peer in peers.Where(p => p.IsAnnounced))
            if (seen.Add(peer.DeviceId ?? peer.Address))
                blips.Add(new RadarBlip(peer.DeviceId ?? peer.Address, "", Connected: false));

        var signature = string.Join("|", blips.Select(b => $"{b.Key}:{b.Label}:{b.Connected}"));
        if (signature == _radarSignature)
            return;
        _radarSignature = signature;
        LcdRadar.Blips = blips;
    }

    private static string Initial(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            return "?";
        // Il primo code point, non il primo char: un'emoji iniziale è una coppia surrogata.
        var first = trimmed.EnumerateRunes().First();
        return Rune.ToUpperInvariant(first).ToString();
    }

    private void RefreshPeers()
    {
        if (_model.Node is not { } node)
            return;
        var peers = node.Peers;
        RefreshRadar(node, peers);
        var signature =string.Join("\n", peers.Select(p => $"{p.DeviceId}|{p.DisplayName}|{p.Address}|{p.Participants}|{p.IsAnnounced}|{p.Agreement}|{p.IsManual}|{p.LooksLikeFriend}"));
        if (signature == _peersSignature)
            return;
        _peersSignature = signature;

        PeersPanel.Children.Clear();
        if (peers.Count == 0)
        {
            PeersPanel.Children.Add(Hint(Loc.T("mainWindow.noHostsOnTheNetworkWhoever")));
            return;
        }

        foreach (var peer in peers)
        {
            var who = peer.IsManual ? peer.Address : $"{peer.DisplayName}{(peer.UserName.Length > 0 ? $" ({peer.UserName})" : "")}";
            var detail = peer.IsAnnounced
                ? (peer.Participants <= 1
                    ? Loc.T("mainWindow.freeOfferingAsHost")
                    : Loc.T("mainWindow.peopleInSession", peer.Participants))
                : peer.IsManual ? Loc.T("mainWindow.savedAddress") : Loc.T("mainWindow.friendNotVisibleNow");
            var badges = (peer.LooksLikeFriend is not null ? $"{Icons.Warning} " : "")
                + (peer.Agreement switch { AgreementLevel.Anytime => $"{Icons.Infinity} ", AgreementLevel.AutoJoin => $"{Icons.SignIn} ", _ => "" })
                + (peer.DeviceId is { } id && node.Friends.TrustsWithSensitive(id) ? $"{Icons.Key} " : "");

            // Gli annunci non sono autenticati: per chi non è un amico conta l'impronta (verificata alla
            // connessione), non il nome, e un nome uguale a quello di un amico si segnala.
            if (peer.LooksLikeFriend is { } imitated)
                detail = Loc.T("mainWindow.notYourFriendSameNameDifferent", imitated);
            if (peer.Agreement == AgreementLevel.None && peer.Fingerprint is { } fingerprint)
                detail += Loc.T("mainWindow.fingerprint2", fingerprint);

            var buttons = new List<Control>();
            if (peer.CanJoin)
            {
                var join = RowButton(Loc.T("common.join"), peer.Agreement == AgreementLevel.None
                    ? Loc.T("mainWindow.askToJoinTheHostDecides")
                    : Loc.T("mainWindow.joinWithoutConfirmationPact"));
                join.Click += async (_, _) => await RunFrom(join, async () =>
                {
                    if (peer.LooksLikeFriend is { } friendName && !await ConfirmDialog.ShowAsync(this, Loc.T("mainWindow.notYourFriend"),
                            Loc.T("mainWindow.hasTheSameNameAsYour", peer.DisplayName, friendName, peer.Fingerprint),
                            Loc.T("mainWindow.joinAnyway")))
                        return;
                    await Join(node, peer.Host, peer.Port, peer.DeviceId);
                });
                buttons.Add(join);
            }

            if (peer.IsManual)
            {
                var remove = RowButton(Icons.Xmark, Loc.T("mainWindow.removeFromTheList"));
                remove.Click += (_, _) => node.RemoveManualPeer(peer.Address);
                buttons.Add(remove);
            }

            PeersPanel.Children.Add(Row($"{badges}{who}", $"{detail} · {peer.Address}", highlight: false, buttons));
        }
    }

    // ================================================================ sessione

    private void WireSessionPage(CusNode node)
    {
        VisibleToggle.Click += async (_, _) => await RunFrom(VisibleToggle, () => node.SetVisibleAsync(!node.IsVisible));
        SensitiveToggle.Click += async (_, _) =>
            await RunFrom(SensitiveToggle, () => node.SetSensitiveForAllAsync(node.Session?.SensitiveForAll != true));
        TransfersButton.Click += (_, _) => TransfersWindow.ShowFor(node);
        LeaveButton.Click += async (_, _) => await RunFrom(LeaveButton, node.LeaveSessionAsync);
    }

    private void RefreshSession()
    {
        if (_model.Node is not { } node)
            return;

        var session = node.Session;
        if (session is null)
        {
            LcdState.Text = Loc.T("common.waiting");
            LcdParticipants.Text = Loc.T("mainWindow.offerAsHostOrJoinNetwork");
        }
        else
        {
            LcdState.Text = session.Role == SessionRole.Host
                ? (session.IsSharing ? Loc.T("mainWindow.sharedHost") : Loc.T("mainWindow.visible"))
                : session.MyDirection == ShareDirection.ReadOnly ? Loc.T("mainWindow.readOnly") : Loc.T("mainWindow.shared");
            Skin.SetRich(LcdParticipants, session.IsSharing
                ? string.Join("  ", session.Participants.Select(p => $"{AvaloniaOverlayIndicator.Glyph(p).Glyph} {p.DisplayName}{(p.IsSelf ? Icons.CaretLeft : "")}"))
                : session.IsVisible
                    ? Loc.T("mainWindow.waitingForSomeoneToAskTo")
                    : Loc.T("mainWindow.nobodyElseInTheSession"));
        }

        RefreshRadar(node, node.Peers);
        var host = session?.Role == SessionRole.Host;
        PageNetworkButton.Content = session is null ? Loc.T("common.network") : Loc.T("mainWindow.session");
        VisibleToggle.IsVisible = host;
        VisibleToggle.Classes.Set("active", session?.IsVisible == true);
        Skin.SetContent(VisibleToggle, session?.IsVisible == true
            ? Loc.T("mainWindow.visibleYes", Icons.Eye)
            : Loc.T("mainWindow.visibleNo", Icons.EyeSlash));
        SensitiveToggle.IsVisible = host && session!.IsSharing;
        SensitiveToggle.Classes.Set("active", session?.SensitiveForAll == true);
        Skin.SetContent(LeaveButton, host ? Loc.T("mainWindow.endSession", Icons.Stop) : Loc.T("mainWindow.leave", Icons.Stop));
        RequestsTitle.IsVisible = host;

        if (!FriendsPageShown)
            ShowNetworkOrSession();
        RefreshRequests();
        RefreshParticipants();
        RefreshTransfersButton();
    }

    private void RefreshRequests()
    {
        if (_model.Node is not { } node)
            return;
        RequestsPanel.Children.Clear();
        if (node.Session?.Role != SessionRole.Host)
            return;

        var requests = node.JoinRequests;
        if (requests.Count == 0)
        {
            RequestsPanel.Children.Add(Hint(node.IsVisible
                ? Loc.T("mainWindow.noPendingRequests")
                : Loc.T("mainWindow.youAreHiddenNobodyCanAsk")));
            return;
        }

        foreach (var request in requests)
        {
            var bidirectional = RowButton(Icons.Bidirectional, Loc.T("mainWindow.letInWithTwoWayAccess"));
            var readOnly = RowButton(Icons.ArrowDown, Loc.T("mainWindow.letInReadOnly"));
            var reject = RowButton(Icons.Xmark, Loc.T("mainWindow.reject"));
            bidirectional.Click += (_, _) => node.AnswerJoinRequest(request.RequestId, ConsentDecision.Accept);
            readOnly.Click += (_, _) => node.AnswerJoinRequest(request.RequestId, ConsentDecision.AcceptReadOnly);
            reject.Click += (_, _) => node.AnswerJoinRequest(request.RequestId, ConsentDecision.Reject);
            RequestsPanel.Children.Add(Row(
                $"{(request.LooksLikeFriend is null ? "?" : Icons.Warning)} {request.DisplayName}{(request.UserName.Length > 0 ? $" ({request.UserName})" : "")}",
                request.LooksLikeFriend is { } imitated
                    ? Loc.T("mainWindow.notYourFriendSameNameDifferent2", imitated, request.Fingerprint)
                    : Loc.T("mainWindow.fingerprint3", request.Fingerprint),
                highlight: false,
                [bidirectional, readOnly, reject]));
        }
    }

    private void RefreshParticipants()
    {
        if (_model.Node is not { } node)
            return;
        ParticipantsPanel.Children.Clear();
        if (node.Session is not { } session)
            return;

        var host = session.Role == SessionRole.Host;
        foreach (var participant in session.Participants)
        {
            var (glyph, role) = AvaloniaOverlayIndicator.Glyph(participant);
            var buttons = new List<Control>();
            var friend = node.Friends.Get(participant.DeviceId);

            if (participant.IsSelf && !host && session.MyGranted == ShareDirection.Bidirectional)
            {
                // Il membro con accesso bidirezionale passa a sola lettura (e torna) con un tasto.
                var readOnly = session.MyDirection == ShareDirection.ReadOnly;
                var toggle = RowButton(readOnly ? Icons.ArrowDown : Icons.Bidirectional, readOnly
                    ? Loc.T("mainWindow.youAreReadOnlyGoBack")
                    : Loc.T("mainWindow.switchToReadOnlyYourCopies"));
                toggle.Click += async (_, _) => await RunFrom(toggle, () => node.SetMyDirectionAsync(readOnly ? ShareDirection.Bidirectional : ShareDirection.ReadOnly));
                buttons.Add(toggle);
            }

            if (!participant.IsSelf)
            {
                if (host)
                {
                    // Un pulsante per membro che alterna bidirezionale e sola lettura.
                    var granted = participant.Granted;
                    var grant = RowButton(granted == ShareDirection.ReadOnly ? Icons.ArrowDown : Icons.Bidirectional,
                        granted == ShareDirection.ReadOnly
                            ? Loc.T("mainWindow.readOnlyClickToGiveTwo")
                            : Loc.T("mainWindow.twoWayClickToMakeRead"));
                    grant.Click += async (_, _) => await RunFrom(grant, () => node.SetMemberGrantAsync(participant.DeviceId,
                        granted == ShareDirection.ReadOnly ? ShareDirection.Bidirectional : ShareDirection.ReadOnly));
                    buttons.Add(grant);
                }

                var trusted = friend?.TrustedWithSensitive == true;
                var trust = RowButton(Icons.Key, trusted
                    ? Loc.T("mainWindow.trustedSeesThePasswordsYouCopy")
                    : Loc.T("mainWindow.clickToAlsoShowThemThe"));
                trust.Classes.Add("toggle");
                trust.Classes.Set("lit", trusted);
                trust.Click += (_, _) => node.SetSensitiveTrust(participant.DeviceId, participant.DisplayName, !trusted);
                buttons.Add(trust);

                if (host)
                {
                    var agreement = friend?.Agreement ?? AgreementLevel.None;
                    var pact = RowButton(agreement switch { AgreementLevel.Anytime => Icons.Infinity, AgreementLevel.AutoJoin => Icons.SignIn, _ => Loc.T("mainWindow.pact") },
                        Loc.T("mainWindow.proposeAMutualPactTheyMust"));
                    pact.Classes.Add("toggle");
                    pact.Classes.Set("lit", agreement != AgreementLevel.None);
                    pact.Flyout = PactMenu(node, participant.DeviceId, agreement);
                    buttons.Add(pact);
                }
            }

            var detail = participant.IsSelf ? Loc.T("common.you", role) : role;
            if (participant.IsHost && !participant.IsSelf && friend?.TrustedWithSensitive != true)
                detail += Loc.T("mainWindow.yourPasswordsDonTReachThem");
            ParticipantsPanel.Children.Add(Row($"{glyph} {participant.DisplayName}", detail, participant.IsSelf, buttons));
        }
    }

    private MenuFlyout PactMenu(CusNode node, string deviceId, AgreementLevel current)
    {
        var flyout = new MenuFlyout();
        void Add(string header, AgreementLevel level)
        {
            var item = new MenuItem { IsEnabled = level != current };
            Skin.SetHeader(item, header);
            item.Click += async (_, _) => await Run(async () =>
            {
                if (!await node.ProposeAgreementAsync(deviceId, level))
                    _model.Notifier?.Info(Loc.T("mainWindow.pactRejected"));
            });
            flyout.Items.Add(item);
        }

        Add(Loc.T("mainWindow.joinsWithoutConfirmationWhenYouOffer", Icons.SignIn), AgreementLevel.AutoJoin);
        Add(Loc.T("mainWindow.connectionAtAnyTime", Icons.Infinity), AgreementLevel.Anytime);
        if (current != AgreementLevel.None)
        {
            var revoke = new MenuItem();
            Skin.SetHeader(revoke, Loc.T("mainWindow.dissolveThePact", Icons.Xmark));
            revoke.Click += async (_, _) => await Run(() => node.RevokeAgreementAsync(deviceId));
            flyout.Items.Add(revoke);
        }

        return flyout;
    }

    private void RefreshTransfersButton()
    {
        if (_model.Node is not { } node)
            return;
        var transfers = node.Transfers;
        var active = transfers.Count(t => t.State == TransferState.Active);
        TransfersButton.IsVisible = transfers.Count > 0;
        Skin.SetContent(TransfersButton, active > 0
            ? Loc.T("mainWindow.transfers", Icons.Transfers, active)
            : Loc.T("mainWindow.transfers2", Icons.Transfers));
    }

    private void RefreshCurrent()
    {
        var current = _model.Node?.CurrentClipboard;
        if (current is null)
        {
            LcdCurrent.Text = "";
            LcdCurrentSize.Text = "";
            _currentFull = "";
            return;
        }

        var self = current.OriginDeviceId == _model.Node?.Identity.DeviceId;
        var title = current.Kind == ClipboardContentKind.Files && !current.IsSensitive
            ? DisplayText.MiddleEllipsis(DisplayText.Sanitize(current.Title, 260, ""), 26)
            : current.Title;
        string Line(string shownTitle) =>
            $"{(self ? Loc.T("mainWindow.you") : DisplayText.Sanitize(current.OriginName, 32))}: {(current.IsSensitive ? "••• " : "")}{shownTitle}{(current.IsShared ? "" : Loc.T("mainWindow.hereOnly"))}";
        LcdCurrent.Text = Line(title);
        _currentFull = current.IsSensitive ? LcdCurrent.Text : Line(DisplayText.Sanitize(current.Title, 4000, ""));
        LcdCurrentSize.Text = current.SizeBytes is { } size ? DisplayText.FormatSize(size) : "";
    }

    // ================================================================ amici

    private void RefreshFriends()
    {
        if (_model.Node is not { } node)
            return;
        FriendsPanel.Children.Clear();
        var friends = node.Friends.All;
        if (friends.Count == 0)
        {
            FriendsPanel.Children.Add(Hint(Loc.T("mainWindow.noFriendsInASessionThe", Icons.Key)));
            return;
        }

        foreach (var friend in friends)
        {
            var buttons = new List<Control>();
            var trust = RowButton(Icons.Key, friend.TrustedWithSensitive
                ? Loc.T("mainWindow.seesYourPasswordsClickToRemove")
                : Loc.T("mainWindow.clickToShowThemYourPasswords"));
            trust.Classes.Add("toggle");
                trust.Classes.Set("lit", friend.TrustedWithSensitive);
            trust.Click += (_, _) => node.SetSensitiveTrust(friend.DeviceId, friend.DisplayName, !friend.TrustedWithSensitive);
            buttons.Add(trust);

            if (friend.Agreement != AgreementLevel.None)
            {
                var revoke = RowButton(Loc.T("mainWindow.dissolve"), Loc.T("mainWindow.dissolvesThePactForBoth"));
                revoke.Click += async (_, _) => await RunFrom(revoke, async () =>
                {
                    if (await ConfirmDialog.ShowAsync(this, Loc.T("mainWindow.dissolveThePact2"),
                            Loc.T("mainWindow.dissolveThePactWithItEnds", friend.DisplayName),
                            Loc.T("mainWindow.dissolve")))
                        await node.RevokeAgreementAsync(friend.DeviceId);
                });
                buttons.Add(revoke);
            }

            var remove = RowButton(Icons.Xmark, Loc.T("mainWindow.removeFromFriendsPactAndTrust"));
            remove.Click += async (_, _) => await RunFrom(remove, () => node.RemoveFriendAsync(friend.DeviceId));
            buttons.Add(remove);

            var agreement = friend.Agreement switch
            {
                AgreementLevel.Anytime => Loc.T("mainWindow.connectsWheneverTheyWant", Icons.Infinity),
                AgreementLevel.AutoJoin => Loc.T("mainWindow.joinsWithoutConfirmation", Icons.SignIn),
                _ => Loc.T("mainWindow.noPact"),
            };
            var where = friend.LastEndpoint is { } endpoint ? $" · {endpoint}" : "";
            FriendsPanel.Children.Add(Row(friend.DisplayName, $"{agreement}{(friend.TrustedWithSensitive ? Loc.T("mainWindow.seesPasswords", Icons.Key) : "")}{where}", false, buttons,
                tip: Loc.T("mainWindow.fingerprint4", friend.Fingerprint)));
        }
    }

    // ================================================================ cronologia

    private void WireHistoryPage(CusNode node)
    {
        RefreshHistoryButton.Click += async (_, _) => await RefreshHistoryAsync();
        UseHistoryButton.Click += async (_, _) =>
        {
            if (HistoryList.SelectedIndex is not (var i and >= 0) || i >= _history.Count)
            {
                _model.Notifier?.Info(Loc.T("mainWindow.pickAHistoryEntryThenPress"));
                return;
            }

            // La voce si fissa prima dell'attesa: nel frattempo la lista può essere ricaricata.
            var entry = _history[i];
            await RunFrom(UseHistoryButton, async () =>
            {
                await node.UseHistoryEntryAsync(entry);
                AddActivity(Loc.T("mainWindow.copiedFromHistory", Describe(entry)));
            });
        };
        node.CurrentClipboardChanged += (_, _) => Post(() =>
        {
            if (HistoryPage.IsVisible)
                _ = RefreshHistoryAsync();
        });
    }

    private async Task RefreshHistoryAsync()
    {
        if (_model.Node is not { } node)
            return;
        HistoryTitle.Text = node.Session?.Role == SessionRole.Member
            ? Loc.T("mainWindow.copiesInThisSessionFromThe")
            : Loc.T("mainWindow.lastCopiesHostedHere");
        await Run(async () =>
        {
            var entries = await node.GetHistoryAsync();
            _history.Clear();
            _history.AddRange(entries);
            // Le voci (nomi e testi) possono arrivare da altri PC: niente a capo né caratteri bidi.
            HistoryList.ItemsSource = _history.Select(h =>
                $"{h.CreatedUtc.ToLocalTime():HH:mm} {DisplayText.Sanitize(h.OriginName, 24)}: {Describe(h)}").ToList();
        });
    }

    // ================================================================ impostazioni

    private void WireSettingsPage(CusNode node)
    {
        void Update()
        {
            var on = node.KeepRunningInBackground;
            BackgroundToggle.Content = on ? Loc.T("mainWindow.yes") : "NO";
            BackgroundToggle.Classes.Set("active", on);
            LanguageItalianButton.Classes.Set("active", Loc.Current == AppLanguage.Italian);
            LanguageEnglishButton.Classes.Set("active", Loc.Current == AppLanguage.English);
        }

        void SetLanguage(AppLanguage language)
        {
            // Salvata nel profilo: da qui in poi vale questa, non più quella dell'installer.
            node.SavedLanguage = language;
            Loc.Current = language;
        }

        LanguageItalianButton.Click += (_, _) => SetLanguage(AppLanguage.Italian);
        LanguageEnglishButton.Click += (_, _) => SetLanguage(AppLanguage.English);
        LanguageChanged += Update;

        BackgroundToggle.Click += (_, _) =>
        {
            node.KeepRunningInBackground = !node.KeepRunningInBackground;
            Update();
        };
        BackgroundToggle.IsEnabled = _model.HasTrayIcon;
        Update();

        async Task SaveName()
        {
            var applied = await node.SetDisplayNameAsync(NameInput.Text);
            // Vuoto: si torna al nome attuale; altrimenti si mostra quello ripulito.
            NameInput.Text = applied ?? node.Identity.DisplayName;
        }

        RenameButton.Click += async (_, _) => await RunFrom(RenameButton, SaveName);
        NameInput.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
                await RunFrom(RenameButton, SaveName);
        };
    }

    // ================================================================ simulazione

    private void WireSimulationPage(ISimulationControls? simulation)
    {
        if (simulation is null)
            return;

        PageSimButton.IsVisible = true;
        SimCurrentText.Text = simulation.DescribeCurrent();
        simulation.ContentChanged += () => Post(() => SimCurrentText.Text = simulation.DescribeCurrent());
        SimCopyTextButton.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(SimTextInput.Text))
                simulation.CopyText(SimTextInput.Text);
        };
        SimCopySensitiveButton.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(SimTextInput.Text))
                simulation.CopySensitiveText(SimTextInput.Text);
        };
        SimCopyFilesButton.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true, Title = Loc.T("mainWindow.filesToCopy") });
            var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            if (paths.Count > 0)
                simulation.CopyPaths(paths);
        };
        SimCopyFolderButton.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Loc.T("mainWindow.folderToCopy") });
            var paths = folders.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            if (paths.Count > 0)
                simulation.CopyPaths(paths);
        };
        SimPasteFilesButton.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Loc.T("mainWindow.whereToPasteTheFiles") });
            if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } folder)
                return;
            await Run(async () =>
            {
                try
                {
                    var written = await simulation.PasteFilesAsync(folder);
                    AddActivity(Loc.T("mainWindow.pastedFilesInto", written.Count, folder));
                }
                catch (RemoteFileUnavailableException ex)
                {
                    AddActivity(Loc.T("mainWindow.cannotPaste", ex.Message));
                }
            });
        };
    }

    // ================================================================ testi troncati

    /// <summary>
    /// I testi della console finiscono con i puntini quando non ci stanno: un clic (doppio clic nella
    /// cronologia, dove il clic singolo sceglie la voce da copiare) apre il messaggio completo.
    /// </summary>
    private void WireExpandableTexts()
    {
        MessageDialog.ExpandOnClick(LcdDevice, () => Loc.T("mainWindow.thisPc"));
        MessageDialog.ExpandOnClick(LcdParticipants, () => Loc.T("mainWindow.session2"));
        MessageDialog.ExpandOnClick(LcdCurrent, () => Loc.T("mainWindow.currentClipboard"), () => _currentFull);
        MessageDialog.ExpandOnClick(LcdLast, () => Loc.T("mainWindow.lastActivity"), () => _lastActivityFull);

        ActivityList.Tapped += async (_, e) =>
        {
            if (ItemText(e.Source) is { } line)
                await MessageDialog.ShowAsync(this, Loc.T("mainWindow.activity"), line);
        };
        HistoryList.DoubleTapped += async (_, e) =>
        {
            if (ItemText(e.Source) is null || HistoryList.SelectedIndex is not (var i and >= 0) || i >= _history.Count)
                return;
            var entry = _history[i];
            var text = $"{entry.CreatedUtc.ToLocalTime():dd/MM HH:mm} · {DisplayText.Sanitize(entry.OriginName, 64)}\n\n"
                + DisplayText.Sanitize(entry.Describe(), 4000, Loc.T("common.empty"));
            await MessageDialog.ShowAsync(this, Loc.T("mainWindow.history"), text);
        };
    }

    /// <summary>Testo della riga di elenco su cui si è cliccato (null se il clic non è su una riga).</summary>
    private static string? ItemText(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as string;

    // ================================================================ righe degli elenchi

    /// <summary>Riga di un elenco: titolo e dettaglio a sinistra, pulsanti a destra; questo PC evidenziato.</summary>
    private static Control Row(string title, string detail, bool highlight, IReadOnlyList<Control> buttons, string? tip = null)
    {
        var titleText = new TextBlock
        {
            FontFamily = Skin.LcdFont,
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Foreground = highlight ? Skin.InkBrush : Skin.LimeTextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Skin.SetRich(titleText, title);
        MessageDialog.ExpandOnClick(titleText, () => Loc.T("mainWindow.detail"));
        var detailText = new TextBlock
        {
            FontFamily = Skin.LcdFont,
            FontSize = 10.5,
            Foreground = highlight ? Skin.InkBrush : new SolidColorBrush(Color.Parse("#7FB83A")),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        Skin.SetRich(detailText, detail);
        MessageDialog.ExpandOnClick(detailText, () => Loc.T("mainWindow.detail"));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        foreach (var button in buttons)
            actions.Children.Add(button);
        DockPanel.SetDock(actions, Dock.Right);

        var row = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 4),
            Background = highlight ? Skin.PillBrush : new SolidColorBrush(Color.Parse("#1C2E10")),
            BorderBrush = highlight ? Skin.LimeTextBrush : new SolidColorBrush(Color.Parse("#2F4A14")),
            BorderThickness = new Thickness(1),
            Child = new DockPanel
            {
                Children =
                {
                    actions,
                    new StackPanel { Spacing = 1, Margin = new Thickness(0, 0, 6, 0), Children = { titleText, detailText } },
                },
            },
        };
        if (tip is not null)
            ToolTip.SetTip(row, tip);
        return row;
    }

    private static Button RowButton(string text, string tip)
    {
        var button = Skin.Pill(text);
        button.MinWidth = 30;
        ToolTip.SetTip(button, tip);
        return button;
    }

    private static TextBlock Hint(string text)
    {
        var block = new TextBlock
        {
            FontFamily = Skin.LcdFont,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#7FB83A")),
            TextWrapping = TextWrapping.Wrap,
        };
        Skin.SetRich(block, text);
        return block;
    }

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
            _model.Notifier?.Error(Loc.T("mainWindow.unexpectedError", ex.Message));
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
