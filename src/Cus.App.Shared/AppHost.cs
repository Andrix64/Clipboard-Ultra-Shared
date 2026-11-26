using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Cus.Core.Abstractions;
using Cus.Core.Hosting;
using Cus.Core.Node;
using Cus.Core.Storage;
using Cus.Platform.Simulated;
using Cus.UI;

namespace Cus.App.Shared;

/// <summary>
/// Composition root comune agli eseguibili: l'unico punto in cui si scelgono le implementazioni
/// (di piattaforma, fornite da <see cref="PlatformHost"/>, oppure simulate) e si avvia la UI.
/// </summary>
public static class AppHost
{
    /// <summary>
    /// Campo statico: tenuto solo in una variabile locale, il lock del profilo (su Windows un mutex) verrebbe
    /// finalizzato dal GC a composizione finita e un secondo avvio risulterebbe "primo".
    /// </summary>
    private static IInstanceLock? s_instance;

    /// <summary>Avvia CUS e restituisce il codice d'uscita del processo.</summary>
    public static int Run(string[] args, PlatformHost platform)
    {
        // Prima di tutto la lingua predefinita (quella dell'installer o di cus.ini, altrimenti quella del
        // sistema): serve già per gli errori d'avvio. Quella scelta dall'utente si applica appena il
        // database è aperto.
        Loc.Current = Loc.DefaultLanguage(AppContext.BaseDirectory);

        var model = Compose(args, platform);
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
            s_instance?.Dispose();
        }

        return 0;
    }

    /// <summary>Porta in primo piano la finestra principale (secondo avvio dello stesso profilo).</summary>
    public static void ShowMainWindow() =>
        Dispatcher.UIThread.Post(() =>
        {
            if ((Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is MainWindow window)
                window.ShowFromTray();
        });

    /// <returns>Null se il profilo è già aperto in un'altra istanza.</returns>
    private static MainWindowModel? Compose(string[] args, PlatformHost platform)
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
        IInstanceLock? instance;
        try
        {
            instance = platform.TryAcquireInstance(options.Profile, paths.DataDirectory, ShowMainWindow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed(Loc.T("startup.cusError"), ex.Message);
        }

        if (instance is null)
            return null;
        s_instance = instance;

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
            database = CusDatabase.Open(paths, platform.CreateSecretStore(paths.DataDirectory));
        }
        catch (Exception ex)
        {
            instance.ReleaseAfterFailedStart();
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
            try
            {
                (monitor, writer) = platform.CreateSystemClipboard(paths.DataDirectory);
            }
            catch (ClipboardUnavailableException ex)
            {
                database.Dispose();
                instance.ReleaseAfterFailedStart();
                return Failed(Loc.T("startup.cusError"), Loc.T("startup.systemClipboardUnavailable", ex.Message));
            }

            overlay = new AvaloniaOverlayIndicator(screens, platform.MakeNonActivating);
        }

        var notifier = new AvaloniaNotifier(screens, $"CUS — {identity.DisplayName}", platform.MakeNonActivating);
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
            instance.ReleaseAfterFailedStart();
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
            AutoStart = platform.SetUpAutoStart(options, node),
            StartInTray = platform.SupportsStartInTray && options.StartInTray,
        };
    }

    private static MainWindowModel Failed(string title, string error) => new()
    {
        Title = title,
        StartupError = error + Environment.NewLine + Environment.NewLine + CusOptions.Usage,
    };
}
