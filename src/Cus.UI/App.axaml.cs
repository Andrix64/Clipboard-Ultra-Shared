using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Cus.UI;

public partial class App : Application
{
    /// <summary>Impostato dal progetto eseguibile prima dell'avvio.</summary>
    public MainWindowModel? Model { get; init; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var model = Model ?? MainWindowModel.Empty;

            // Con il nodo avviato CUS vive nell'area di notifica (in basso a destra): chiudere la
            // finestra la nasconde, "Esci" dal menu chiude davvero. La chiusura dell'applicazione
            // segue quella della finestra principale, non delle altre (indicatore, notifiche).
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            if (model.Node is { } node)
            {
                var overlay = model.Overlay;
                MainWindow? window = null;
                var open = new NativeMenuItem(Loc.T("app.openCus"));
                open.Click += (_, _) => window?.ShowFromTray();

                // Gli stessi comandi dell'indicatore: nasconderlo o mostrarlo, file in uscita e in arrivo.
                var indicator = new NativeMenuItem(Loc.T("app.hideIndicator")) { IsEnabled = false };
                indicator.Click += (_, _) => overlay?.SetHidden(!overlay.IsHidden);
                var outgoing = new NativeMenuItem(Loc.T("app.outgoingFiles"));
                outgoing.Click += (_, _) => TransfersWindow.ShowFor(node);
                var incoming = new NativeMenuItem(Loc.T("app.incomingFiles"));
                incoming.Click += (_, _) => TransfersWindow.ShowFor(node, incoming: true);

                var exit = new NativeMenuItem(Loc.T("app.exit"));
                exit.Click += (_, _) => window?.ExitApplication();

                var menu = new NativeMenu { Items = { open, new NativeMenuItemSeparator() } };
                if (overlay is not null)
                    menu.Items.Add(indicator);
                menu.Items.Add(outgoing);
                menu.Items.Add(incoming);
                menu.Items.Add(new NativeMenuItemSeparator());
                menu.Items.Add(exit);

                var tray = new TrayIcon
                {
                    Icon = Skin.AppIcon,
                    ToolTipText = model.Title,
                    Menu = menu,
                };
                tray.Clicked += (_, _) => window?.ShowFromTray();
                TrayIcon.SetIcons(this, [tray]);

                window = new MainWindow(model with { HasTrayIcon = true });
                window.Closed += (_, _) => tray.IsVisible = false;

                void RefreshIndicatorItem()
                {
                    if (overlay is null)
                        return;
                    indicator.IsEnabled = overlay.IsActive;
                    indicator.Header = Loc.T(overlay.IsHidden ? "app.showIndicator" : "app.hideIndicator");
                }

                // Numerino sull'icona (barra delle applicazioni e area di notifica): utenti di CUS visibili
                // in rete. Gli annunci scadono senza avvisare, quindi si ricontrolla anche a intervalli.
                var lastVisible = -1;
                void RefreshVisibleCount()
                {
                    var visible = node.Peers.Where(p => p.IsAnnounced && p.DeviceId is not null).Select(p => p.DeviceId).Distinct().Count();
                    if (visible == lastVisible)
                        return;
                    lastVisible = visible;
                    var icon = AppIconBadge.For(visible);
                    tray.Icon = icon;
                    window.Icon = icon;
                    tray.ToolTipText = visible > 0 ? $"{model.Title}{Environment.NewLine}{Loc.T("app.visibleOnNetwork", visible)}" : model.Title;
                }

                if (overlay is not null)
                    overlay.IndicatorStateChanged += (_, _) => RefreshIndicatorItem();
                node.PeersChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshVisibleCount);
                var peersTimer = new Avalonia.Threading.DispatcherTimer(
                    TimeSpan.FromSeconds(3), Avalonia.Threading.DispatcherPriority.Background, (_, _) => RefreshVisibleCount());
                peersTimer.Start();
                window.Closed += (_, _) => peersTimer.Stop();
                RefreshVisibleCount();

                // Cambio di lingua dalle impostazioni: anche il menu dell'area di notifica.
                Loc.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    open.Header = Loc.T("app.openCus");
                    outgoing.Header = Loc.T("app.outgoingFiles");
                    incoming.Header = Loc.T("app.incomingFiles");
                    exit.Header = Loc.T("app.exit");
                    RefreshIndicatorItem();
                    lastVisible = -1;
                    RefreshVisibleCount();
                });
                if (model.StartInTray)
                {
                    // Avvio all'accesso a Windows: solo l'icona nell'area di notifica. Il lifetime mostra la
                    // finestra principale assegnata prima del suo avvio, quindi la si assegna subito dopo.
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => desktop.MainWindow = window);
                }
                else
                {
                    desktop.MainWindow = window;
                }
            }
            else
            {
                desktop.MainWindow = new MainWindow(model);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
