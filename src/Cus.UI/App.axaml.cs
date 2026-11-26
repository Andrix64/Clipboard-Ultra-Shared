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
            if (model.Node is not null)
            {
                MainWindow? window = null;
                var open = new NativeMenuItem(Loc.T("app.openCus"));
                open.Click += (_, _) => window?.ShowFromTray();
                var exit = new NativeMenuItem(Loc.T("app.exit"));
                // Cambio di lingua dalle impostazioni: anche il menu dell'area di notifica.
                Loc.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    open.Header = Loc.T("app.openCus");
                    exit.Header = Loc.T("app.exit");
                });
                exit.Click += (_, _) => window?.ExitApplication();
                var tray = new TrayIcon
                {
                    Icon = Skin.AppIcon,
                    ToolTipText = model.Title,
                    Menu = new NativeMenu { Items = { open, new NativeMenuItemSeparator(), exit } },
                };
                tray.Clicked += (_, _) => window?.ShowFromTray();
                TrayIcon.SetIcons(this, [tray]);

                window = new MainWindow(model with { HasTrayIcon = true });
                window.Closed += (_, _) => tray.IsVisible = false;
                desktop.MainWindow = window;
            }
            else
            {
                desktop.MainWindow = new MainWindow(model);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
