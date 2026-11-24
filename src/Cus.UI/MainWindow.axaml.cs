using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Cus.Core.Abstractions;
using Cus.Core.Hosting;
using Cus.Core.Node;
using Cus.Core.Storage;
using Cus.Core.Trust;

namespace Cus.UI;

public partial class MainWindow : Window
{
    private const int MaxActivityLines = 200;

    private readonly MainWindowModel _model;
    private readonly List<PeerView> _peers = [];
    private readonly List<HistoryEntry> _history = [];
    private readonly List<TrustedDevice> _devices = [];
    private readonly List<(Button Button, Control Page)> _pages = [];
    private IDisposable? _codeTimer;

    /// <summary>Arresto del nodo avviato (un secondo clic su chiudi non lo ripete).</summary>
    private bool _shutdownStarted;

    /// <summary>Arresto concluso: la prossima chiusura è quella vera.</summary>
    private bool _closeAllowed;

    /// <summary>Aggiornamento della lista dei peer in corso: la selezione cambia, ma non per mano dell'utente.</summary>
    private bool _refreshingPeers;

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
            Controls.IsVisible = false;
            Side.IsEnabled = false;
            return;
        }

        if (model.Node is not { } node)
            return;

        node.Activity += line => Post(() => AddActivity(line));
        node.SessionChanged += (_, _) => Post(RefreshSession);
        node.PeersChanged += (_, _) => Post(RefreshPeers);
        node.PairingCodeChanged += (_, _) => Post(RefreshCode);
        node.Trust.Changed += (_, _) => Post(() =>
        {
            RefreshDevices();
            RefreshSession();
        });
        if (model.Notifier is { } notifier)
            notifier.Notified += (message, isError) => AddActivity((isError ? "! " : "") + message);

        LcdDevice.Text = $"▸ {node.Identity.DisplayName}";
        LcdPort.Text = $"PORTA {node.Port}";

        WireSessionControls(node);
        WireHistoryPage(node);
        WireDevicesPage(node);
        WireSimulationPage(model.Simulation);

        // La lista dei peer scoperti in rete scade: si aggiorna periodicamente.
        DispatcherTimer.Run(() =>
        {
            RefreshPeers();
            return true;
        }, TimeSpan.FromSeconds(3));

        RefreshSession();
        RefreshPeers();
        RefreshDevices();
        RefreshCode();
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

        _pages.AddRange(
        [
            (PagePeersButton, PeersPage),
            (PageHistoryButton, HistoryPage),
            (PageDevicesButton, DevicesPage),
            (PageSimButton, SimPage),
            (PageActivityButton, ActivityList),
            (PageInfoButton, InfoPage),
        ]);
        foreach (var (button, page) in _pages)
            button.Click += (_, _) => ShowPage(page);
        ShowPage(PeersPage);
    }

    private void ShowPage(Control page)
    {
        foreach (var (button, candidate) in _pages)
        {
            candidate.IsVisible = candidate == page;
            button.Classes.Set("active", candidate == page);
        }

        if (page == HistoryPage)
            _ = RefreshHistoryAsync();
    }

    // ================================================================ sessione

    private void WireSessionControls(CusNode node)
    {
        LeaveButton.Click += async (_, _) => await Run(node.LeaveSessionAsync);
        InviteButton.Click += async (_, _) =>
        {
            if (PeersList.SelectedIndex is var i && i >= 0 && i < _peers.Count)
            {
                // Peer scoperto: l'identità che risponde deve essere quella annunciata (null per i
                // peer aggiunti a mano, che non hanno un'identità nota).
                var peer = _peers[i];
                await Invite(node, peer.Host, peer.Port, peer.DeviceId);
            }
            else
            {
                ShowPage(PeersPage);
                _model.Notifier?.Info("Scegli un dispositivo nell'elenco PEER, poi premi INVITA.");
            }
        };
        InviteAddressButton.Click += async (_, _) =>
        {
            if (ParseAddress(AddressInput.Text) is { } endpoint)
                await Invite(node, endpoint.Host, endpoint.Port, expectedDeviceId: null);
        };
        AddPeerButton.Click += (_, _) =>
        {
            if (ParseAddress(AddressInput.Text) is { } endpoint)
            {
                node.AddManualPeer(endpoint.ToString());
                AddressInput.Text = "";
            }
        };
        RemovePeerButton.Click += (_, _) =>
        {
            if (PeersList.SelectedIndex is var i && i >= 0 && i < _peers.Count && _peers[i].IsManual)
                node.RemoveManualPeer(_peers[i].Address);
        };
        PeersList.SelectionChanged += (_, _) =>
        {
            // Solo una scelta dell'utente: l'aggiornamento periodico della lista non deve
            // sovrascrivere l'indirizzo che l'utente sta scrivendo.
            if (!_refreshingPeers && PeersList.SelectedIndex is var i && i >= 0 && i < _peers.Count)
                PairAddressInput.Text = _peers[i].Address;
        };
    }

    private async Task Invite(CusNode node, string host, int port, string? expectedDeviceId)
    {
        // Un solo invito alla volta: un doppio clic non deve mandarne due allo stesso PC.
        if (!InviteButton.IsEnabled)
            return;
        InviteButton.IsEnabled = InviteAddressButton.IsEnabled = false;
        try
        {
            AddActivity($"Richiesta inviata a {new PeerEndpoint(host, port)}: in attesa della risposta...");
            await Run(async () =>
            {
                var result = await node.InviteAsync(host, port, expectedDeviceId);
                if (result.Outcome == InviteOutcome.Rejected)
                    AddActivity($"{result.PeerName} ha rifiutato" + (result.Reason is null ? "." : $": {result.Reason}."));
            });
        }
        finally
        {
            InviteButton.IsEnabled = InviteAddressButton.IsEnabled = true;
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

    private void RefreshSession()
    {
        if (_model.Node is not { } node)
            return;

        var session = node.Session;
        if (node.Trust.State.SelfRevoked)
        {
            LcdState.Text = "REVOCATO";
            LcdParticipants.Text = "Serve una nuova identità (pagina DISPOSITIVI)";
        }
        else if (session is null)
        {
            LcdState.Text = "IN ATTESA";
            LcdParticipants.Text = "Nessuna sessione · scegli un peer e premi INVITA";
        }
        else
        {
            LcdState.Text = session.Role == SessionRole.Host ? "CONDIVISA · HOST" : "CONDIVISA";
            LcdParticipants.Text = string.Join(" · ", session.Participants.Select(p =>
                p.DisplayName + (p.IsHost ? " ★" : "") + (p.Direction == ShareDirection.ReadOnly ? " (sola lettura)" : "")));
        }

        LeaveButton.IsEnabled = session is not null;
        LeaveLabel.Text = session?.Role == SessionRole.Host ? "CHIUDI" : "ESCI";
    }

    private void RefreshPeers()
    {
        if (_model.Node is not { } node)
            return;
        var selected = PeersList.SelectedIndex >= 0 && PeersList.SelectedIndex < _peers.Count ? _peers[PeersList.SelectedIndex] : null;
        var peers = node.Peers;
        var lines = peers.Select(p => p.IsManual
            ? $"{p.Address} · a mano"
            : $"{p.DisplayName}{(p.UserName.Length > 0 ? $" ({p.UserName})" : "")} — {p.Address}" + (p.IsTrusted ? " · tuo PC" : "")).ToList();

        // Lista invariata (il caso normale ogni 3 secondi): non si tocca, così selezione, scorrimento
        // e focus restano dove sono.
        if (PeersList.ItemsSource is List<string> current && current.SequenceEqual(lines) && _peers.SequenceEqual(peers))
            return;

        _refreshingPeers = true;
        try
        {
            _peers.Clear();
            _peers.AddRange(peers);
            PeersList.ItemsSource = lines;
            PeersList.SelectedIndex = selected is null ? -1 : _peers.FindIndex(p => p.Address == selected.Address && p.DeviceId == selected.DeviceId);
        }
        finally
        {
            _refreshingPeers = false;
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
    }

    private async Task RefreshHistoryAsync()
    {
        if (_model.Node is not { } node)
            return;
        await Run(async () =>
        {
            var entries = await node.GetHistoryAsync();
            _history.Clear();
            _history.AddRange(entries);
            // Le voci (nomi e testi) possono arrivare da altri PC: niente a capo né caratteri bidi.
            HistoryList.ItemsSource = _history.Select(h =>
                $"#{h.Sequence,-3} {h.CreatedUtc.ToLocalTime():HH:mm:ss} {DisplayText.Sanitize(h.OriginName, 64)}: {Describe(h)}").ToList();
        });
    }

    // ================================================================ dispositivi

    private void WireDevicesPage(CusNode node)
    {
        IdentityText.Text = $"QUESTO PC: {node.Identity.DisplayName} ({node.Identity.UserName})\nIMPRONTA: {node.Identity.Info.Fingerprint}";

        ShowCodeButton.Click += (_, _) =>
        {
            if (node.ActivePairingCode is not null)
            {
                node.CancelPairing();
                return;
            }

            _ = Run(() =>
            {
                node.StartPairing();
                return Task.CompletedTask;
            });
        };
        PairButton.Click += async (_, _) =>
        {
            if (ParseAddress(PairAddressInput.Text) is not { } endpoint)
                return;
            var code = PairCodeInput.Text ?? "";
            await RunFrom(PairButton, async () =>
            {
                try
                {
                    var device = await node.PairWithAsync(endpoint.Host, endpoint.Port, code);
                    PairCodeInput.Text = "";
                    _model.Notifier?.Info($"Abbinato a {device.DisplayName}.");
                }
                catch (PairingFailedException ex)
                {
                    _model.Notifier?.Error(ex.Message);
                }
            });
        };
        RevokeButton.Click += async (_, _) =>
        {
            if (DevicesList.SelectedIndex is not (var i and >= 0) || i >= _devices.Count)
                return;
            var device = _devices[i];
            await RunFrom(RevokeButton, async () =>
            {
                if (await ConfirmDialog.ShowAsync(this, "Revoca dispositivo",
                        $"Revocare {device.DisplayName}? Non potrà più condividere la clipboard con i tuoi PC "
                        + "e la revoca non si può annullare: per riaverlo servirà un nuovo abbinamento con una nuova identità.",
                        "REVOCA"))
                    await node.RevokeAsync(device.DeviceId);
            });
        };
        RevokeSelfButton.Click += async (_, _) => await RunFrom(RevokeSelfButton, async () =>
        {
            if (await ConfirmDialog.ShowAsync(this, "Revoca questo PC",
                    "Revocare questo PC? Gli altri tuoi PC smetteranno di fidarsi di lui e la revoca non si può annullare: "
                    + "per tornare a usare CUS qui servirà una nuova identità.",
                    "REVOCA QUESTO PC"))
                await node.RevokeAsync(node.Identity.DeviceId);
        });
        ResetIdentityButton.Click += async (_, _) =>
        {
            if (_model.ResetIdentityAndRestart is not { } reset)
                return;
            await Run(async () =>
            {
                if (!await ConfirmDialog.ShowAsync(this, "Nuova identità",
                        "Generare una nuova identità e riavviare CUS? L'identità attuale viene cancellata e non si può recuperare: "
                        + "questo PC andrà abbinato di nuovo agli altri.",
                        "NUOVA IDENTITÀ"))
                    return;
                ResetIdentityButton.IsEnabled = false;
                try
                {
                    await reset();
                }
                finally
                {
                    ResetIdentityButton.IsEnabled = true;
                }
            });
        };
        DevicesList.SelectionChanged += (_, _) =>
        {
            if (DevicesList.SelectedIndex is var i && i >= 0 && i < _devices.Count)
            {
                var pairedBy = node.Trust.PairedBy(_devices[i].DeviceId);
                PairedByText.Text = pairedBy.Count == 0
                    ? ""
                    : $"{_devices[i].DisplayName} ha abbinato: {string.Join(", ", pairedBy.Select(d => d.DisplayName))}. Se lo revochi, valuta se revocare anche questi.";
            }
        };
    }

    private void RefreshDevices()
    {
        if (_model.Node is not { } node)
            return;
        var state = node.Trust.State;
        RevokedPanel.IsVisible = state.SelfRevoked;
        _devices.Clear();
        _devices.AddRange(state.Trusted.Values.OrderBy(d => d.DisplayName, StringComparer.CurrentCultureIgnoreCase));
        var revoked = node.Trust.Entries
            .Where(e => e.Kind == TrustEntryKind.Pair && state.IsRevoked(e.SubjectId) && e.SubjectId != node.Identity.DeviceId)
            .GroupBy(e => e.SubjectId)
            .Select(g => $"{DisplayText.Sanitize(g.Last().SubjectName, 64)} — REVOCATO");
        DevicesList.ItemsSource = _devices.Select(d => $"{d.DisplayName} — {Core.Identity.DeviceIdentity.FormatFingerprint(d.SignPublicKey)}")
            .Concat(revoked)
            .ToList();
        PairedByText.Text = "";
    }

    private void RefreshCode()
    {
        var code = _model.Node?.ActivePairingCode;
        _codeTimer?.Dispose();
        if (code is null)
        {
            CodeText.Text = "";
            LcdCode.Text = "";
            return;
        }

        void Update()
        {
            var left = code.ExpiresAt - DateTimeOffset.UtcNow;
            var text = left > TimeSpan.Zero
                ? $"CODICE {code.Code[..3]} {code.Code[3..]}  ·  {left:m\\:ss}  ·  TENTATIVI {code.AttemptsLeft}"
                : "";
            CodeText.Text = text;
            LcdCode.Text = text;
        }

        Update();
        _codeTimer = DispatcherTimer.Run(() =>
        {
            Update();
            return _model.Node?.ActivePairingCode is not null;
        }, TimeSpan.FromSeconds(1));
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

    // ================================================================ utilità

    private PeerEndpoint? ParseAddress(string? text)
    {
        try
        {
            return PeerEndpoint.Parse(text ?? "");
        }
        catch (ArgumentException ex)
        {
            _model.Notifier?.Error(ex.Message);
            return null;
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
