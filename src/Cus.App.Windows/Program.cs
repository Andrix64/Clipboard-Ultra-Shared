using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Cus.Core.Abstractions;
using Cus.Core.Hosting;
using Cus.Core.Node;
using Cus.Core.Storage;
using Cus.Platform.Simulated;
using Cus.Platform.Windows;
using Cus.UI;

namespace Cus.App.Windows;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var model = Compose(args);

        AppBuilder.Configure(() => new Cus.UI.App { Model = model })
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args);

        return 0;
    }

    /// <summary>
    /// Composition root: l'unico punto in cui si scelgono le implementazioni (Windows reali o simulate).
    /// </summary>
    private static MainWindowModel Compose(string[] args)
    {
        CusOptions options;
        try
        {
            options = CusOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            return Failed("CUS — errore", ex.Message);
        }

        // Un profilo per volta: due istanze sullo stesso DB si contenderebbero identità e porta.
        var mutex = new Mutex(initiallyOwned: true, $@"Local\CUS-profile-{options.Profile}", out var createdNew);
        if (!createdNew)
            return Failed("CUS — errore", $"Il profilo '{options.Profile}' è già aperto in un'altra finestra di CUS.");

        var paths = new ProfileAppPaths(options.Profile);
        var info = new List<string>
        {
            $"Profilo: {options.Profile}",
            $"Cartella dati: {paths.DataDirectory}",
            $"Clipboard: {(options.ClipboardMode == ClipboardMode.Simulated ? "simulata" : "di sistema")}",
            $"Ascolto su: {options.BindAddress}",
            $"Scoperta automatica: {(options.EnableDiscovery ? "attiva" : "disattivata")}",
        };

        CusDatabase database;
        try
        {
            database = CusDatabase.Open(paths, new DpapiSecretStore(paths.DataDirectory));
        }
        catch (Exception ex)
        {
            mutex.ReleaseMutex();
            return Failed("CUS — errore", $"Impossibile aprire il database cifrato del profilo: {ex.Message}");
        }

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
            var windows = new WindowsClipboard();
            (monitor, writer) = (windows, windows);
            overlay = new AvaloniaOverlayIndicator(screens, NativeWindowStyle.MakeNonActivating);
        }

        var notifier = new AvaloniaNotifier(screens, $"CUS — {identity.DisplayName}", NativeWindowStyle.MakeNonActivating);
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
            mutex.ReleaseMutex();
            return Failed($"CUS — {options.Profile}", $"Avvio non riuscito: {ex.Message}");
        }

        info.Add($"Porta TCP: {node.Port}");

        return new MainWindowModel
        {
            Title = $"CUS — {identity.DisplayName}",
            InfoLines = info,
            Node = node,
            Notifier = notifier,
            Simulation = simulation,
            Shutdown = Shutdown,
        };
    }

    private static MainWindowModel Failed(string title, string error) => new()
    {
        Title = title,
        StartupError = error + Environment.NewLine + Environment.NewLine + CusOptions.Usage,
    };
}
