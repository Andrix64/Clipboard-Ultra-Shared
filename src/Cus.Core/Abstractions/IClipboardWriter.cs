namespace Cus.Core.Abstractions;

/// <summary>
/// Scrive nella clipboard un contenuto ricevuto da un peer. Come su un sistema reale, la scrittura
/// fa scattare il monitor, con <see cref="ClipboardChangedEventArgs.IsOwnWrite"/> a true.
/// </summary>
public interface IClipboardWriter
{
    /// <param name="content">Contenuto da scrivere.</param>
    /// <param name="files">
    /// Per i file: sorgente da cui leggere il contenuto solo quando l'utente incolla (lazy).
    /// </param>
    Task WriteAsync(ClipboardContent content, IRemoteFileSource? files = null, CancellationToken cancellationToken = default);
}

/// <summary>Legge su richiesta i byte di un file che sta su un altro PC.</summary>
public interface IRemoteFileSource
{
    /// <exception cref="RemoteFileUnavailableException">Il PC d'origine non è disponibile o ha copiato altro.</exception>
    Task<byte[]> ReadAsync(int fileIndex, long offset, int count, CancellationToken cancellationToken = default);
}

public sealed class RemoteFileUnavailableException(string message) : Exception(message);
