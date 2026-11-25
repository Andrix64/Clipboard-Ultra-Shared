using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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

    /// <summary>Firma dell'ultimo elenco dei peer disegnato: si ridisegna solo se cambia.</summary>
    private string _peersSignature = "";

    /// <summary>Arresto del nodo avviato (un secondo clic su chiudi non lo ripete).</summary>
    private bool _shutdownStarted;

    /// <summary>Arresto concluso: la prossima chiusura è quella vera.</summary>
    private bool _closeAllowed;

    /// <summary>Uscita chiesta dall'area di notifica (o dal sistema): niente riduzione in background.</summary>
    private bool _exitRequested;

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
        InfoText.Text = string.Join(Environment.NewLine, model.InfoLines);
        WireChrome();

        if (model.StartupError is { } error)
        {
            LcdState.Text = "ERRORE";
            LcdParticipants.Text = "Avvio non riuscito";
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

        LcdDevice.Text = $"▸ {node.Identity.DisplayName}";
        LcdPort.Text = $"PORTA {node.Port}";
        IdentityText.Text = $"{node.Identity.DisplayName} ({node.Identity.UserName})\nIMPRONTA {node.Identity.Info.Fingerprint}";

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
        LcdState.Text = "CHIUSURA...";
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
        PageFriendsButton.Click += (_, _) => ShowPage(_rightPages, FriendsPage);
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
            _model.Notifier?.Info($"{result.PeerName} non ti ha fatto entrare" + (result.Reason is null ? "." : $": {result.Reason}."));
    }

    private void RefreshPeers()
    {
        if (_model.Node is not { } node)
            return;
        var peers = node.Peers;
        var signature = string.Join("\n", peers.Select(p => $"{p.DeviceId}|{p.DisplayName}|{p.Address}|{p.Participants}|{p.IsAnnounced}|{p.Agreement}|{p.IsManual}"));
        if (signature == _peersSignature)
            return;
        _peersSignature = signature;

        PeersPanel.Children.Clear();
        if (peers.Count == 0)
        {
            PeersPanel.Children.Add(Hint("Nessun host in rete. Chi vuole condividere deve premere OFFRITI COME HOST; oppure inserisci un indirizzo qui sotto."));
            return;
        }

        foreach (var peer in peers)
        {
            var who = peer.IsManual ? peer.Address : $"{peer.DisplayName}{(peer.UserName.Length > 0 ? $" ({peer.UserName})" : "")}";
            var detail = peer.IsAnnounced
                ? (peer.Participants <= 1 ? "libero, si offre come host" : $"{peer.Participants} persone in sessione")
                : peer.IsManual ? "indirizzo salvato" : "amico, non visibile ora";
            var badges = (peer.Agreement switch { AgreementLevel.Anytime => "∞ ", AgreementLevel.AutoJoin => "⤵ ", _ => "" })
                + (peer.DeviceId is { } id && node.Friends.TrustsWithSensitive(id) ? "••• " : "");

            var buttons = new List<Control>();
            if (peer.CanJoin)
            {
                var join = RowButton("ENTRA", peer.Agreement == AgreementLevel.None ? "Chiedi di entrare: l'host decide" : "Entra senza conferma (patto)");
                join.Click += async (_, _) => await RunFrom(join, () => Join(node, peer.Host, peer.Port, peer.DeviceId));
                buttons.Add(join);
            }

            if (peer.IsManual)
            {
                var remove = RowButton("✕", "Togli dall'elenco");
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
            LcdState.Text = "IN ATTESA";
            LcdParticipants.Text = "Offriti come host o entra (pannello RETE)";
        }
        else
        {
            LcdState.Text = session.Role == SessionRole.Host
                ? (session.IsSharing ? "CONDIVISA · HOST" : "VISIBILE")
                : session.MyDirection == ShareDirection.ReadOnly ? "SOLA LETTURA" : "CONDIVISA";
            LcdParticipants.Text = session.IsSharing
                ? string.Join("  ", session.Participants.Select(p => $"{AvaloniaOverlayIndicator.Glyph(p).Glyph} {p.DisplayName}{(p.IsSelf ? "◂" : "")}"))
                : session.IsVisible ? "In attesa che qualcuno chieda di entrare..." : "Nessun altro nella sessione";
        }

        var host = session?.Role == SessionRole.Host;
        PageNetworkButton.Content = session is null ? "RETE" : "SESSIONE";
        VisibleToggle.IsVisible = host;
        VisibleToggle.Classes.Set("active", session?.IsVisible == true);
        VisibleToggle.Content = session?.IsVisible == true ? "◉ VISIBILE" : "○ NASCOSTO";
        SensitiveToggle.IsVisible = host && session!.IsSharing;
        SensitiveToggle.Classes.Set("active", session?.SensitiveForAll == true);
        LeaveButton.Content = host ? "■ CHIUDI SESSIONE" : "■ ESCI";
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
            RequestsPanel.Children.Add(Hint(node.IsVisible ? "Nessuna richiesta in attesa." : "Sei nascosto: nessuno può chiedere di entrare."));
            return;
        }

        foreach (var request in requests)
        {
            var bidirectional = RowButton("⇄", "Fai entrare con accesso bidirezionale");
            var readOnly = RowButton("↓", "Fai entrare in sola lettura");
            var reject = RowButton("✕", "Rifiuta");
            bidirectional.Click += (_, _) => node.AnswerJoinRequest(request.RequestId, ConsentDecision.Accept);
            readOnly.Click += (_, _) => node.AnswerJoinRequest(request.RequestId, ConsentDecision.AcceptReadOnly);
            reject.Click += (_, _) => node.AnswerJoinRequest(request.RequestId, ConsentDecision.Reject);
            RequestsPanel.Children.Add(Row(
                $"? {request.DisplayName}{(request.UserName.Length > 0 ? $" ({request.UserName})" : "")}",
                $"impronta {request.Fingerprint}",
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
                var toggle = RowButton(readOnly ? "↓" : "⇄", readOnly ? "Sei in sola lettura: torna bidirezionale" : "Passa in sola lettura: le tue copie restano qui");
                toggle.Click += async (_, _) => await RunFrom(toggle, () => node.SetMyDirectionAsync(readOnly ? ShareDirection.Bidirectional : ShareDirection.ReadOnly));
                buttons.Add(toggle);
            }

            if (!participant.IsSelf)
            {
                if (host)
                {
                    // Un pulsante per membro che alterna bidirezionale e sola lettura.
                    var granted = participant.Granted;
                    var grant = RowButton(granted == ShareDirection.ReadOnly ? "↓" : "⇄",
                        granted == ShareDirection.ReadOnly ? "In sola lettura: clic per dargli accesso bidirezionale" : "Bidirezionale: clic per metterlo in sola lettura");
                    grant.Click += async (_, _) => await RunFrom(grant, () => node.SetMemberGrantAsync(participant.DeviceId,
                        granted == ShareDirection.ReadOnly ? ShareDirection.Bidirectional : ShareDirection.ReadOnly));
                    buttons.Add(grant);
                }

                var trusted = friend?.TrustedWithSensitive == true;
                var trust = RowButton("•••", trusted ? "Affidabile: vede le password che copi. Clic per togliere" : "Clic per mostrargli anche le password che copi (affidabile)");
                trust.Classes.Add("toggle");
                trust.Classes.Set("lit", trusted);
                trust.Click += (_, _) => node.SetSensitiveTrust(participant.DeviceId, participant.DisplayName, !trusted);
                buttons.Add(trust);

                if (host)
                {
                    var agreement = friend?.Agreement ?? AgreementLevel.None;
                    var pact = RowButton(agreement switch { AgreementLevel.Anytime => "∞", AgreementLevel.AutoJoin => "⤵", _ => "PATTO" },
                        "Proponi un patto reciproco (deve accettarlo)");
                    pact.Classes.Add("toggle");
                    pact.Classes.Set("lit", agreement != AgreementLevel.None);
                    pact.Flyout = PactMenu(node, participant.DeviceId, agreement);
                    buttons.Add(pact);
                }
            }

            var detail = participant.IsSelf ? $"{role} · sei tu" : role;
            if (participant.IsHost && !participant.IsSelf && friend?.TrustedWithSensitive != true)
                detail += " · le tue password non gli arrivano";
            ParticipantsPanel.Children.Add(Row($"{glyph} {participant.DisplayName}", detail, participant.IsSelf, buttons));
        }
    }

    private MenuFlyout PactMenu(CusNode node, string deviceId, AgreementLevel current)
    {
        var flyout = new MenuFlyout();
        void Add(string header, AgreementLevel level)
        {
            var item = new MenuItem { Header = header, IsEnabled = level != current };
            item.Click += async (_, _) => await Run(async () =>
            {
                if (!await node.ProposeAgreementAsync(deviceId, level))
                    _model.Notifier?.Info("Patto rifiutato.");
            });
            flyout.Items.Add(item);
        }

        Add("⤵ Entra senza conferma quando ti offri", AgreementLevel.AutoJoin);
        Add("∞ Collegamento in qualsiasi momento", AgreementLevel.Anytime);
        if (current != AgreementLevel.None)
        {
            var revoke = new MenuItem { Header = "✕ Sciogli il patto" };
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
        TransfersButton.Content = active > 0 ? $"⇅ TRASFERIMENTI ({active})" : "⇅ TRASFERIMENTI";
    }

    private void RefreshCurrent()
    {
        var current = _model.Node?.CurrentClipboard;
        if (current is null)
        {
            LcdCurrent.Text = "";
            LcdCurrentSize.Text = "";
            return;
        }

        var self = current.OriginDeviceId == _model.Node?.Identity.DeviceId;
        var title = current.Kind == ClipboardContentKind.Files && !current.IsSensitive
            ? DisplayText.MiddleEllipsis(DisplayText.Sanitize(current.Title, 260, ""), 26)
            : current.Title;
        LcdCurrent.Text = $"{(self ? "tu" : DisplayText.Sanitize(current.OriginName, 32))}: {(current.IsSensitive ? "••• " : "")}{title}{(current.IsShared ? "" : " · solo qui")}";
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
            FriendsPanel.Children.Add(Hint("Nessun amico. In una sessione l'host può proporre un patto ai connessi; chiunque può marcare un partecipante come affidabile (•••)."));
            return;
        }

        foreach (var friend in friends)
        {
            var buttons = new List<Control>();
            var trust = RowButton("•••", friend.TrustedWithSensitive ? "Vede le tue password: clic per togliere" : "Clic per mostrargli le tue password");
            trust.Classes.Add("toggle");
                trust.Classes.Set("lit", friend.TrustedWithSensitive);
            trust.Click += (_, _) => node.SetSensitiveTrust(friend.DeviceId, friend.DisplayName, !friend.TrustedWithSensitive);
            buttons.Add(trust);

            if (friend.Agreement != AgreementLevel.None)
            {
                var revoke = RowButton("SCIOGLI", "Scioglie il patto per entrambi");
                revoke.Click += async (_, _) => await RunFrom(revoke, async () =>
                {
                    if (await ConfirmDialog.ShowAsync(this, "Sciogli il patto",
                            $"Sciogliere il patto con {friend.DisplayName}? Cade anche dal suo lato: per rifarlo servirà una nuova proposta in sessione.",
                            "SCIOGLI"))
                        await node.RevokeAgreementAsync(friend.DeviceId);
                });
                buttons.Add(revoke);
            }

            var remove = RowButton("✕", "Togli dagli amici (patto e fiducia)");
            remove.Click += async (_, _) => await RunFrom(remove, () => node.RemoveFriendAsync(friend.DeviceId));
            buttons.Add(remove);

            var agreement = friend.Agreement switch
            {
                AgreementLevel.Anytime => "∞ si collega quando vuole",
                AgreementLevel.AutoJoin => "⤵ entra senza conferma",
                _ => "nessun patto",
            };
            var where = friend.LastEndpoint is { } endpoint ? $" · {endpoint}" : "";
            FriendsPanel.Children.Add(Row(friend.DisplayName, $"{agreement}{(friend.TrustedWithSensitive ? " · ••• vede le password" : "")}{where}", false, buttons,
                tip: $"Impronta {friend.Fingerprint}"));
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
                _model.Notifier?.Info("Scegli una voce della cronologia, poi premi COPIA.");
                return;
            }

            // La voce si fissa prima dell'attesa: nel frattempo la lista può essere ricaricata.
            var entry = _history[i];
            await RunFrom(UseHistoryButton, async () =>
            {
                await node.UseHistoryEntryAsync(entry);
                AddActivity($"Copiato dalla cronologia: {Describe(entry)}");
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
        HistoryTitle.Text = node.Session?.Role == SessionRole.Member ? "COPIE DI QUESTA SESSIONE (DALL'HOST)" : "ULTIME 20 COPIE OSPITATE QUI";
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
            BackgroundToggle.Content = on ? "SÌ" : "NO";
            BackgroundToggle.Classes.Set("active", on);
        }

        BackgroundToggle.Click += (_, _) =>
        {
            node.KeepRunningInBackground = !node.KeepRunningInBackground;
            Update();
        };
        BackgroundToggle.IsEnabled = _model.HasTrayIcon;
        Update();
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
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true, Title = "File da copiare" });
            var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            if (paths.Count > 0)
                simulation.CopyPaths(paths);
        };
        SimCopyFolderButton.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Cartella da copiare" });
            var paths = folders.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            if (paths.Count > 0)
                simulation.CopyPaths(paths);
        };
        SimPasteFilesButton.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Dove incollare i file" });
            if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } folder)
                return;
            await Run(async () =>
            {
                try
                {
                    var written = await simulation.PasteFilesAsync(folder);
                    AddActivity($"Incollati {written.Count} file in {folder}.");
                }
                catch (RemoteFileUnavailableException ex)
                {
                    AddActivity($"! Impossibile incollare: {ex.Message}");
                }
            });
        };
    }

    // ================================================================ righe degli elenchi

    /// <summary>Riga di un elenco: titolo e dettaglio a sinistra, pulsanti a destra; questo PC evidenziato.</summary>
    private static Control Row(string title, string detail, bool highlight, IReadOnlyList<Control> buttons, string? tip = null)
    {
        var titleText = new TextBlock
        {
            Text = title,
            FontFamily = Skin.LcdFont,
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Foreground = highlight ? Skin.InkBrush : Skin.LimeTextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var detailText = new TextBlock
        {
            Text = detail,
            FontFamily = Skin.LcdFont,
            FontSize = 10.5,
            Foreground = highlight ? Skin.InkBrush : new SolidColorBrush(Color.Parse("#7FB83A")),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

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

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        FontFamily = Skin.LcdFont,
        FontSize = 11,
        Foreground = new SolidColorBrush(Color.Parse("#7FB83A")),
        TextWrapping = TextWrapping.Wrap,
    };

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
            _model.Notifier?.Error($"Errore inatteso: {ex.Message}");
        }
    }

    private void AddActivity(string line)
    {
        var items = (ActivityList.ItemsSource as List<string>)?.ToList() ?? [];
        items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {line}");
        if (items.Count > MaxActivityLines)
            items.RemoveRange(MaxActivityLines, items.Count - MaxActivityLines);
        ActivityList.ItemsSource = items;
        LcdLast.Text = "» " + Shorten(line);
    }

    /// <summary>Descrizione di una voce per le liste: testo ripulito (può arrivare da un altro PC) e accorciato.</summary>
    private static string Describe(HistoryEntry entry) => Shorten(DisplayText.Sanitize(entry.Describe(), 200, "(vuota)"));

    private static string Shorten(string text)
    {
        text = text.ReplaceLineEndings(" ");
        return text.Length <= 80 ? text : text[..77] + "...";
    }

    private static void Post(Action action) => Dispatcher.UIThread.Post(action);
}
