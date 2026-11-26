using Avalonia.Controls;
using Cus.Core.Abstractions;
using Cus.Core.Hosting;
using Cus.Core.Node;

namespace Cus.App.Shared;

/// <summary>
/// Le parti dell'avvio che cambiano da un sistema operativo all'altro. Tutto il resto (opzioni, profilo,
/// database, identità, nodo, chiusura) è uguale ovunque e sta in <see cref="AppHost"/>.
/// </summary>
public abstract class PlatformHost
{
    /// <summary>
    /// Un profilo per volta: due istanze sullo stesso database si contenderebbero identità e porta.
    /// Null se il profilo è già aperto: la prima istanza è stata avvisata di mostrarsi.
    /// </summary>
    /// <param name="onActivate">Da chiamare, su qualunque thread, a ogni secondo avvio dello stesso profilo.</param>
    /// <exception cref="IOException">Il profilo non si può bloccare (l'avvio mostra l'errore).</exception>
    /// <exception cref="UnauthorizedAccessException">Come sopra.</exception>
    public abstract IInstanceLock? TryAcquireInstance(string profile, string dataDirectory, Action onActivate);

    /// <summary>Dove si custodisce la chiave del database del profilo.</summary>
    public abstract ISecretStore CreateSecretStore(string dataDirectory);

    /// <summary>La clipboard del sistema, per le istanze con <see cref="ClipboardMode.Real"/>.</summary>
    /// <exception cref="ClipboardUnavailableException">La clipboard di sistema non è utilizzabile.</exception>
    public abstract (IClipboardMonitor Monitor, IClipboardWriter Writer) CreateSystemClipboard(string dataDirectory);

    /// <summary>Indicatore e notifiche non devono rubare il focus a chi sta scrivendo altrove.</summary>
    public abstract void MakeNonActivating(Window window);

    /// <summary>Riallinea l'avvio automatico all'impostazione, a nodo avviato. Null dove non è previsto.</summary>
    public virtual IAutoStart? SetUpAutoStart(CusOptions options, CusNode node) => null;

    /// <summary>L'opzione <c>--tray</c> (partire nell'area di notifica) vale su questa piattaforma.</summary>
    public virtual bool SupportsStartInTray => false;
}

/// <summary>Il profilo tenuto da questa istanza, fino alla chiusura dell'app.</summary>
public interface IInstanceLock : IDisposable
{
    /// <summary>L'avvio è fallito dopo aver preso il profilo: ogni piattaforma decide cosa ne resta.</summary>
    void ReleaseAfterFailedStart();
}

/// <summary>La clipboard di sistema non è utilizzabile (per esempio niente sessione grafica): l'avvio mostra il motivo.</summary>
public sealed class ClipboardUnavailableException(string message, Exception innerException) : Exception(message, innerException);
