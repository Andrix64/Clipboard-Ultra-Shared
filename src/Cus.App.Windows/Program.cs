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
    private static Mutex? _profileMutex;
    private static EventWaitHandle? _activateSignal;

    [STAThread]
    public static int Main(string[] args)
    {
        // Prima di tutto la lingua predefinita (installer, altrimenti Windows): serve già per gli errori d'avvio.
        // Quella scelta dall'utente, salvata nel profilo, si applica appena il database è aperto.
        Loc.Current = Loc.DefaultLanguage(AppContext.BaseDirectory);

        var model = Compose(args);
        if (model is null)
            return 0; // Già aperto: la prima istanza è stata portata in primo piano, questa esce in silenzio.

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

        // Un profilo per volta: due istanze sullo stesso DB si contenderebbero identità e porta.
        var mutex = new Mutex(initiallyOwned: true, $@"Local\CUS-profile-{options.Profile}", out var createdNew);
        var activateName = $@"Local\CUS-activate-{options.Profile}";
        if (!createdNew)
        {
            mutex.Dispose();
            SignalRunningInstance(activateName);
            return null;
        }

        // Campo statico: con una sola variabile locale il GC finalizzerebbe il mutex a Compose finito,
        // la maniglia si chiuderebbe e un secondo avvio risulterebbe "primo".
        _profileMutex = mutex;
        ListenForActivation(activateName);

        var paths = new ProfileAppPaths(options.Profile);
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
            database = CusDatabase.Open(paths, new DpapiSecretStore(paths.DataDirectory));
        }
        catch (Exception ex)
        {
            mutex.ReleaseMutex();
            return Failed(Loc.T("startup.cusError"), Loc.T("startup.cannotOpenTheProfileSEncrypted", ex.Message));
        }

        // Lingua: quella scelta nelle impostazioni del profilo; se mai scelta, quella dell'installer.
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

    /// <summary>Chiede alla prima istanza di portarsi in primo piano. Il secondo avvio ha appena ricevuto
    /// il diritto di primo piano dall'utente e lo cede a tutti, altrimenti Windows lampeggia solo la barra.</summary>
    private static void SignalRunningInstance(string eventName)
    {
        try
        {
            AllowSetForegroundWindow(-1);
            using var signal = EventWaitHandle.OpenExisting(eventName);
            signal.Set();
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
        {
            // La prima istanza si sta chiudendo: niente da fare, nessun avviso.
        }
    }

    private static void ListenForActivation(string eventName)
    {
        _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        var thread = new Thread(() =>
        {
            while (_activateSignal.WaitOne())
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if ((Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is Cus.UI.MainWindow window)
                        window.ShowFromTray();
                });
            }
        })
        { IsBackground = true, Name = "CUS-activate" };
        thread.Start();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    private static MainWindowModel Failed(string title, string error) => new()
    {
        Title = title,
        StartupError = error + Environment.NewLine + Environment.NewLine + CusOptions.Usage,
    };
}
