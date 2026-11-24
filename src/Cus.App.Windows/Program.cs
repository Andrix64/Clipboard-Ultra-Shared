using Avalonia;
using Cus.Core.Abstractions;
using Cus.Core.Hosting;
using Cus.Platform.Simulated;
using Cus.UI;

namespace Cus.App.Windows;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var model = BuildModel(args);

        AppBuilder.Configure(() => new Cus.UI.App { Model = model })
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args);

        return 0;
    }

    /// <summary>
    /// Composition root: sceglie le implementazioni in base alle opzioni. È l'unico punto in cui
    /// si decide tra servizi Windows reali e simulati.
    /// </summary>
    private static MainWindowModel BuildModel(string[] args)
    {
        CusOptions options;
        try
        {
            options = CusOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            return new MainWindowModel { Title = "CUS — errore", InfoLines = [ex.Message] };
        }

        var paths = new ProfileAppPaths(options.Profile);
        var info = new List<string>
        {
            $"Profilo: {options.Profile}",
            $"Cartella dati: {paths.DataDirectory}",
            $"Porta: {options.Port?.ToString() ?? $"prima libera tra {NetworkDefaults.DefaultPort} e {NetworkDefaults.PortRangeEnd}"}",
            $"Clipboard: {(options.ClipboardMode == ClipboardMode.Simulated ? "simulata" : "di sistema")}",
            $"Peer manuali: {(options.Peers.Count == 0 ? "nessuno" : string.Join(", ", options.Peers))}",
        };

        IClipboardMonitor? monitor = null;
        Action<string>? simulateCopy = null;

        if (options.ClipboardMode == ClipboardMode.Simulated)
        {
            var clipboard = new SimulatedClipboard();
            monitor = clipboard;
            simulateCopy = text => clipboard.Copy(ClipboardContent.FromText(text));
        }
        else
        {
            // La clipboard Windows reale arriva in fase 1 (PLAN.md).
            info.Add("La clipboard di sistema non è ancora implementata: avvia con --clipboard simulated.");
        }

        return new MainWindowModel
        {
            Title = $"CUS — {options.Profile}",
            InfoLines = info,
            Clipboard = monitor,
            SimulateCopy = simulateCopy,
        };
    }
}
