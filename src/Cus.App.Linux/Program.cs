using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Cus.Core.Abstractions;
using Cus.Core.Hosting;
using Cus.Core.Node;
using Cus.Core.Storage;
using Cus.Platform.Linux;
using Cus.Platform.Simulated;
using Cus.UI;

[assembly: SupportedOSPlatform("linux")]

namespace Cus.App.Linux;

internal static class Program
{
    private static SingleInstance? _instance;

    public static int Main(string[] args)
    {
        // Prima di tutto la lingua predefinita (cus.ini accanto all'eseguibile, altrimenti $LANG):
        // serve già per gli errori d'avvio. Quella scelta dall'utente si applica appena il database è aperto.
        Loc.Current = Loc.DefaultLanguage(AppContext.BaseDirectory);

        var model = Compose(args);
        if (model is null)
            return 0; // Già aperto: la prima istanza è stata portata in primo piano, questa esce in silenzio.

        try
        {
            AppBuilder.Configure(() => new Cus.UI.App { Model = model })
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace()
                .StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            _instance?.Dispose();
        }

        return 0;
    }

    /// <summary>
    /// Composition root: l'unico punto in cui si scelgono le implementazioni (Linux reali o simulate).
    /// Stessa struttura di Cus.App.Windows; cambiano clipboard, segreti, finestre e istanza singola.
    /// </summary>
    private static MainWindowModel? Compose(string[] args)
    {
        CusOptions options;
        try
        {
            options = CusOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            return Failed(Loc.T("startup.cusError"), ex.Message);
        }

        var paths = new ProfileAppPaths(options.Profile);

        // Un profilo per volta: due istanze sullo stesso DB si contenderebbero identità e porta.
        SingleInstance? instance;
        try
        {
            instance = SingleInstance.TryAcquire(paths.DataDirectory, options.Profile, ShowMainWindow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed(Loc.T("startup.cusError"), ex.Message);
        }

        if (instance is null)
            return null;
        _instance = instance;

        int? port = null;
        IReadOnlyList<string> Info()
        {
            var lines = new List<string>
            {
                Loc.T("startup.profile", options.Profile),
                Loc.T("startup.dataFolder", paths.DataDirectory),
                options.ClipboardMode == ClipboardMode.Simulated
                    ? Loc.T("startup.clipboardSimulated")
                    : Loc.T("startup.clipboardSystem"),
                Loc.T("startup.listeningOn", options.BindAddress),
                options.EnableDiscovery
                    ? Loc.T("startup.automaticDiscoveryOn")
                    : Loc.T("startup.automaticDiscoveryOff"),
            };
            if (port is { } p)
                lines.Add(Loc.T("startup.tcpPort", p));
            return lines;
        }

        CusDatabase database;
        try
        {
            database = CusDatabase.Open(paths, new SecretServiceStore(paths.DataDirectory));
        }
        catch (Exception ex)
        {
            ReleaseInstance();
            return Failed(Loc.T("startup.cusError"), Loc.T("startup.cannotOpenTheProfileSEncrypted", ex.Message));
        }

        // Lingua: quella scelta nelle impostazioni del profilo; se mai scelta, quella predefinita.
        if (Loc.Parse(database.GetSetting(Loc.SettingKey)) is { } saved)
            Loc.Current = saved;

        var defaultName = options.Profile == CusOptions.DefaultProfile ? Environment.MachineName : $"{Environment.MachineName} ({options.Profile})";
        var identity = CusNode.LoadOrCreateIdentity(database, options.DisplayName ?? defaultName, Environment.UserName);
        if (options.DisplayName is { } name && name != identity.DisplayName)
        {
            identity = identity.WithDisplayName(name);
            database.SaveIdentity(identity);
        }

        Func<Screens?> screens = () =>
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Screens;

        IClipboardMonitor monitor;
        IClipboardWriter writer;
        IOverlayIndicator overlay;
        ISimulationControls? simulation = null;
        if (options.ClipboardMode == ClipboardMode.Simulated)
        {
            // Più istanze sullo stesso PC: clipboard virtuale e indicatore solo nella finestra,
            // per non sovrapporre gli indicatori reali di istanze diverse.
            var simulated = new SimulatedClipboard();
            (monitor, writer, overlay) = (simulated, simulated, new RecordingOverlayIndicator());
            simulation = new SimulationControls(simulated);
        }
        else
        {
            X11Clipboard x11;
            try
            {
                // I file ricevuti si scaricano qui all'incolla; la cartella si svuota all'avvio e alla chiusura.
                x11 = new X11Clipboard(Path.Combine(paths.DataDirectory, "received-files"));
            }
            catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
            {
                database.Dispose();
                ReleaseInstance();
                return Failed(Loc.T("startup.cusError"), Loc.T("startup.systemClipboardUnavailable", ex.Message));
            }

            (monitor, writer) = (x11, x11);
            overlay = new AvaloniaOverlayIndicator(screens, MakeNonActivating);
        }

        var notifier = new AvaloniaNotifier(screens, $"CUS — {identity.DisplayName}", MakeNonActivating);
        var node = new CusNode(
            new CusNodeOptions
            {
                Port = options.Port,
                BindAddress = options.BindAddress,
                EnableDiscovery = options.EnableDiscovery,
                ManualPeers = options.Peers,
            },
            database, identity, monitor, writer, overlay, new AvaloniaConsentPrompt(), notifier);

        // Idempotente: chiusura della finestra, riavvio con nuova identità e avvio fallito possono
        // chiederlo più volte, ma nodo e database si chiudono una volta sola. Gira sul thread pool:
        // chi aspetta dal thread della UI non rischia deadlock.
        var shutdownLock = new object();
        Task? shutdownTask = null;
        Task Shutdown()
        {
            lock (shutdownLock)
                return shutdownTask ??= Task.Run(async () =>
                {
                    await node.DisposeAsync();
                    monitor.Dispose();
                    overlay.Dispose();
                    database.Dispose();
                });
        }

        try
        {
            node.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Shutdown().GetAwaiter().GetResult();
            ReleaseInstance();
            return Failed($"CUS — {options.Profile}", Loc.T("startup.startupFailed", ex.Message));
        }

        port = node.Port;

        return new MainWindowModel
        {
            Title = $"CUS — {identity.DisplayName}",
            InfoLines = Info,
            Node = node,
            Notifier = notifier,
            Simulation = simulation,
            Shutdown = Shutdown,
        };
    }

    /// <summary>Su X11 l'handle di piattaforma è l'id della finestra.</summary>
    private static void MakeNonActivating(Window window)
    {
        if (window.TryGetPlatformHandle() is { HandleDescriptor: "XID", Handle: var handle })
            X11WindowStyle.MakeNonActivating(handle);
    }

    private static void ShowMainWindow() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if ((Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is Cus.UI.MainWindow window)
                window.ShowFromTray();
        });

    private static void ReleaseInstance()
    {
        _instance?.Dispose();
        _instance = null;
    }

    private static MainWindowModel Failed(string title, string error) => new()
    {
        Title = title,
        StartupError = error + Environment.NewLine + Environment.NewLine + CusOptions.Usage,
    };
}
