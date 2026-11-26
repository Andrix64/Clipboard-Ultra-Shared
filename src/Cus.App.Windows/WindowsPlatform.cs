using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Cus.App.Shared;
using Cus.Core.Abstractions;
using Cus.Core.Hosting;
using Cus.Core.Node;
using Cus.Platform.Windows;

namespace Cus.App.Windows;

/// <summary>Le parti Windows dell'avvio: mutex del profilo, DPAPI, clipboard Win32, finestre e registro.</summary>
internal sealed class WindowsPlatform : PlatformHost
{
    public override IInstanceLock? TryAcquireInstance(string profile, string dataDirectory, Action onActivate)
    {
        var mutex = new Mutex(initiallyOwned: true, $@"Local\CUS-profile-{profile}", out var createdNew);
        var activateName = $@"Local\CUS-activate-{profile}";
        if (!createdNew)
        {
            mutex.Dispose();
            SignalRunningInstance(activateName);
            return null;
        }

        ListenForActivation(activateName, onActivate);
        return new ProfileMutex(mutex);
    }

    public override ISecretStore CreateSecretStore(string dataDirectory) => new DpapiSecretStore(dataDirectory);

    public override (IClipboardMonitor Monitor, IClipboardWriter Writer) CreateSystemClipboard(string dataDirectory)
    {
        var clipboard = new WindowsClipboard();
        return (clipboard, clipboard);
    }

    public override void MakeNonActivating(Window window) => NativeWindowStyle.MakeNonActivating(window);

    public override bool SupportsStartInTray => true;

    /// <summary>
    /// Avvio all'accesso a Windows: solo il profilo predefinito con la clipboard reale (le istanze di
    /// prova non devono partire da sole). Attivo di default; a ogni avvio si riallinea il registro
    /// all'impostazione, così un CUS spostato o aggiornato in un'altra cartella resta registrato giusto.
    /// </summary>
    public override IAutoStart? SetUpAutoStart(CusOptions options, CusNode node)
    {
        if (options.Profile != CusOptions.DefaultProfile || options.ClipboardMode != ClipboardMode.Real
            || Environment.ProcessPath is not { } exePath || !IsDeployedExecutable(exePath))
            return null;

        var autoStart = new RegistryAutoStart(exePath);
        try
        {
            if (node.LaunchAtLogin)
                autoStart.Enable();
            else
                autoStart.Disable();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Trace.WriteLine($"CUS: avvio automatico non aggiornato: {ex.Message}");
        }

        return autoStart;
    }

    /// <summary>
    /// L'eseguibile installato o del pacchetto portabile, non quello di una build di sviluppo
    /// (bin\Debug, bin\Release) né dotnet.exe con "dotnet run": registrarli farebbe partire
    /// all'accesso una copia che poi cambia o sparisce.
    /// </summary>
    private static bool IsDeployedExecutable(string path)
    {
        if (!string.Equals(Path.GetFileName(path), "cus.exe", StringComparison.OrdinalIgnoreCase))
            return false;
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var i = 0; i + 1 < segments.Length; i++)
        {
            if (string.Equals(segments[i], "bin", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(segments[i + 1], "Debug", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(segments[i + 1], "Release", StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        return true;
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

    /// <summary>Il segnale resta aperto finché l'app è aperta: lo tiene il thread che lo ascolta.</summary>
    private static void ListenForActivation(string eventName, Action onActivate)
    {
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        var thread = new Thread(() =>
        {
            while (signal.WaitOne())
                onActivate();
        })
        { IsBackground = true, Name = "CUS-activate" };
        thread.Start();
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>Il mutex del profilo. Lo chiude l'uscita del processo, come prima di questa classe.</summary>
    private sealed class ProfileMutex(Mutex mutex) : IInstanceLock
    {
        /// <summary>
        /// Rilascia il mutex ma ne tiene aperto l'handle: un nuovo avvio trova il profilo ancora aperto e
        /// chiede a questa istanza (che mostra l'errore) di portarsi in primo piano.
        /// </summary>
        public void ReleaseAfterFailedStart() => mutex.ReleaseMutex();

        public void Dispose()
        {
        }
    }
}
