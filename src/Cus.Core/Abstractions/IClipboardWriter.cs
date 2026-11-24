namespace Cus.Core.Abstractions;

/// <summary>
/// Scrive nella clipboard un contenuto ricevuto da un peer. Come su un sistema reale, la scrittura
/// fa scattare il monitor: evitare il rimbalzo verso la sessione è compito del nucleo.
/// </summary>
public interface IClipboardWriter
{
    Task WriteAsync(ClipboardContent content, CancellationToken cancellationToken = default);
}
